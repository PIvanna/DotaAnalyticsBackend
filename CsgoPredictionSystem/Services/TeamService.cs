using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Models;
using CsgoPredictionSystem.DTO;
using CsgoPredictionSystem.Helpers;
using CsgoPredictionSystem.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace CsgoPredictionSystem.Services;

public class TeamService
{
    private readonly DotaDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly ILogger<TeamService> _logger;

    public TeamService(DotaDbContext context, IHttpClientFactory httpClientFactory, IHubContext<SyncHub> hubContext, ILogger<TeamService> logger)
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
            syncType = "Teams",
            status = $"{progress}% - {message}",
            lastRunAt = DateTime.UtcNow
        });
    }

    public async Task<List<object>> GetTeamsLookupAsync()
    {
        return await _context.Teams
            .OrderBy(t => t.TeamName)
            .Select(t => new {
                t.TeamId,
                t.TeamName,
                t.LogoPath
            })
            .ToListAsync<object>();
    }
    public async Task<string> SyncTeamsFromApi(int count, int minRating = 1000, int activeDays = 180)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try 
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "DotaAnalyticsApp");

            await Notify("Getting a list of commands from OpenDota...", 10);
            var response = await client.GetStringAsync("https://api.opendota.com/api/teams");
            var allTeams = JsonConvert.DeserializeObject<List<TeamApiDto>>(response);

            if (allTeams == null || !allTeams.Any()) return "Data not received.";

            await Notify("Filtering teams by rating and activity...", 30);
            long minUnixTime = DateTimeOffset.UtcNow.AddDays(-activeDays).ToUnixTimeSeconds();

            var teamsToProcess = allTeams
                .Where(t => t.Rating >= minRating && t.LastMatchTime >= minUnixTime)      
                .Take(count)                                     
                .ToList();

            if (!teamsToProcess.Any()) return "No team meets the criteria.";

            var externalIds = teamsToProcess.Select(t => t.TeamId).ToList();
            var existingTeamsMap = await _context.Teams
                .Where(t => t.ExternalId.HasValue && externalIds.Contains(t.ExternalId.Value))
                .ToDictionaryAsync(t => t.ExternalId!.Value, t => t);

            int added = 0, updated = 0;
            await Notify("Updating the command database...", 60);

            foreach (var t in teamsToProcess)
            {
                if (existingTeamsMap.TryGetValue(t.TeamId, out var existing))
                {
                    existing.TeamName = t.Name?.Length > 100 ? t.Name.Substring(0, 100) : (t.Name ?? existing.TeamName);
                    existing.Acronym = t.Tag?.Length > 20 ? t.Tag.Substring(0, 20) : (t.Tag ?? existing.Acronym);
                    existing.CurrentRank = (int)t.Rating; 
                    existing.LogoPath = t.LogoUrl ?? existing.LogoPath;
                    existing.Wins = t.Wins;
                    existing.Losses = t.Losses;
                    existing.LastMatchTime = t.LastMatchTime;
                    updated++;
                }
                else
                {
                    _context.Teams.Add(new Team {
                        ExternalId = t.TeamId,
                        TeamName = t.Name?.Length > 100 ? t.Name.Substring(0, 100) : (t.Name ?? "Unknown Team"),
                        Acronym = t.Tag?.Length > 20 ? t.Tag.Substring(0, 20) : (t.Tag ?? "N/A"),
                        CurrentRank = (int)t.Rating,
                        LogoPath = t.LogoUrl,
                        Wins = t.Wins,
                        Losses = t.Losses,
                        LastMatchTime = t.LastMatchTime
                    });
                    added++;
                }
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            await Notify("Command synchronization is complete!", 100);
            return $"Added: {added}, Updated: {updated}.";
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Command synchronization error");
            
            string friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            await Notify(friendlyError, 0);
            throw;
        }
    }
    
    public async Task<object> GetTeamsDashboardAsync()
    {
        var totalTeams = await _context.Teams.CountAsync();
        
        var top10Raw = await _context.Teams
            .OrderByDescending(t => t.CurrentRank)
            .Take(10)
            .Select(t => new
            {
                t.TeamId, t.TeamName, t.Acronym,
                t.LogoPath, t.CurrentRank, t.Wins, t.Losses
            })
            .ToListAsync();
 
        var top10 = top10Raw.Select(t =>
        {
            int tg = t.Wins + t.Losses;
            return new
            {
                t.TeamId, t.TeamName, t.Acronym, t.LogoPath, t.CurrentRank,
                t.Wins, t.Losses,
                totalGames = tg,
                winRate    = tg > 0 ? Math.Round((double)t.Wins / tg * 100, 1) : 0.0
            };
        }).ToList();
        
        var allTeamsRaw = await _context.Teams
            .Where(t => (t.Wins + t.Losses) > 0)
            .Select(t => new { t.Wins, t.Losses })
            .ToListAsync();
 
        var winRateBuckets = new (int from, int to, string label)[]
        {
            (0, 30, "0-30%"), (30, 40, "30-40%"), (40, 50, "40-50%"),
            (50, 60, "50-60%"), (60, 70, "60-70%"), (70, 100, "70%+")
        };
 
        var winRateDistribution = winRateBuckets.Select(b =>
        {
            int count = allTeamsRaw.Count(t =>
            {
                double wr = (double)t.Wins / (t.Wins + t.Losses) * 100;
                return wr >= b.from && wr < b.to;
            });
            return new { range = b.label, count };
        }).ToList();
 
        var since30 = DateTime.UtcNow.AddDays(-30);
        var recentlyActiveCount = await _context.MatchTeams
            .Where(mt => mt.Match.MatchDate >= since30)
            .Select(mt => mt.TeamId)
            .Distinct()
            .CountAsync();
 
        int elite  = allTeamsRaw.Count(t => (double)t.Wins / (t.Wins + t.Losses) >= 0.60);
        int strong = allTeamsRaw.Count(t => { var wr = (double)t.Wins / (t.Wins + t.Losses); return wr >= 0.50 && wr < 0.60; });
        int avg    = allTeamsRaw.Count(t => { var wr = (double)t.Wins / (t.Wins + t.Losses); return wr >= 0.40 && wr < 0.50; });
        int weak   = allTeamsRaw.Count(t => (double)t.Wins / (t.Wins + t.Losses) < 0.40);
 
        var tierBreakdown = new[]
        {
            new { name = "Elite (60%+ WR)", value = elite, color = "#fbbf24" },
            new { name = "Strong (50-60%)", value = strong, color = "#10b981" },
            new { name = "Average (40-50%)", value = avg, color = "#00d4ff" },
            new { name = "Struggling (<40%)", value = weak, color = "#ef4444" }
        };
 
        var mostActiveTeams = await _context.MatchTeams
            .GroupBy(mt => new { mt.TeamId, mt.Team.TeamName, mt.Team.LogoPath })
            .Select(g => new
            {
                name   = g.Key.TeamName,
                logo   = g.Key.LogoPath,
                games  = g.Count()
            })
            .OrderByDescending(x => x.games)
            .Take(5)
            .ToListAsync();
 
        var globalWins   = allTeamsRaw.Sum(t => t.Wins);
        var globalLosses = allTeamsRaw.Sum(t => t.Losses);
        var globalAvgWr  = allTeamsRaw.Count > 0
            ? Math.Round(allTeamsRaw.Average(t => (double)t.Wins / (t.Wins + t.Losses) * 100), 1) : 0.0;
 
        return new
        {
            summary = new
            {
                totalTeams,
                recentlyActiveCount,
                globalAvgWinRate = globalAvgWr,
                totalRecordedGames = globalWins + globalLosses
            },
            top10,
            winRateDistribution,
            tierBreakdown,
            mostActiveTeams
        };
    }
 
    public async Task<object> GetTeamsPagedAsync(
        int page, int pageSize,
        string? search, int? minRank, int? maxRank,
        string sortBy = "rank")
    {
        var query = _context.Teams.AsQueryable();
 
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t =>
                t.TeamName.ToLower().Contains(search.ToLower()) ||
                t.Acronym.ToLower().Contains(search.ToLower()));
 
        if (minRank.HasValue) query = query.Where(t => t.CurrentRank >= minRank.Value);
        if (maxRank.HasValue) query = query.Where(t => t.CurrentRank <= maxRank.Value);
 
        query = sortBy.ToLower() switch
        {
            "wins"     => query.OrderByDescending(t => t.Wins),
            "activity" => query.OrderByDescending(t => t.LastMatchTime),
            "winrate"  => query.OrderByDescending(t =>
                (t.Wins + t.Losses) == 0 ? 0.0 : (double)t.Wins / (t.Wins + t.Losses)),
            _          => query.OrderByDescending(t => t.CurrentRank)
        };
 
        var total = await query.CountAsync();
 
        var rawItems = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                t.TeamId,
                t.TeamName,
                t.Acronym,
                t.LogoPath,
                t.CurrentRank,
                t.Wins,
                t.Losses,
                t.LastMatchTime
            })
            .ToListAsync();
 
        var items = rawItems.Select(t =>
        {
            int totalGames = t.Wins + t.Losses;
            double winRate = totalGames > 0
                ? Math.Round((double)t.Wins / totalGames * 100, 1) : 0.0;
            DateTime? lastMatch = t.LastMatchTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(t.LastMatchTime.Value).UtcDateTime
                : null;
            return new
            {
                t.TeamId,
                t.TeamName,
                t.Acronym,
                t.LogoPath,
                t.CurrentRank,
                t.Wins,
                t.Losses,
                totalGames,
                winRate,
                lastMatchDate = lastMatch
            };
        }).ToList();
 
        var top5Raw = await _context.Teams
            .OrderByDescending(t => t.CurrentRank)
            .Take(5)
            .Select(t => new { t.TeamId, t.TeamName, t.LogoPath, t.CurrentRank, t.Wins, t.Losses })
            .ToListAsync();
 
        var top5 = top5Raw.Select(t => new
        {
            t.TeamId, t.TeamName, t.LogoPath, t.CurrentRank,
            winRate = (t.Wins + t.Losses) > 0
                ? Math.Round((double)t.Wins / (t.Wins + t.Losses) * 100, 1) : 0.0
        }).ToList();
 
        return new { total, page, pageSize, items, top5 };
    }
    
    public async Task<object?> GetTeamDetailsAsync(int id)
    {
        var team = await _context.Teams
            .Include(t => t.Players)
            .FirstOrDefaultAsync(t => t.TeamId == id);
 
        if (team == null) return null;
 
        int totalGames = team.Wins + team.Losses;
        double winRate = totalGames > 0
            ? Math.Round((double)team.Wins / totalGames * 100, 1) : 0.0;
 
        
        var avgDurationSec = await _context.MatchTeams
            .Where(mt => mt.TeamId == id)
            .AverageAsync(mt => (double?)mt.Match.Duration) ?? 0;
 
        
        var tournaments = await _context.MatchTeams
            .Where(mt => mt.TeamId == id && mt.Match.Tournament != null)
            .Select(mt => new { mt.Match.Tournament!.TournamentId, mt.Match.Tournament.Name, mt.Match.Tournament.Tier })
            .Distinct()
            .ToListAsync();
        
        var rawMatches = await _context.MatchTeams
            .Where(mt => mt.TeamId == id)
            .OrderByDescending(mt => mt.Match.MatchDate)
            .Take(10)
            .Select(mt => new
            {
                mt.MatchId,
                mt.Match.MatchDate,
                Duration       = mt.Match.Duration,
                MyScore        = mt.Score,
                mt.IsWinner,
                mt.Side,
                TournamentName = mt.Match.Tournament != null ? mt.Match.Tournament.Name : null,
                AllTeams = mt.Match.MatchTeams.Select(x => new
                {
                    x.TeamId,
                    x.Team.TeamName,
                    x.Team.LogoPath,
                    x.Score
                }).ToList()
            })
            .ToListAsync();
 
        var recentMatches = rawMatches.Select(m =>
        {
            var opp = m.AllTeams.FirstOrDefault(t => t.TeamId != id);
            return new
            {
                m.MatchId,
                m.MatchDate,
                m.Duration,
                m.MyScore,
                m.IsWinner,
                m.Side,
                m.TournamentName,
                opponentName  = opp?.TeamName,
                opponentLogo  = opp?.LogoPath,
                opponentScore = opp?.Score ?? 0
            };
        }).ToList();
        
        var formStreak = recentMatches
            .Select(m => m.IsWinner ? "W" : "L")
            .ToList();
        
        var playerIds = team.Players.Select(p => p.PlayerId).ToList();
 
        var playerStatsRaw = await _context.PlayerMatchStats
            .Where(s => s.TeamId == id && playerIds.Contains(s.PlayerId))
            .Select(s => new { s.PlayerId, s.Kills, s.Deaths, s.Assists })
            .ToListAsync();
 
        var roster = team.Players.Select(p =>
        {
            var stats = playerStatsRaw.Where(s => s.PlayerId == p.PlayerId).ToList();
            double kda = 0;
            if (stats.Count > 0)
            {
                double totalKA  = stats.Sum(s => s.Kills + s.Assists);
                double totalD   = stats.Sum(s => Math.Max(1, s.Deaths));
                kda = Math.Round(totalKA / totalD, 2);
            }
            return new
            {
                p.PlayerId,
                p.Nickname,
                p.Country,
                p.PhotoPath,
                kda,
                gamesPlayed = stats.Count
            };
        }).ToList();
 
        DateTime? lastMatch = team.LastMatchTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(team.LastMatchTime.Value).UtcDateTime : null;
 
        return new
        {
            info = new
            {
                team.TeamId,
                team.TeamName,
                team.Acronym,
                team.LogoPath,
                team.CurrentRank,
                team.Wins,
                team.Losses,
                totalGames,
                winRate,
                avgDuration = Math.Round(avgDurationSec / 60.0, 1),
                lastMatch
            },
            roster,
            tournaments,
            recentMatches,
            formStreak
        };
    }
    
    public async Task<object?> GetAdvancedTeamAnalyticsAsync(int id)
    {
        var team = await _context.Teams.FirstOrDefaultAsync(t => t.TeamId == id);
        if (team == null) return null;
        
 
        var allMatchTeamsRaw = await _context.MatchTeams
            .Select(mt => new
            {
                mt.TeamId,
                mt.Team.TeamName,
                mt.Score,
                DurationMin = (double)mt.Match.Duration / 60.0
            })
            .ToListAsync();
 
        var globalMatrix = allMatchTeamsRaw
            .GroupBy(mt => new { mt.TeamId, mt.TeamName })
            .Select(g =>
            {
                double totalDur = g.Sum(x => x.DurationMin);
                double avgDur   = g.Count() > 0 ? g.Average(x => x.DurationMin) : 0;
                double kpm      = totalDur > 0 ? g.Sum(x => x.Score) / totalDur : 0;
                return new
                {
                    name     = g.Key.TeamName,
                    isTarget = g.Key.TeamId == id,
                    duration = Math.Round(avgDur, 1),
                    kpm      = Math.Round(kpm, 3)
                };
            })
            .Where(x => x.duration > 0)
            .ToList();
 
        var heroStatsRaw = await _context.PlayerMatchStats
            .Where(s => s.TeamId == id)
            .Select(s => new { s.MatchId, HeroName = s.Hero.LocalizedName })
            .ToListAsync();
 
        var wonMatchIds = await _context.MatchTeams
            .Where(mt => mt.TeamId == id && mt.IsWinner)
            .Select(mt => mt.MatchId)
            .ToListAsync();
 
        var wonSet = wonMatchIds.ToHashSet();
 
        var signatures = heroStatsRaw
            .GroupBy(s => s.HeroName)
            .Select(g =>
            {
                int games  = g.Count();
                int wins   = g.Count(x => wonSet.Contains(x.MatchId));
                double wr  = games > 0 ? Math.Round((double)wins / games * 100, 1) : 0;
                return new { heroName = g.Key, games, wins, winRate = wr };
            })
            .Where(x => x.games >= 3)
            .OrderByDescending(x => x.winRate)
            .ThenByDescending(x => x.games)
            .Take(8)
            .ToList();
 
        int uniqueHeroes = heroStatsRaw.Select(s => s.HeroName).Distinct().Count();
        int totalHeroPicks = heroStatsRaw.Count;
 
        var totalDurationMin = allMatchTeamsRaw
            .Where(mt => mt.TeamId == id)
            .Sum(x => x.DurationMin);
 
        var towerDmgSum = await _context.PlayerMatchStats
            .Where(s => s.TeamId == id)
            .SumAsync(s => (double)s.TowerDamage);
 
        double tdm = totalDurationMin > 0
            ? Math.Round(towerDmgSum / totalDurationMin, 1) : 0.0;
 
        var longGameIds = await _context.Matches
            .Where(m => m.Duration > 2400 && m.MatchTeams.Any(mt => mt.TeamId == id))
            .Select(m => m.MatchId)
            .ToListAsync();
 
        var longGameWins = await _context.MatchTeams
            .Where(mt => mt.TeamId == id && longGameIds.Contains(mt.MatchId) && mt.IsWinner)
            .CountAsync();
 
        int longGameCount   = longGameIds.Count;
        double comebackRate = longGameCount > 0
            ? Math.Round((double)longGameWins / longGameCount * 100, 1) : 0.0;
 
        var since3m = DateTime.UtcNow.AddMonths(-3);
        var recentResultsRaw = await _context.MatchTeams
            .Where(mt => mt.TeamId == id && mt.Match.MatchDate >= since3m)
            .Select(mt => new { mt.Match.MatchDate, mt.IsWinner })
            .ToListAsync();
 
        var performanceTrend = recentResultsRaw
            .GroupBy(x => new { x.MatchDate.Year, x.MatchDate.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g =>
            {
                int games = g.Count();
                int wins  = g.Count(x => x.IsWinner);
                return new
                {
                    month   = $"{g.Key.Year}-{g.Key.Month:D2}",
                    label   = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM yy"),
                    games,
                    wins,
                    winRate = games > 0 ? Math.Round((double)wins / games * 100, 1) : 0.0
                };
            })
            .ToList();
 
        var sideRaw = await _context.MatchTeams
            .Where(mt => mt.TeamId == id)
            .Select(mt => new { mt.Side, mt.IsWinner })
            .ToListAsync();
 
        var radiantGames = sideRaw.Count(x => x.Side == "Radiant");
        var radiantWins  = sideRaw.Count(x => x.Side == "Radiant" && x.IsWinner);
        var direGames    = sideRaw.Count(x => x.Side == "Dire");
        var direWins     = sideRaw.Count(x => x.Side == "Dire" && x.IsWinner);
 
        var sidePreference = new
        {
            radiantGames,
            radiantWins,
            radiantWinRate = radiantGames > 0
                ? Math.Round((double)radiantWins / radiantGames * 100, 1) : 0.0,
            direGames,
            direWins,
            direWinRate = direGames > 0
                ? Math.Round((double)direWins / direGames * 100, 1) : 0.0,
            preferredSide = radiantGames >= direGames ? "Radiant" : "Dire"
        };
 
        var myMatchIds = await _context.MatchTeams
            .Where(mt => mt.TeamId == id)
            .Select(mt => mt.MatchId)
            .ToListAsync();
 
        var myMatchIdSet = myMatchIds.ToHashSet();
 
        var opponentRaw = await _context.MatchTeams
            .Where(mt => myMatchIdSet.Contains(mt.MatchId) && mt.TeamId != id)
            .Select(mt => new { mt.TeamId, mt.Team.TeamName, mt.Team.LogoPath, mt.IsWinner, mt.MatchId })
            .ToListAsync();
 
        var ourWinSet = wonMatchIds.ToHashSet();
 
        var topOpponents = opponentRaw
            .GroupBy(x => new { x.TeamId, x.TeamName, x.LogoPath })
            .Select(g =>
            {
                int games     = g.Count();
                int ourWins   = g.Count(x => ourWinSet.Contains(x.MatchId));
                int theirWins = games - ourWins;
                return new
                {
                    name      = g.Key.TeamName,
                    logo      = g.Key.LogoPath,
                    games,
                    ourWins,
                    theirWins,
                    ourWinRate = games > 0 ? Math.Round((double)ourWins / games * 100, 1) : 0.0
                };
            })
            .OrderByDescending(x => x.games)
            .Take(5)
            .ToList();
 
        var avgPlayerStats = await _context.PlayerMatchStats
            .Where(s => s.TeamId == id)
            .GroupBy(s => s.PlayerId)
            .Select(g => new
            {
                playerId = g.Key,
                avgKills   = g.Average(x => (double)x.Kills),
                avgDeaths  = g.Average(x => (double)x.Deaths),
                avgAssists = g.Average(x => (double)x.Assists),
                avgGpm     = g.Average(x => (double)x.Gpm),
                avgXpm     = g.Average(x => (double)x.Xpm)
            })
            .ToListAsync();
 
        var teamPlayers = await _context.Players
            .Where(p => p.TeamId == id)
            .Select(p => new { p.PlayerId, p.Nickname })
            .ToListAsync();
 
        var playerPerformance = avgPlayerStats
            .Join(teamPlayers, s => s.playerId, p => p.PlayerId,
                (s, p) => new
                {
                    p.Nickname,
                    avgKills   = Math.Round(s.avgKills, 1),
                    avgDeaths  = Math.Round(s.avgDeaths, 1),
                    avgAssists = Math.Round(s.avgAssists, 1),
                    avgGpm     = Math.Round(s.avgGpm, 0),
                    avgXpm     = Math.Round(s.avgXpm, 0),
                    kda        = Math.Round((s.avgKills + s.avgAssists) / Math.Max(1, s.avgDeaths), 2)
                })
            .OrderByDescending(x => x.kda)
            .ToList();
 

        double pressureIndex = Math.Min(100, Math.Round(tdm / 5.0, 1)); 
 
        return new
        {
            matrix     = globalMatrix,
            signatures,
            heroBreadth = new
            {
                uniqueHeroes,
                totalHeroPicks,
                diversityScore = totalHeroPicks > 0
                    ? Math.Round((double)uniqueHeroes / totalHeroPicks * 100, 1) : 0.0
            },
            objectives = new
            {
                towerDamagePerMinute = tdm,
                pressureIndex,
                comebackRate,
                totalLongGames = longGameCount
            },
            sidePreference,
            performanceTrend,
            topOpponents,
            playerPerformance
        };
    }
}