using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Models;
using CsgoPredictionSystem.DTO;
using CsgoPredictionSystem.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Newtonsoft.Json;

namespace CsgoPredictionSystem.Services;

public class MatchService
{
    private readonly DotaDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MatchService> _logger;
    private readonly IHubContext<SyncHub> _hubContext;

    public MatchService(DotaDbContext context, IHttpClientFactory httpClientFactory, ILogger<MatchService> logger, IHubContext<SyncHub> hubContext)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _hubContext = hubContext;
    }
    
    private async Task Notify(string message, int progress)
    {
        _logger.LogInformation("[SignalR] Sending update: {Progress}% - {Message}", progress, message);
    
        try 
        {
            await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new
            {
                syncType = "Matches",
                status = $"{progress}% - {message}",
                lastRunAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SignalR] Failed to send message to clients");
        }
    }

    public async Task<string> SyncMatchesTask(int count, int daysAgo, int minDurationMinutes, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", "DotaAnalyticsApp");

        await Notify("Getting a list of pro-match...", 5);
        
        var response = await client.GetAsync("https://api.opendota.com/api/proMatches", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var allMatches = JsonConvert.DeserializeObject<List<MatchApiDto>>(json);

        if (allMatches == null || !allMatches.Any()) return "No matches found.";

        var minStartTime = DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeSeconds();
        var matchesToProcess = allMatches
            .Where(m => m.StartTime >= minStartTime && m.Duration >= minDurationMinutes * 60 && m.RadiantTeamId.HasValue)
            .Take(count).ToList();

        if (!matchesToProcess.Any()) return "No new matches for the specified period.";

        await Notify("Preparing local directories...", 10);
        
        var teamMap = await _context.Teams.ToDictionaryAsync(t => t.ExternalId, t => t, ct);
        var tournamentMap = await _context.Tournaments.ToDictionaryAsync(t => t.ExternalId, t => t.TournamentId, ct);
        var playerMap = await _context.Players.ToDictionaryAsync(p => p.SteamId, p => p.PlayerId, ct);

        int added = 0;
        int failed = 0;

        for (int i = 0; i < matchesToProcess.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var m = matchesToProcess[i];
            int progress = 10 + (i * 85 / matchesToProcess.Count);
            
            if (await _context.Matches.AnyAsync(x => x.ExternalId == m.MatchId, ct)) continue;

            using var matchTransaction = await _context.Database.BeginTransactionAsync(ct);
            try
            {
                await Notify($"Match processing {m.MatchId} ({i + 1}/{matchesToProcess.Count})...", progress);
                
                await Task.Delay(1500, ct); 

                var detailResponse = await client.GetAsync($"https://api.opendota.com/api/matches/{m.MatchId}", ct);
                var detailJson = await detailResponse.Content.ReadAsStringAsync(ct);
                var details = JsonConvert.DeserializeObject<MatchDetailApiDto>(detailJson);
                
                if (details == null) throw new Exception("API Error: Empty server response.");

                var newMatch = new Match {
                    ExternalId = m.MatchId,
                    MatchDate = DateTimeOffset.FromUnixTimeSeconds(m.StartTime).UtcDateTime,
                    Duration = m.Duration,
                    TournamentId = tournamentMap.TryGetValue(m.LeagueId, out int tId) ? tId : null
                };
                _context.Matches.Add(newMatch);
                await _context.SaveChangesAsync(ct);

                await AddMatchTeamFast(newMatch.MatchId, details.RadiantTeamId, details.RadiantScore, details.RadiantWin, "Radiant", teamMap);
                await AddMatchTeamFast(newMatch.MatchId, details.DireTeamId, details.DireScore, !details.RadiantWin, "Dire", teamMap);

                foreach (var p in details.Players)
                {
                    if (p.AccountId.HasValue && playerMap.TryGetValue(p.AccountId.Value, out int dbPlayerId))
                    {
                        var radiantTeam = teamMap.Values.FirstOrDefault(t => t.ExternalId == details.RadiantTeamId);
                        var direTeam = teamMap.Values.FirstOrDefault(t => t.ExternalId == details.DireTeamId);

                        _context.PlayerMatchStats.Add(new PlayerMatchStats {
                            MatchId = newMatch.MatchId,
                            PlayerId = dbPlayerId,
                            HeroId = p.HeroId,
                            TeamId = (p.PlayerSlot < 128) ? radiantTeam?.TeamId : direTeam?.TeamId,
                            Kills = p.Kills, Deaths = p.Deaths, Assists = p.Assists,
                            Gpm = p.Gpm, Xpm = p.Xpm, NetWorth = p.NetWorth,
                            LastHits = p.LastHits, Denies = p.Denies,
                            HeroDamage = p.HeroDamage, TowerDamage = p.TowerDamage,
                            PlayerSlot = p.PlayerSlot
                        });
                    }
                }
                await _context.SaveChangesAsync(ct);
                await matchTransaction.CommitAsync(ct);
                added++;
            }
            catch (OperationCanceledException)
            {
                await matchTransaction.RollbackAsync();
                throw; 
            }
            catch (Exception ex)
            {
                if (matchTransaction.GetDbTransaction().Connection != null)
                    await matchTransaction.RollbackAsync(CancellationToken.None); 
    
                failed++;
    
                string friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
                _logger.LogError("Match {Id} skipped. Reason: {Msg}", m.MatchId, friendlyError);
    
                await Notify($"⚠️ Match {m.MatchId} omitted: {friendlyError}", progress);
            }
        }

        await Notify($"Synchronization complete! Added: {added}", 100);
        return $"Added {added} matches.";
    }
    
    private async Task AddMatchTeamFast(int matchId, long? apiTeamId, int score, bool isWinner, string side, Dictionary<long?, Team> teamMap)
    {
        if (apiTeamId.HasValue && teamMap.TryGetValue(apiTeamId, out var team))
        {
            _context.MatchTeams.Add(new MatchTeam {
                MatchId = matchId, TeamId = team.TeamId, Score = score, IsWinner = isWinner, Side = side
            });
        }
    }
    
 
    public async Task<object> GetMatchDashboardAsync()
    {
        var totalMatches = await _context.Matches.CountAsync();
        if (totalMatches == 0) return new { totalMatches = 0 };

        var avgDurationSec = await _context.Matches
            .AverageAsync(m => (double?)m.Duration) ?? 0;
 
        var radiantWins = await _context.MatchTeams
            .CountAsync(mt => mt.Side == "Radiant" && mt.IsWinner);
 
        var totalKills = await _context.PlayerMatchStats.SumAsync(s => s.Kills);
 
        var totalDurationMin = await _context.Matches
            .SumAsync(m => (double)(m.Duration ?? 0) / 60.0);
        
        var durations = await _context.Matches
            .Select(m => (m.Duration ?? 0) / 60)
            .ToListAsync();
 
        var buckets = new (int from, int to, string label)[]
        {
            (0,  10,  "0-10 min"),  (10, 20, "10-20 min"),
            (20, 30, "20-30 min"), (30, 40, "30-40 min"),
            (40, 50, "40-50 min"), (50, 60, "50-60 min"),
            (60, 70, "60-70 min"), (70, 80, "70-80 min"),
            (80, 90, "80-90 min"), (90, int.MaxValue, "90+ min")
        };
 
        var distribution = buckets
            .Select(b => new
            {
                range = b.label,
                count = durations.Count(d => d >= b.from && d < b.to)
            })
            .Where(b => b.count > 0)
            .ToList();
 
        
        var longest = await _context.Matches
            .OrderByDescending(m => m.Duration)
            .Select(m => new { id = m.MatchId, durationSec = (int)(m.Duration ?? 0) })
            .FirstOrDefaultAsync();
 
        var shortest = await _context.Matches
            .Where(m => m.Duration > 600)
            .OrderBy(m => m.Duration)
            .Select(m => new { id = m.MatchId, durationSec = (int)(m.Duration ?? 0) })
            .FirstOrDefaultAsync();
 
        var bloodiestRaw = await _context.PlayerMatchStats
            .GroupBy(s => s.MatchId)
            .Select(g => new { matchId = g.Key, totalKillsMatch = g.Sum(s => s.Kills) })
            .OrderByDescending(x => x.totalKillsMatch)
            .FirstOrDefaultAsync();

        var radiantTeamIds = await _context.MatchTeams
            .Where(mt => mt.Side == "Radiant")
            .Select(mt => mt.TeamId)
            .Distinct()
            .ToListAsync();
 
        var direTeamIds = await _context.MatchTeams
            .Where(mt => mt.Side == "Dire")
            .Select(mt => mt.TeamId)
            .Distinct()
            .ToListAsync();
 
        var radiantStatsRaw = await _context.PlayerMatchStats
            .Where(s => s.TeamId != null && radiantTeamIds.Contains(s.TeamId.Value))
            .Select(s => new { s.Gpm, s.TowerDamage, s.HeroDamage })
            .ToListAsync();
 
        var direStatsRaw = await _context.PlayerMatchStats
            .Where(s => s.TeamId != null && direTeamIds.Contains(s.TeamId.Value))
            .Select(s => new { s.Gpm, s.TowerDamage, s.HeroDamage })
            .ToListAsync();
 
        var factionStats = new
        {
            radiant = new
            {
                avgGpm        = radiantStatsRaw.Count > 0 ? Math.Round(radiantStatsRaw.Average(s => (double)s.Gpm), 1) : 0.0,
                avgTowerDmg   = radiantStatsRaw.Count > 0 ? Math.Round(radiantStatsRaw.Average(s => (double)s.TowerDamage), 0) : 0.0,
                avgHeroDmg    = radiantStatsRaw.Count > 0 ? Math.Round(radiantStatsRaw.Average(s => (double)s.HeroDamage), 0) : 0.0,
                winRate       = totalMatches > 0 ? Math.Round((double)radiantWins / totalMatches * 100, 1) : 0.0
            },
            dire = new
            {
                avgGpm        = direStatsRaw.Count > 0 ? Math.Round(direStatsRaw.Average(s => (double)s.Gpm), 1) : 0.0,
                avgTowerDmg   = direStatsRaw.Count > 0 ? Math.Round(direStatsRaw.Average(s => (double)s.TowerDamage), 0) : 0.0,
                avgHeroDmg    = direStatsRaw.Count > 0 ? Math.Round(direStatsRaw.Average(s => (double)s.HeroDamage), 0) : 0.0,
                winRate       = totalMatches > 0 ? Math.Round(100 - (double)radiantWins / totalMatches * 100, 1) : 0.0
            }
        };
 
        var since14 = DateTime.UtcNow.AddDays(-14).Date;
        var activityRaw = await _context.Matches
            .Where(m => m.MatchDate >= since14)
            .GroupBy(m => m.MatchDate.Date)
            .Select(g => new { date = g.Key, count = g.Count() })
            .OrderBy(g => g.date)
            .ToListAsync();
 
        var activityTrend = Enumerable.Range(0, 14)
            .Select(i => since14.AddDays(i))
            .Select(d => new
            {
                date  = d.ToString("dd MMM"),
                count = activityRaw.FirstOrDefault(x => x.date == d)?.count ?? 0
            })
            .ToList();
 
        var topTournaments = await _context.Matches
            .Where(m => m.TournamentId != null)
            .GroupBy(m => new { m.TournamentId, m.Tournament!.Name })
            .Select(g => new { name = g.Key.Name, matchCount = g.Count() })
            .OrderByDescending(x => x.matchCount)
            .Take(5)
            .ToListAsync();
 
        var topTeams = await _context.MatchTeams
            .Where(mt => mt.IsWinner)
            .GroupBy(mt => new { mt.TeamId, mt.Team.TeamName, mt.Team.LogoPath })
            .Select(g => new { name = g.Key.TeamName, logo = g.Key.LogoPath, wins = g.Count() })
            .OrderByDescending(g => g.wins)
            .Take(5)
            .ToListAsync();
 
        return new
        {
            summary = new
            {
                totalMatches,
                avgDurationMin  = Math.Round(avgDurationSec / 60, 1),
                kpm             = totalDurationMin > 0 ? Math.Round(totalKills / totalDurationMin, 2) : 0.0,
                radiantWinRate  = Math.Round((double)radiantWins / totalMatches * 100, 1),
                direWinRate     = Math.Round(100 - (double)radiantWins / totalMatches * 100, 1)
            },
            records = new
            {
                longest = longest == null ? null : new
                {
                    id       = longest.id,
                    label    = "The Marathon",
                    valueSec = longest.durationSec,
                    valueMin = Math.Round(longest.durationSec / 60.0, 1)
                },
                shortest = shortest == null ? null : new
                {
                    id       = shortest.id,
                    label    = "The Stomp",
                    valueSec = shortest.durationSec,
                    valueMin = Math.Round(shortest.durationSec / 60.0, 1)
                },
                bloodiest = bloodiestRaw == null ? null : new
                {
                    id    = bloodiestRaw.matchId,
                    label = "The Bloodbath",
                    value = bloodiestRaw.totalKillsMatch
                }
            },
            factionStats,
            distribution,
            activityTrend,
            topTournaments,
            topTeams
        };
    }
 
    public async Task<object> GetMatchesPagedAsync(
        int page, int pageSize,
        int? teamId, int? tournamentId,
        DateTime? from, DateTime? to,
        string sortOrder = "desc")
    {
        var query = _context.Matches.AsQueryable();
 
        if (teamId.HasValue)
            query = query.Where(m => m.MatchTeams.Any(mt => mt.TeamId == teamId));
        if (tournamentId.HasValue)
            query = query.Where(m => m.TournamentId == tournamentId);
        if (from.HasValue)
            query = query.Where(m => m.MatchDate >= from.Value);
        if (to.HasValue)
            query = query.Where(m => m.MatchDate <= to.Value);
 
        var total = await query.CountAsync();
 
        query = sortOrder.ToLower() == "asc"
            ? query.OrderBy(m => m.MatchDate)
            : query.OrderByDescending(m => m.MatchDate);
 
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new
            {
                m.MatchId,
                m.MatchDate,
                m.Duration,
                tournamentName = m.Tournament != null ? m.Tournament.Name : null,
                radiant = m.MatchTeams
                    .Where(mt => mt.Side == "Radiant")
                    .Select(mt => new
                    {
                        name     = mt.Team.TeamName,
                        logo     = mt.Team.LogoPath,
                        acronym  = mt.Team.Acronym,
                        mt.Score,
                        mt.IsWinner
                    })
                    .FirstOrDefault(),
                dire = m.MatchTeams
                    .Where(mt => mt.Side == "Dire")
                    .Select(mt => new
                    {
                        name     = mt.Team.TeamName,
                        logo     = mt.Team.LogoPath,
                        acronym  = mt.Team.Acronym,
                        mt.Score,
                        mt.IsWinner
                    })
                    .FirstOrDefault()
            })
            .ToListAsync();
 
        return new { total, page, pageSize, items };
    }
    
 
    public async Task<object?> GetMatchDetailsAsync(int id)
    {
        var match = await _context.Matches
            .Include(m => m.Tournament)
            .FirstOrDefaultAsync(m => m.MatchId == id);
 
        if (match == null) return null;
 
        
        var matchTeams = await _context.MatchTeams
            .Where(mt => mt.MatchId == id)
            .Select(mt => new
            {
                mt.TeamId,
                mt.Team.TeamName,
                mt.Team.LogoPath,
                mt.Team.Acronym,
                mt.Score,
                mt.IsWinner,
                mt.Side
            })
            .ToListAsync();
 
        var radiantTeam = matchTeams.FirstOrDefault(mt => mt.Side == "Radiant");
        var direTeam    = matchTeams.FirstOrDefault(mt => mt.Side == "Dire");
 
        var playerStats = await _context.PlayerMatchStats
            .Where(s => s.MatchId == id)
            .Select(s => new
            {
                s.PlayerId,
                s.TeamId,
                s.PlayerSlot,
                Nickname     = s.Player.Nickname,
                HeroName     = s.Hero.LocalizedName,
                HeroId       = s.Hero.HeroId,
                s.Kills, s.Deaths, s.Assists,
                s.Gpm, s.Xpm,
                s.NetWorth, s.HeroDamage,
                s.TowerDamage, s.LastHits, s.Denies
            })
            .ToListAsync();
 
        var playerStatsFormatted = playerStats.Select(s => new
        {
            s.PlayerId,
            s.TeamId,
            s.PlayerSlot,
            s.Nickname,
            heroName    = s.HeroName,
            s.Kills, s.Deaths, s.Assists,
            kda         = Math.Round((double)(s.Kills + s.Assists) / Math.Max(1, s.Deaths), 2),
            s.Gpm, s.Xpm,
            netWorth    = s.NetWorth,
            heroDamage  = s.HeroDamage,
            towerDamage = s.TowerDamage,
            s.LastHits, s.Denies,
            side = s.TeamId == radiantTeam?.TeamId ? "Radiant" : "Dire"
        }).ToList();
 
        var radiantPlayers = playerStatsFormatted.Where(p => p.side == "Radiant").ToList();
        var direPlayers    = playerStatsFormatted.Where(p => p.side == "Dire").ToList();
 
        int radiantNetWorth = radiantPlayers.Sum(p => p.netWorth);
        int direNetWorth    = direPlayers.Sum(p => p.netWorth);
        int netWorthDiff    = direNetWorth - radiantNetWorth;
 
        var heroIds    = playerStats.Select(s => s.HeroId).Distinct().ToList();
        var heroRoles  = await _context.HeroRoles
            .Where(hr => heroIds.Contains(hr.HeroId))
            .Select(hr => new { hr.HeroId, hr.Role.RoleName })
            .ToListAsync();
 
        double durationMin = (match.Duration ?? 0) / 60.0;
        int radiantTotalXp = (int)radiantPlayers.Sum(p => p.Xpm * durationMin);
        int direTotalXp    = (int)direPlayers.Sum(p => p.Xpm * durationMin);
 
        return new
        {
            info = new
            {
                match.MatchId,
                match.MatchDate,
                match.Duration,
                durationMin    = Math.Round(durationMin, 1),
                tournamentName = match.Tournament?.Name,
                tournamentId   = match.TournamentId
            },
            radiantTeam = radiantTeam == null ? null : new
            {
                radiantTeam.TeamId,
                radiantTeam.TeamName,
                radiantTeam.LogoPath,
                radiantTeam.Acronym,
                radiantTeam.Score,
                radiantTeam.IsWinner
            },
            direTeam = direTeam == null ? null : new
            {
                direTeam.TeamId,
                direTeam.TeamName,
                direTeam.LogoPath,
                direTeam.Acronym,
                direTeam.Score,
                direTeam.IsWinner
            },
            radiantPlayers,
            direPlayers,
            netWorthComparison = new
            {
                radiantTotal = radiantNetWorth,
                direTotal    = direNetWorth,
                diff         = netWorthDiff,
                leader       = netWorthDiff > 0 ? "Dire" : "Radiant",
                leaderAmount = Math.Abs(netWorthDiff),
                leadMatchesWinner = (netWorthDiff > 0) == (direTeam?.IsWinner ?? false)
            },
            goldXpComparison = new
            {
                radiantNetWorth,
                direNetWorth,
                radiantTotalXp,
                direTotalXp
            },
            heroRoles
        };
    }
 
    public async Task<object> GetMatchAnalyticsAsync()
    {
        var totalMatches = await _context.Matches.CountAsync();
        if (totalMatches == 0) return new { totalMatches = 0 };
 
        var avgDurationSec   = await _context.Matches.AverageAsync(m => (double?)m.Duration) ?? 0;
        var totalKills       = await _context.PlayerMatchStats.SumAsync(s => s.Kills);
        var totalDurationMin = await _context.Matches.SumAsync(m => (double)(m.Duration ?? 0) / 60.0);
        var radiantWins      = await _context.MatchTeams.CountAsync(mt => mt.Side == "Radiant" && mt.IsWinner);
 
        var durations = await _context.Matches
            .Select(m => (m.Duration ?? 0) / 60)
            .ToListAsync();
 
        var buckets = new (int from, int to, string label)[]
        {
            (0, 10, "0-10"), (10, 20, "10-20"), (20, 30, "20-30"),
            (30, 40, "30-40"), (40, 50, "40-50"), (50, 60, "50-60"),
            (60, 70, "60-70"), (70, 80, "70-80"), (80, 90, "80-90"),
            (90, int.MaxValue, "90+")
        };
 
        var distribution = buckets
            .Select(b => new
            {
                range = $"{b.label} min",
                count = durations.Count(d => d >= b.from && d < b.to)
            })
            .ToList();
 
        var longest = await _context.Matches
            .OrderByDescending(m => m.Duration)
            .Select(m => new { id = m.MatchId, val = (double)(m.Duration ?? 0), label = "The Marathon" })
            .FirstOrDefaultAsync();
 
        var shortest = await _context.Matches
            .Where(m => m.Duration > 600)
            .OrderBy(m => m.Duration)
            .Select(m => new { id = m.MatchId, val = (double)(m.Duration ?? 0), label = "The Stomp" })
            .FirstOrDefaultAsync();
 
        var bloodiest = await _context.PlayerMatchStats
            .GroupBy(s => s.MatchId)
            .Select(g => new { id = g.Key, val = (double)g.Sum(s => s.Kills), label = "The Bloodbath" })
            .OrderByDescending(x => x.val)
            .FirstOrDefaultAsync();
 
        var allPlayerStats = await _context.PlayerMatchStats
            .Select(s => new { s.TeamId, s.Gpm, s.TowerDamage, s.HeroDamage })
            .ToListAsync();
 
        var radiantTeamIds = await _context.MatchTeams
            .Where(mt => mt.Side == "Radiant")
            .Select(mt => mt.TeamId)
            .Distinct()
            .ToListAsync();
 
        var direTeamIds = await _context.MatchTeams
            .Where(mt => mt.Side == "Dire")
            .Select(mt => mt.TeamId)
            .Distinct()
            .ToListAsync();
 
        var radiantStatsAll = allPlayerStats
            .Where(s => s.TeamId.HasValue && radiantTeamIds.Contains(s.TeamId.Value))
            .ToList();
        var direStatsAll = allPlayerStats
            .Where(s => s.TeamId.HasValue && direTeamIds.Contains(s.TeamId.Value))
            .ToList();
 
        var factionStats = new[]
        {
            new
            {
                side         = "Radiant",
                avgGpm       = radiantStatsAll.Count > 0 ? Math.Round(radiantStatsAll.Average(s => (double)s.Gpm), 1)         : 0.0,
                avgTowerDmg  = radiantStatsAll.Count > 0 ? Math.Round(radiantStatsAll.Average(s => (double)s.TowerDamage), 0) : 0.0
            },
            new
            {
                side         = "Dire",
                avgGpm       = direStatsAll.Count > 0 ? Math.Round(direStatsAll.Average(s => (double)s.Gpm), 1)         : 0.0,
                avgTowerDmg  = direStatsAll.Count > 0 ? Math.Round(direStatsAll.Average(s => (double)s.TowerDamage), 0) : 0.0
            }
        };
        
        var matchNetWorthRaw = await _context.MatchTeams
            .Select(mt => new { mt.MatchId, mt.TeamId, mt.Side, mt.IsWinner })
            .ToListAsync();
 
        var playerNwRaw = await _context.PlayerMatchStats
            .Select(s => new { s.MatchId, s.TeamId, s.NetWorth })
            .ToListAsync();
 
        var matchIds = matchNetWorthRaw.Select(x => x.MatchId).Distinct().ToList();
        int nwLeaderWon = 0, nwLeaderLost = 0;
 
        foreach (var matchId in matchIds)
        {
            var teams = matchNetWorthRaw.Where(x => x.MatchId == matchId).ToList();
            if (teams.Count < 2) continue;
 
            var nwBySide = teams.Select(t => new
            {
                t.Side,
                t.IsWinner,
                nw = playerNwRaw
                    .Where(p => p.MatchId == matchId && p.TeamId == t.TeamId)
                    .Sum(p => p.NetWorth)
            }).ToList();
 
            if (nwBySide.Count < 2) continue;
            var leader = nwBySide.OrderByDescending(x => x.nw).First();
            if (leader.IsWinner) nwLeaderWon++;
            else nwLeaderLost++;
        }
 
        int nwTotal = nwLeaderWon + nwLeaderLost;
        double nwWinCorrelation = nwTotal > 0
            ? Math.Round((double)nwLeaderWon / nwTotal * 100, 1) : 0.0;
 
        return new
        {
            totalMatches,
            metaPulse = new
            {
                avgDuration    = Math.Round(avgDurationSec / 60, 1),
                kpm            = totalDurationMin > 0 ? Math.Round(totalKills / totalDurationMin, 2) : 0.0,
                radiantWinRate = Math.Round((double)radiantWins / totalMatches * 100, 1),
                direWinRate    = Math.Round(100 - (double)radiantWins / totalMatches * 100, 1)
            },
            distribution,
            records = new[] { longest, shortest, bloodiest },
            factionStats,
            nwWinCorrelation  
        };
    }
}