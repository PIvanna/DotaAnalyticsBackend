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

    public async Task<object> GetRecentMatches(int count)
    {
        return await _context.Matches
            .Include(m => m.Tournament)
            .Include(m => m.MatchTeams).ThenInclude(mt => mt.Team)
            .OrderByDescending(m => m.MatchDate)
            .Take(count)
            .ToListAsync();
    }
    
public async Task<object> GetMatchesPagedAsync(int page, int pageSize, int? teamId, int? tournamentId, DateTime? from, DateTime? to)
{
    var query = _context.Matches
        .Include(m => m.Tournament)
        .Include(m => m.MatchTeams).ThenInclude(mt => mt.Team)
        .AsQueryable();

    if (teamId.HasValue) query = query.Where(m => m.MatchTeams.Any(mt => mt.TeamId == teamId));
    if (tournamentId.HasValue) query = query.Where(m => m.TournamentId == tournamentId);
    if (from.HasValue) query = query.Where(m => m.MatchDate >= from.Value);
    if (to.HasValue) query = query.Where(m => m.MatchDate <= to.Value);

    var total = await query.CountAsync();
    var items = await query
        .OrderByDescending(m => m.MatchDate)
        .Skip((page - 1) * pageSize).Take(pageSize)
        .Select(m => new {
            m.MatchId,
            m.MatchDate,
            m.Duration,
            tournamentName = m.Tournament.Name,
            radiant = m.MatchTeams.Where(mt => mt.Side == "Radiant").Select(mt => new { mt.Team.TeamName, mt.Team.LogoPath, mt.Score, mt.IsWinner }).FirstOrDefault(),
            dire = m.MatchTeams.Where(mt => mt.Side == "Dire").Select(mt => new { mt.Team.TeamName, mt.Team.LogoPath, mt.Score, mt.IsWinner }).FirstOrDefault()
        }).ToListAsync();

    return new { total, items };
}

public async Task<object> GetMatchDashboardAsync()
{
    var totalMatches = await _context.Matches.CountAsync();
    return new {
        totalMatches,
        avgDuration = Math.Round((await _context.Matches.AverageAsync(m => (double?)m.Duration) ?? 0) / 60, 1),
        maxKillsGame = await _context.PlayerMatchStats
            .GroupBy(s => s.MatchId)
            .Select(g => new { id = g.Key, kills = g.Sum(s => s.Kills) })
            .OrderByDescending(x => x.kills).FirstOrDefaultAsync(),
        radiantWinRate = Math.Round((double)await _context.MatchTeams.CountAsync(mt => mt.Side == "Radiant" && mt.IsWinner) / Math.Max(1, totalMatches) * 100, 1)
    };
}

    public async Task<object?> GetMatchDetailsAsync(int id)
    {
        var match = await _context.Matches
            .Include(m => m.Tournament)
            .FirstOrDefaultAsync(m => m.MatchId == id);

        if (match == null) return null;

        var playerStats = await _context.PlayerMatchStats
            .Where(s => s.MatchId == id)
            .Select(s => new {
                s.Player.Nickname,
                s.Hero.LocalizedName,
                heroIcon = s.Hero.HeroName,
                s.Kills, s.Deaths, s.Assists,
                s.Gpm, s.Xpm, s.NetWorth,
                s.HeroDamage, s.TowerDamage,
                side = _context.MatchTeams
                    .Where(mt => mt.MatchId == id && mt.TeamId == s.TeamId)
                    .Select(mt => mt.Side)
                    .FirstOrDefault()
            }).ToListAsync();

        return new {
            info = new {
                match.MatchId,
                match.MatchDate,
                match.Duration,
                tournamentName = match.Tournament?.Name,
                radiantScore = _context.MatchTeams.Where(mt => mt.MatchId == id && mt.Side == "Radiant").Select(mt => mt.Score).FirstOrDefault(),
                direScore = _context.MatchTeams.Where(mt => mt.MatchId == id && mt.Side == "Dire").Select(mt => mt.Score).FirstOrDefault()
            },
            radiantPlayers = playerStats.Where(p => p.side == "Radiant").ToList(),
            direPlayers = playerStats.Where(p => p.side == "Dire").ToList()
        };
    }
    
    public async Task<object> GetMatchAnalyticsAsync()
{
    var totalMatches = await _context.Matches.CountAsync();
    if (totalMatches == 0) return new { totalMatches = 0 };

    var avgDurationSeconds = await _context.Matches.AverageAsync(m => (double?)m.Duration) ?? 0;
    var totalKills = await _context.PlayerMatchStats.SumAsync(s => s.Kills);
    var totalDurationMinutes = await _context.Matches.SumAsync(m => (double)m.Duration / 60.0);
    
    var radiantWins = await _context.MatchTeams.CountAsync(mt => mt.Side == "Radiant" && mt.IsWinner);

    var distribution = await _context.Matches
        .Select(m => m.Duration / 60)
        .GroupBy(d => d / 10) 
        .Select(g => new { 
            range = $"{g.Key * 10}-{g.Key * 10 + 10} min", 
            count = g.Count(),
            sortKey = g.Key 
        })
        .OrderBy(x => x.sortKey)
        .ToListAsync();

    var longest = await _context.Matches.OrderByDescending(m => m.Duration)
        .Select(m => new { id = m.MatchId, val = Math.Round((double)m.Duration / 60, 1), label = "The Marathon" }).FirstOrDefaultAsync();
    
    var shortest = await _context.Matches.Where(m => m.Duration > 600).OrderBy(m => m.Duration)
        .Select(m => new { id = m.MatchId, val = Math.Round((double)m.Duration / 60, 1), label = "The Stomp" }).FirstOrDefaultAsync();

    var bloodiest = await _context.PlayerMatchStats
        .GroupBy(s => s.MatchId)
        .Select(g => new { id = g.Key, val = (double)g.Sum(s => s.Kills), label = "The Bloodbath" })
        .OrderByDescending(x => x.val).FirstOrDefaultAsync();

    var factionStats = await _context.MatchTeams
        .GroupBy(mt => mt.Side)
        .Select(g => new {
            side = g.Key,
            avgGpm = _context.PlayerMatchStats.Where(s => s.TeamId == g.First().TeamId).Average(s => (double?)s.Gpm) ?? 0,
            avgTowerDamage = _context.PlayerMatchStats.Where(s => s.TeamId == g.First().TeamId).Average(s => (double?)s.TowerDamage) ?? 0
        }).ToListAsync();

    return new {
        totalMatches,
        metaPulse = new {
            avgDuration = Math.Round(avgDurationSeconds / 60, 1),
            kpm = totalDurationMinutes > 0 ? Math.Round(totalKills / totalDurationMinutes, 2) : 0,
            radiantWinRate = Math.Round((double)radiantWins / totalMatches * 100, 1),
            direWinRate = Math.Round(100 - ((double)radiantWins / totalMatches * 100), 1)
        },
        distribution,
        records = new[] { longest, shortest, bloodiest },
        factionStats
    };
}
}