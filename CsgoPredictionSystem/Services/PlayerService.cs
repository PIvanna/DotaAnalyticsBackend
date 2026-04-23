using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Models;
using CsgoPredictionSystem.DTO;
using CsgoPredictionSystem.Helpers;
using CsgoPredictionSystem.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace CsgoPredictionSystem.Services;

public class PlayerService
{
    private readonly DotaDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHubContext<SyncHub> _hubContext; 
    private readonly ILogger<PlayerService> _logger;

    public PlayerService(DotaDbContext context, IHttpClientFactory httpClientFactory, IHubContext<SyncHub> hubContext, ILogger<PlayerService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    private async Task Notify(string message, int progress)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new
        {
            syncType = "Players",
            status = $"{progress}% - {message}",
            lastRunAt = DateTime.UtcNow
        });
    }

    public async Task<string> SyncPlayersFromApi(int count, int daysInactive, bool mustHaveTeam)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try 
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "DotaAnalyticsApp");

            await Notify("Request to OpenDota API...", 10);
            var response = await client.GetStringAsync("https://api.opendota.com/api/proPlayers");
            var allPlayers = JsonConvert.DeserializeObject<List<PlayerApiDto>>(response);

            if (allPlayers == null || !allPlayers.Any()) return "Data not received.";

            var thresholdDate = DateTime.UtcNow.AddDays(-daysInactive);

            await Notify("Data filtering and preparation...", 30);
            var playersToProcess = allPlayers
                .Where(p => p.LastMatchTime.HasValue && p.LastMatchTime >= thresholdDate) 
                .Where(p => !mustHaveTeam || (p.TeamId.HasValue && p.TeamId > 0))        
                .OrderByDescending(p => p.LastMatchTime)                                
                .Take(count)                                                            
                .ToList();

            if (!playersToProcess.Any()) return "No player meets the criteria.";

            var playerIds = playersToProcess.Select(p => p.AccountId).ToList();
            var existingPlayersMap = await _context.Players
                .Where(p => playerIds.Contains(p.SteamId))
                .ToDictionaryAsync(p => p.SteamId, p => p);

            var teamsMap = await _context.Teams
                .Where(t => t.ExternalId.HasValue)
                .ToDictionaryAsync(t => t.ExternalId!.Value, t => t.TeamId);

            int added = 0, updated = 0;
            await Notify("Saving to database...", 60);

            foreach (var p in playersToProcess)
            {
                existingPlayersMap.TryGetValue(p.AccountId, out var existing);
                int? teamIdInDb = (p.TeamId.HasValue && teamsMap.TryGetValue(p.TeamId.Value, out var tId)) ? tId : null;

                if (existing == null)
                {
                    _context.Players.Add(new Player {
                        SteamId = p.AccountId,
                        Nickname = p.Name ?? "Unknown",
                        Country = p.CountryCode,
                        PhotoPath = p.Avatar,
                        TeamId = teamIdInDb,
                        LastMatchTime = p.LastMatchTime
                    });
                    added++;
                }
                else
                {
                    existing.Nickname = p.Name ?? existing.Nickname;
                    existing.PhotoPath = p.Avatar ?? existing.PhotoPath;
                    existing.Country = p.CountryCode ?? existing.Country;
                    existing.TeamId = teamIdInDb ?? existing.TeamId;
                    existing.LastMatchTime = p.LastMatchTime ?? existing.LastMatchTime;
                    updated++;
                }
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync(); 

            await Notify("Player synchronization is complete!", 100);
            return $"Added: {added}, Updated: {updated}.";
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Error synchronizing players");
            
            string friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            await Notify(friendlyError, 0);
            
            throw;
        }
    }    

 
    public async Task<object> GetPlayerDashboardAsync()
    {
        var totalPlayers = await _context.Players.CountAsync();
 
        var countryDistribution = await _context.Players
            .Where(p => p.Country != null)
            .GroupBy(p => p.Country)
            .Select(g => new { name = g.Key!, value = g.Count() })
            .OrderByDescending(x => x.value)
            .Take(10)
            .ToListAsync();
        
        var gpmRaw = await _context.PlayerMatchStats
            .GroupBy(s => new { s.PlayerId, s.Player.Nickname })
            .Select(g => new
            {
                g.Key.PlayerId,
                g.Key.Nickname,
                totalGpm   = g.Sum(s => (long)s.Gpm),
                matchCount = g.Count()
            })
            .Where(x => x.matchCount >= 5) 
            .OrderByDescending(x => x.totalGpm)
            .Take(10)
            .ToListAsync();
 
        var topGpm = gpmRaw.Select(x => new
        {
            name    = x.Nickname,
            avgGpm  = Math.Round((double)x.totalGpm / x.matchCount, 1),
            matches = x.matchCount
        }).OrderByDescending(x => x.avgGpm).ToList();
 
        var kdaRaw = await _context.PlayerMatchStats
            .GroupBy(s => new { s.PlayerId, s.Player.Nickname })
            .Select(g => new
            {
                g.Key.Nickname,
                totalKills   = g.Sum(s => (long)s.Kills),
                totalDeaths  = g.Sum(s => (long)s.Deaths),
                totalAssists = g.Sum(s => (long)s.Assists),
                matchCount   = g.Count()
            })
            .Where(x => x.matchCount >= 5)
            .ToListAsync();
 
        var topKda = kdaRaw.Select(x => new
        {
            name    = x.Nickname,
            kda     = Math.Round((double)(x.totalKills + x.totalAssists) / Math.Max(1, x.totalDeaths), 2),
            matches = x.matchCount
        }).OrderByDescending(x => x.kda).Take(10).ToList();
 
        var mostActiveRaw = await _context.PlayerMatchStats
            .GroupBy(s => new { s.PlayerId, s.Player.Nickname, s.Player.PhotoPath })
            .Select(g => new
            {
                g.Key.Nickname,
                g.Key.PhotoPath,
                matches = g.Count()
            })
            .OrderByDescending(x => x.matches)
            .FirstOrDefaultAsync();
 
        var roleDistribution = await _context.PlayerMatchStats
            .Join(_context.HeroRoles, s => s.HeroId, hr => hr.HeroId,
                  (s, hr) => new { hr.Role.RoleName })
            .GroupBy(x => x.RoleName)
            .Select(g => new { name = g.Key, value = g.Count() })
            .OrderByDescending(x => x.value)
            .ToListAsync();
 
        var since14 = DateTime.UtcNow.AddDays(-14).Date;
        var activityRaw = await _context.PlayerMatchStats
            .Where(s => s.Match.MatchDate >= since14)
            .GroupBy(s => s.Match.MatchDate.Date)
            .Select(g => new { date = g.Key, uniquePlayers = g.Select(x => x.PlayerId).Distinct().Count() })
            .OrderBy(g => g.date)
            .ToListAsync();
 
        var activityTrend = Enumerable.Range(0, 14)
            .Select(i => since14.AddDays(i))
            .Select(d => new
            {
                date          = d.ToString("dd MMM"),
                uniquePlayers = activityRaw.FirstOrDefault(x => x.date == d)?.uniquePlayers ?? 0
            })
            .ToList();
 
        var withTeam    = await _context.Players.CountAsync(p => p.TeamId != null);
        var withoutTeam = totalPlayers - withTeam;
 
        return new
        {
            summary = new
            {
                totalPlayers,
                withTeam,
                withoutTeam,
                countriesCount = countryDistribution.Count
            },
            topGpm,
            topKda,
            mostActive     = mostActiveRaw,
            countryDistribution,
            roleDistribution,
            activityTrend
        };
    }
    
 
    public async Task<object> GetPlayersPagedAsync(
        int page, int pageSize,
        string? search, int? teamId,
        string? country, string sortBy = "nickname")
    {
        var query = _context.Players.AsQueryable();
 
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Nickname.ToLower().Contains(search.ToLower()));
 
        if (teamId.HasValue)
            query = query.Where(p => p.TeamId == teamId);
 
        if (!string.IsNullOrWhiteSpace(country))
            query = query.Where(p => p.Country == country);
 
        query = sortBy.ToLower() switch
        {
            "activity" => query.OrderByDescending(p => p.LastMatchTime),
            _          => query.OrderBy(p => p.Nickname)
        };
 
        var total = await query.CountAsync();
 
        var playerIds = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.PlayerId,
                p.Nickname,
                p.Country,
                p.PhotoPath,
                p.LastMatchTime,
                TeamName = p.Team != null ? p.Team.TeamName : null,
                TeamLogo = p.Team != null ? p.Team.LogoPath : null
            })
            .ToListAsync();
 
        var ids = playerIds.Select(p => p.PlayerId).ToList();
 
        var statsRaw = await _context.PlayerMatchStats
            .Where(s => ids.Contains(s.PlayerId))
            .GroupBy(s => s.PlayerId)
            .Select(g => new
            {
                PlayerId     = g.Key,
                totalGpm     = g.Sum(s => (long)s.Gpm),
                totalKills   = g.Sum(s => (long)s.Kills),
                totalDeaths  = g.Sum(s => (long)s.Deaths),
                totalAssists = g.Sum(s => (long)s.Assists),
                matchCount   = g.Count()
            })
            .ToListAsync();
 
        var winnerMatchTeams = await _context.MatchTeams
            .Where(mt => mt.IsWinner)
            .Select(mt => new { mt.MatchId, mt.TeamId })
            .ToListAsync();
 
        var wonSet = winnerMatchTeams.Select(x => (x.MatchId, x.TeamId)).ToHashSet();
 
        var playerWins = await _context.PlayerMatchStats
            .Where(s => ids.Contains(s.PlayerId))
            .Select(s => new { s.PlayerId, s.MatchId, s.TeamId })
            .ToListAsync();
 
        var winsByPlayer = playerWins
            .GroupBy(x => x.PlayerId)
            .ToDictionary(
                g => g.Key,
                g => g.Count(x => wonSet.Contains((x.MatchId, x.TeamId ?? 0)))
            );
 
        var statsMap = statsRaw.ToDictionary(s => s.PlayerId);
 
        var items = playerIds.Select(p =>
        {
            statsMap.TryGetValue(p.PlayerId, out var s);
            int matches  = s?.matchCount ?? 0;
            double avgGpm = matches > 0 ? Math.Round((double)(s!.totalGpm) / matches, 1) : 0;
            double kda    = matches > 0
                ? Math.Round((double)(s!.totalKills + s.totalAssists) / Math.Max(1, s.totalDeaths), 2)
                : 0;
            winsByPlayer.TryGetValue(p.PlayerId, out int wins);
            double winRate = matches > 0 ? Math.Round((double)wins / matches * 100, 1) : 0;
 
            return new
            {
                p.PlayerId,
                p.Nickname,
                p.Country,
                p.PhotoPath,
                p.TeamName,
                p.TeamLogo,
                lastMatchDate = p.LastMatchTime,
                totalMatches  = matches,
                avgGpm,
                kda,
                winRate
            };
        })
        .OrderByDescending(p => sortBy.ToLower() switch
        {
            "gpm"  => p.avgGpm,
            "kda"  => p.kda,
            "wins" => (double)p.winRate,
            _      => 0.0
        })
        .ToList();
 

        var finalItems = sortBy.ToLower() is "gpm" or "kda" or "wins"
            ? items.Cast<object>().ToList()
            : items.OrderBy(p => p.Nickname).Cast<object>().ToList();
 
        return new { total, page, pageSize, items = finalItems };
    }
    
    public async Task<object?> GetPlayerDetailsAsync(int id)
    {
        var player = await _context.Players
            .Include(p => p.Team)
            .FirstOrDefaultAsync(p => p.PlayerId == id);
 
        if (player == null) return null;
 
        var statsRaw = await _context.PlayerMatchStats
            .Where(s => s.PlayerId == id)
            .Select(s => new
            {
                s.MatchId,
                s.TeamId,
                s.HeroId,
                HeroName = s.Hero.LocalizedName,
                s.Kills,
                s.Deaths,
                s.Assists,
                s.Gpm,
                s.Xpm,
                s.NetWorth,
                s.HeroDamage,
                s.TowerDamage,
                s.LastHits,
                s.Denies,
                MatchDate = s.Match.MatchDate
            })
            .ToListAsync();
 
        if (!statsRaw.Any())
        {
            return new
            {
                info = BuildPlayerInfo(player, 0, 0, 0, 0),
                performance = new { avgGpm = 0.0, avgXpm = 0.0, avgKills = 0.0, avgDeaths = 0.0, avgAssists = 0.0, kda = 0.0, totalMatches = 0, winRate = 0.0 },
                signatureHeroes = new List<object>(),
                recentMatches   = new List<object>(),
                formStreak      = new List<string>()
            };
        }
 
        int totalMatches = statsRaw.Count;
 
        var matchIds = statsRaw.Select(s => s.MatchId).Distinct().ToList();
        var wonMatchTeams = await _context.MatchTeams
            .Where(mt => matchIds.Contains(mt.MatchId) && mt.IsWinner)
            .Select(mt => new { mt.MatchId, mt.TeamId })
            .ToListAsync();
        var wonSet = wonMatchTeams.Select(x => (x.MatchId, x.TeamId)).ToHashSet();
 
        int wins    = statsRaw.Count(s => wonSet.Contains((s.MatchId, s.TeamId ?? 0)));
        double winRate = Math.Round((double)wins / totalMatches * 100, 1);
 
        double totalKills   = statsRaw.Sum(s => s.Kills);
        double totalDeaths  = statsRaw.Sum(s => s.Deaths);
        double totalAssists = statsRaw.Sum(s => s.Assists);
        double kda          = Math.Round((totalKills + totalAssists) / Math.Max(1, totalDeaths), 2);
 
        var performance = new
        {
            avgGpm       = Math.Round(statsRaw.Average(s => (double)s.Gpm), 1),
            avgXpm       = Math.Round(statsRaw.Average(s => (double)s.Xpm), 1),
            avgKills     = Math.Round(statsRaw.Average(s => (double)s.Kills), 1),
            avgDeaths    = Math.Round(statsRaw.Average(s => (double)s.Deaths), 1),
            avgAssists   = Math.Round(statsRaw.Average(s => (double)s.Assists), 1),
            avgHeroDmg   = Math.Round(statsRaw.Average(s => (double)s.HeroDamage), 0),
            avgTowerDmg  = Math.Round(statsRaw.Average(s => (double)s.TowerDamage), 0),
            avgNetWorth  = Math.Round(statsRaw.Average(s => (double)s.NetWorth), 0),
            avgLastHits  = Math.Round(statsRaw.Average(s => (double)s.LastHits), 1),
            kda,
            totalMatches,
            wins,
            winRate
        };
 

        var signatureHeroes = statsRaw
            .GroupBy(s => s.HeroName)
            .Select(g =>
            {
                int games    = g.Count();
                int heroWins = g.Count(s => wonSet.Contains((s.MatchId, s.TeamId ?? 0)));
                double avgK  = Math.Round(g.Average(s => (double)s.Kills), 1);
                double avgD  = Math.Round(g.Average(s => (double)s.Deaths), 1);
                double avgA  = Math.Round(g.Average(s => (double)s.Assists), 1);
                double heroKda = Math.Round((g.Sum(s => (double)(s.Kills + s.Assists))) / Math.Max(1, g.Sum(s => (double)s.Deaths)), 2);
                return new
                {
                    name    = g.Key,
                    games,
                    wins    = heroWins,
                    winRate = games > 0 ? Math.Round((double)heroWins / games * 100, 1) : 0.0,
                    avgGpm  = Math.Round(g.Average(s => (double)s.Gpm), 1),
                    avgKda  = heroKda,
                    avgKills   = avgK,
                    avgDeaths  = avgD,
                    avgAssists = avgA
                };
            })
            .Where(x => x.games >= 3)
            .OrderByDescending(x => x.games)
            .Take(8)
            .ToList();
 
        var recent = statsRaw
            .OrderByDescending(s => s.MatchDate)
            .Take(10)
            .ToList();
 
        var recentMatchIds = recent.Select(s => s.MatchId).ToList();
        var matchTeamsRaw  = await _context.MatchTeams
            .Where(mt => recentMatchIds.Contains(mt.MatchId))
            .Select(mt => new { mt.MatchId, mt.TeamId, mt.Team.TeamName, mt.IsWinner, mt.Side })
            .ToListAsync();
 
        var recentMatches = recent.Select(s =>
        {
            bool isWin = wonSet.Contains((s.MatchId, s.TeamId ?? 0));
            var myTeam = matchTeamsRaw.FirstOrDefault(mt => mt.MatchId == s.MatchId && mt.TeamId == s.TeamId);
            var oppTeam = matchTeamsRaw.FirstOrDefault(mt => mt.MatchId == s.MatchId && mt.TeamId != s.TeamId);
            return new
            {
                s.MatchId,
                matchDate      = s.MatchDate,
                hero           = s.HeroName,
                s.Kills, s.Deaths, s.Assists,
                s.Gpm,
                result         = isWin ? "Win" : "Loss",
                side           = myTeam?.Side,
                opponentTeam   = oppTeam?.TeamName
            };
        }).ToList();
 
        var formStreak = recentMatches.Select(m => m.result == "Win" ? "W" : "L").ToList();
 
        var since3m = DateTime.UtcNow.AddMonths(-3);
        var performanceTrend = statsRaw
            .Where(s => s.MatchDate >= since3m)
            .GroupBy(s => new { s.MatchDate.Year, s.MatchDate.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g =>
            {
                int gamesCount = g.Count();
                int monthWins  = g.Count(s => wonSet.Contains((s.MatchId, s.TeamId ?? 0)));
                return new
                {
                    label   = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM yy"),
                    games   = gamesCount,
                    wins    = monthWins,
                    winRate = gamesCount > 0 ? Math.Round((double)monthWins / gamesCount * 100, 1) : 0.0,
                    avgGpm  = Math.Round(g.Average(s => (double)s.Gpm), 1),
                    avgKda  = Math.Round(
                        (g.Sum(s => (double)(s.Kills + s.Assists))) / Math.Max(1.0, g.Sum(s => (double)s.Deaths)), 2)
                };
            })
            .ToList();
 
        return new
        {
            info = BuildPlayerInfo(player, totalMatches, wins, winRate,
                Math.Round(statsRaw.Average(s => (double)s.Gpm), 1)),
            performance,
            signatureHeroes,
            recentMatches,
            formStreak,
            performanceTrend
        };
    }
 
    private static object BuildPlayerInfo(
        Player player, int totalMatches, int wins, double winRate, double avgGpm)
    {
        return new
        {
            player.PlayerId,
            player.Nickname,
            player.Country,
            player.PhotoPath,
            teamName      = player.Team?.TeamName,
            teamId        = player.Team?.TeamId,
            teamLogo      = player.Team?.LogoPath,
            lastMatchTime = player.LastMatchTime,
            totalMatches,
            wins,
            winRate,
            avgGpm
        };
    }
 
    public async Task<object?> GetPlayerAnalyticsAsync(int id)
    {
        var exists = await _context.Players.AnyAsync(p => p.PlayerId == id);
        if (!exists) return null;
        
        var statsRaw = await _context.PlayerMatchStats
            .Where(s => s.PlayerId == id)
            .Select(s => new
            {
                s.MatchId,
                s.TeamId,
                s.HeroId,
                HeroName     = s.Hero.LocalizedName,
                s.Kills, s.Deaths, s.Assists,
                s.Gpm, s.Xpm,
                s.HeroDamage, s.TowerDamage,
                s.NetWorth, s.LastHits,
                MatchDate    = s.Match.MatchDate,
                Duration     = s.Match.Duration
            })
            .ToListAsync();
 
        if (!statsRaw.Any()) return null;
 
        var matchIds = statsRaw.Select(s => s.MatchId).Distinct().ToList();
        var wonMatchIds = await _context.MatchTeams
            .Where(mt => matchIds.Contains(mt.MatchId) && mt.IsWinner)
            .Select(mt => new { mt.MatchId, mt.TeamId })
            .ToListAsync();
        var wonSet = wonMatchIds.Select(x => (x.MatchId, x.TeamId)).ToHashSet();
 
        var gpmDurationScatter = statsRaw.Select(s => new
        {
            gpm      = s.Gpm,
            duration = Math.Round((double)(s.Duration) / 60.0, 1),
            result   = wonSet.Contains((s.MatchId, s.TeamId ?? 0)) ? "Win" : "Loss"
        }).ToList();
 
        var heroIds       = statsRaw.Select(s => s.HeroId).Distinct().ToList();
        var heroRolesRaw  = await _context.HeroRoles
            .Where(hr => heroIds.Contains(hr.HeroId))
            .Select(hr => new { hr.HeroId, hr.Role.RoleName })
            .ToListAsync();
 
        var roleBreakdown = heroRolesRaw
            .Join(statsRaw, hr => hr.HeroId, s => s.HeroId, (hr, s) => hr.RoleName)
            .GroupBy(r => r)
            .Select(g => new { role = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToList();
 
        
        var globalAvgRaw = await _context.PlayerMatchStats
            .GroupBy(s => s.PlayerId)
            .Select(g => new
            {
                avgGpm     = g.Sum(s => (long)s.Gpm),
                avgKills   = g.Sum(s => (long)s.Kills),
                avgDeaths  = g.Sum(s => (long)s.Deaths),
                avgAssists = g.Sum(s => (long)s.Assists),
                count      = g.Count()
            })
            .Where(x => x.count >= 3)
            .ToListAsync();
 
        double globalGpm     = globalAvgRaw.Count > 0 ? globalAvgRaw.Average(x => (double)x.avgGpm / x.count) : 0;
        double globalKills   = globalAvgRaw.Count > 0 ? globalAvgRaw.Average(x => (double)x.avgKills / x.count) : 0;
        double globalDeaths  = globalAvgRaw.Count > 0 ? globalAvgRaw.Average(x => (double)x.avgDeaths / x.count) : 0;
        double globalAssists = globalAvgRaw.Count > 0 ? globalAvgRaw.Average(x => (double)x.avgAssists / x.count) : 0;
 
        double playerGpm     = statsRaw.Average(s => (double)s.Gpm);
        double playerKills   = statsRaw.Average(s => (double)s.Kills);
        double playerDeaths  = statsRaw.Average(s => (double)s.Deaths);
        double playerAssists = statsRaw.Average(s => (double)s.Assists);
 
        var radarComparison = new[]
        {
            new { stat = "GPM",     player = Math.Round(playerGpm,     1), global = Math.Round(globalGpm,     1) },
            new { stat = "Kills",   player = Math.Round(playerKills,   1), global = Math.Round(globalKills,   1) },
            new { stat = "Deaths",  player = Math.Round(playerDeaths,  1), global = Math.Round(globalDeaths,  1) },
            new { stat = "Assists", player = Math.Round(playerAssists, 1), global = Math.Round(globalAssists, 1) },
            new { stat = "DMG",     player = Math.Round(statsRaw.Average(s => (double)s.HeroDamage), 0),
                                    global = 0.0 }  
        };
 
        var bestGames = statsRaw
            .OrderByDescending(s => s.Gpm)
            .Take(5)
            .Select(s => new
            {
                s.MatchId,
                hero      = s.HeroName,
                s.Kills, s.Deaths, s.Assists,
                s.Gpm,
                duration  = Math.Round((double)(s.Duration) / 60.0, 1),
                matchDate = s.MatchDate,
                result    = wonSet.Contains((s.MatchId, s.TeamId ?? 0)) ? "Win" : "Loss"
            })
            .ToList();
 
        var sideRaw = await _context.MatchTeams
            .Where(mt => matchIds.Contains(mt.MatchId))
            .Select(mt => new { mt.MatchId, mt.TeamId, mt.Side, mt.IsWinner })
            .ToListAsync();
 
        var playerSides = sideRaw
            .Where(mt => statsRaw.Any(s => s.MatchId == mt.MatchId && s.TeamId == mt.TeamId))
            .ToList();
 
        var radiantGames = playerSides.Count(x => x.Side == "Radiant");
        var radiantWins  = playerSides.Count(x => x.Side == "Radiant" && x.IsWinner);
        var direGames    = playerSides.Count(x => x.Side == "Dire");
        var direWins     = playerSides.Count(x => x.Side == "Dire" && x.IsWinner);
 
        var sidePerformance = new
        {
            radiantGames,
            radiantWins,
            radiantWinRate = radiantGames > 0 ? Math.Round((double)radiantWins / radiantGames * 100, 1) : 0.0,
            direGames,
            direWins,
            direWinRate    = direGames > 0 ? Math.Round((double)direWins / direGames * 100, 1) : 0.0,
            preferredSide  = radiantGames >= direGames ? "Radiant" : "Dire"
        };
 
        int uniqueHeroes = statsRaw.Select(s => s.HeroId).Distinct().Count();
 
        return new
        {
            gpmDurationScatter,
            roleBreakdown,
            radarComparison,
            bestGames,
            sidePerformance,
            heroBreadth = new
            {
                uniqueHeroes,
                totalGames     = statsRaw.Count,
                diversityScore = statsRaw.Count > 0
                    ? Math.Round((double)uniqueHeroes / statsRaw.Count * 100, 1) : 0.0
            }
        };
    }
 
    public async Task<object> GetGlobalPlayerAnalyticsAsync()
    {
        var scatterRaw = await _context.PlayerMatchStats
            .GroupBy(s => new { s.PlayerId, s.Player.Nickname })
            .Select(g => new
            {
                g.Key.Nickname,
                totalGpm     = g.Sum(s => (long)s.Gpm),
                totalKills   = g.Sum(s => (long)s.Kills),
                totalDeaths  = g.Sum(s => (long)s.Deaths),
                totalAssists = g.Sum(s => (long)s.Assists),
                matches      = g.Count()
            })
            .Where(x => x.matches >= 5)
            .ToListAsync();
 
        var scatterData = scatterRaw.Select(x => new
        {
            name    = x.Nickname,
            gpm     = Math.Round((double)x.totalGpm / x.matches, 1),
            kda     = Math.Round((double)(x.totalKills + x.totalAssists) / Math.Max(1, x.totalDeaths), 2),
            matches = x.matches
        }).ToList();
        
 
        var playersWithCountry = await _context.Players
            .Where(p => p.Country != null)
            .Select(p => new { p.PlayerId, p.Country })
            .ToListAsync();
 
        var allStats = await _context.PlayerMatchStats
            .Select(s => new { s.PlayerId, s.MatchId, s.TeamId, s.Kills, s.Deaths, s.Assists })
            .ToListAsync();
 
        var allWonTeams = await _context.MatchTeams
            .Where(mt => mt.IsWinner)
            .Select(mt => new { mt.MatchId, mt.TeamId })
            .ToListAsync();
        var globalWonSet = allWonTeams.Select(x => (x.MatchId, x.TeamId)).ToHashSet();
 
        var playerCountryMap = playersWithCountry.ToDictionary(p => p.PlayerId, p => p.Country!);
 
        var regionalIndex = allStats
            .Where(s => playerCountryMap.ContainsKey(s.PlayerId))
            .GroupBy(s => playerCountryMap[s.PlayerId])
            .Select(g =>
            {
                int totalMatches  = g.Count();
                int countryWins   = g.Count(s => globalWonSet.Contains((s.MatchId, s.TeamId ?? 0)));
                double totalK     = g.Sum(s => (double)(s.Kills + s.Assists));
                double totalD     = g.Sum(s => (double)s.Deaths);
                int playerCount   = g.Select(s => s.PlayerId).Distinct().Count();
                return new
                {
                    country      = g.Key,
                    playerCount,
                    totalMatches,
                    avgKda       = Math.Round(totalK / Math.Max(1, totalD), 2),
                    winRate      = totalMatches > 0
                        ? Math.Round((double)countryWins / totalMatches * 100, 1) : 0.0
                };
            })
            .Where(x => x.playerCount >= 2)
            .OrderByDescending(x => x.avgKda)
            .Take(10)
            .ToList();
 
        var greediest = scatterData
            .OrderByDescending(x => x.gpm)
            .Select(x => new { name = x.name, value = x.gpm, matches = x.matches })
            .FirstOrDefault();
 
        var highestKda = scatterData
            .OrderByDescending(x => x.kda)
            .Select(x => new { name = x.name, value = x.kda, matches = x.matches })
            .FirstOrDefault();
 
        var mostMatches = scatterRaw
            .OrderByDescending(x => x.matches)
            .Select(x => new { name = x.Nickname, value = x.matches })
            .FirstOrDefault();
 
        var roleBenchmarksRaw = await _context.PlayerMatchStats
            .Join(_context.HeroRoles, s => s.HeroId, hr => hr.HeroId, (s, hr) => new
            {
                hr.Role.RoleName,
                s.Gpm,
                s.Xpm,
                s.Kills,
                s.Deaths,
                s.Assists
            })
            .GroupBy(x => x.RoleName)
            .Select(g => new
            {
                role      = g.Key,
                totalGpm  = g.Sum(x => (long)x.Gpm),
                totalXpm  = g.Sum(x => (long)x.Xpm),
                totalK    = g.Sum(x => (long)x.Kills),
                totalD    = g.Sum(x => (long)x.Deaths),
                totalA    = g.Sum(x => (long)x.Assists),
                count     = g.Count()
            })
            .ToListAsync();
 
        var roleBenchmarks = roleBenchmarksRaw.Select(g => new
        {
            g.role,
            avgGpm  = g.count > 0 ? Math.Round((double)g.totalGpm / g.count, 1) : 0.0,
            avgXpm  = g.count > 0 ? Math.Round((double)g.totalXpm / g.count, 1) : 0.0,
            avgKda  = Math.Round((double)(g.totalK + g.totalA) / Math.Max(1, g.totalD), 2),
            samples = g.count
        }).OrderByDescending(x => x.avgGpm).ToList();
 
        return new
        {
            scatterData,
            regionalIndex,
            roleBenchmarks,
            outliers = new { greediest, highestKda, mostMatches }
        };
    }
    
 
    public async Task<object> GetAvailablePlayersLookupAsync()
    {
        var linkedPlayerIds = await _context.Users
            .Where(u => u.PlayerId != null)
            .Select(u => u.PlayerId!.Value)
            .ToListAsync();
 
        return await _context.Players
            .Where(p => !linkedPlayerIds.Contains(p.PlayerId))
            .Select(p => new
            {
                id       = p.PlayerId,
                nickname = p.Nickname,
                photo    = p.PhotoPath,
                country  = p.Country
            })
            .OrderBy(p => p.nickname)
            .ToListAsync();
    }
}