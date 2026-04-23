using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Models;
using CsgoPredictionSystem.DTO;
using CsgoPredictionSystem.Hubs;
using CsgoPredictionSystem.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace CsgoPredictionSystem.Services;

public class TournamentService
{
    private readonly DotaDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly ILogger<TournamentService> _logger;

    public TournamentService(DotaDbContext context, IHttpClientFactory httpClientFactory, IHubContext<SyncHub> hubContext, ILogger<TournamentService> logger)
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
            syncType = "Tournaments",
            status = $"{progress}% - {message}",
            lastRunAt = DateTime.UtcNow
        });
    }

    public async Task<string> SyncTournamentsFromApi(int count, string[] tiers)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "DotaAnalyticsApp");

            await Notify("Query generation to Explorer API...", 10);

            string tierFilter = "";
            if (tiers != null && tiers.Length > 0 && !tiers.Contains("all"))
            {
                var formattedTiers = string.Join(",", tiers.Select(t => $"'{t.ToLower().Trim()}'"));
                tierFilter = $"WHERE tier IN ({formattedTiers})";
            }

            string sql = $@"
                SELECT leagueid, name, tier 
                FROM leagues 
                {tierFilter} 
                ORDER BY leagueid DESC 
                LIMIT {count}";

            var encodedSql = Uri.EscapeDataString(sql);
            var url = $"https://api.opendota.com/api/explorer?sql={encodedSql}";

            await Notify("Getting tournament data...", 30);
            var response = await client.GetStringAsync(url);
            var explorerData = JsonConvert.DeserializeObject<ExplorerResponse<LeagueApiDto>>(response);

            if (explorerData?.Rows == null) return "Error: API returned an empty result.";

            var leaguesToProcess = explorerData.Rows;
            var leagueIds = leaguesToProcess.Select(l => l.LeagueId).ToList();

            var existingTournaments = await _context.Tournaments
                .Where(t => t.ExternalId.HasValue && leagueIds.Contains(t.ExternalId.Value))
                .ToDictionaryAsync(t => t.ExternalId!.Value);

            int added = 0, updated = 0;
            await Notify("Saving tournaments to the base...", 70);

            foreach (var l in leaguesToProcess)
            {
                if (existingTournaments.TryGetValue(l.LeagueId, out var existing))
                {
                    existing.Name = l.Name?.Length > 255 ? l.Name.Substring(0, 255) : (l.Name ?? existing.Name);
                    existing.Tier = l.Tier ?? existing.Tier;
                    updated++;
                }
                else
                {
                    _context.Tournaments.Add(new Tournament {
                        ExternalId = l.LeagueId,
                        Name = l.Name?.Length > 255 ? l.Name.Substring(0, 255) : (l.Name ?? "Unknown League"),
                        Tier = l.Tier ?? "N/A"
                    });
                    added++;
                }
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            await Notify("Tournament synchronization is complete!", 100);
            return $"Successful: added {added}, updated {updated}.";
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Tournament synchronization error");
            
            string friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            await Notify(friendlyError, 0);
            throw new Exception(friendlyError);
        }
    }
 
    public async Task<object> GetTournamentsPaged(
        int page, int pageSize,
        string? search, int? tier,
        DateTime? fromDate, DateTime? toDate,
        string sortOrder = "desc")
    {
        var query = _context.Tournaments.AsQueryable();
 
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(t => t.Name.ToLower().Contains(search.ToLower().Trim()));
 
        if (tier.HasValue && tier.Value > 0)
        {
            string? target = tier.Value switch
            {
                1 => "premium",
                2 => "professional",
                3 => "amateur",
                _ => null
            };
            if (target != null)
                query = query.Where(t => t.Tier.ToLower().Contains(target));
        }
 
        if (fromDate.HasValue)
            query = query.Where(t => t.Matches.Any(m => m.MatchDate >= fromDate.Value));
 
        if (toDate.HasValue)
            query = query.Where(t => t.Matches.Any(m => m.MatchDate <= toDate.Value));
 
        var totalItems = await query.CountAsync();
 
        var baseQuery = query.Select(t => new
        {
            tournamentId  = t.TournamentId,
            name          = t.Name,
            tier          = t.Tier,
            tierId        = t.Tier.ToLower().Contains("premium") ? 1
                          : t.Tier.ToLower().Contains("professional") ? 2 : 3,
            matchCount    = t.Matches.Count,
            lastMatchDate = t.Matches.Max(m => (DateTime?)m.MatchDate),
            externalId    = t.ExternalId
        });
 
        baseQuery = sortOrder.ToLower() == "asc"
            ? baseQuery.OrderBy(x => x.matchCount).ThenBy(x => x.tournamentId)
            : baseQuery.OrderByDescending(x => x.matchCount).ThenByDescending(x => x.tournamentId);
 
        var items = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
 
        return new { total = totalItems, page, pageSize, items };
    }
    
 
    public async Task<object> GetTournamentDashboardAsync()
    {
        var totalMatches     = await _context.Matches.CountAsync();
        var totalTournaments = await _context.Tournaments.CountAsync();
 
        var tierDistribution = await _context.Tournaments
            .GroupBy(t => t.Tier)
            .Select(g => new { name = g.Key ?? "Unknown", value = g.Count() })
            .OrderByDescending(x => x.value)
            .ToListAsync();
 
        var activityStart = DateTime.UtcNow.AddDays(-14).Date;
        var activityRaw = await _context.Matches
            .Where(m => m.MatchDate >= activityStart)
            .GroupBy(m => m.MatchDate.Date)
            .Select(g => new { date = g.Key, count = g.Count() })
            .OrderBy(g => g.date)
            .ToListAsync();
 
        var activityTrend = Enumerable.Range(0, 14)
            .Select(i => activityStart.AddDays(i))
            .Select(d => new
            {
                date  = d.ToString("dd MMM"),
                count = activityRaw.FirstOrDefault(x => x.date == d)?.count ?? 0
            })
            .ToList();
 
        var topTournamentsByMatches = await _context.Tournaments
            .Select(t => new
            {
                name       = t.Name,
                matchCount = t.Matches.Count,
                tier       = t.Tier,
                tierId     = t.Tier.ToLower().Contains("premium") ? 1
                           : t.Tier.ToLower().Contains("professional") ? 2 : 3
            })
            .Where(t => t.matchCount > 0)
            .OrderByDescending(t => t.matchCount)
            .Take(5)
            .ToListAsync();
 
        var topTeams = await _context.MatchTeams
            .Where(mt => mt.IsWinner)
            .GroupBy(mt => new { mt.Team.TeamId, mt.Team.TeamName, mt.Team.LogoPath })
            .Select(g => new
            {
                name = g.Key.TeamName,
                logo = g.Key.LogoPath,
                wins = g.Count()
            })
            .OrderByDescending(g => g.wins)
            .Take(5)
            .ToListAsync();
 
        var radiantWins = await _context.MatchTeams
            .CountAsync(mt => mt.Side == "Radiant" && mt.IsWinner);
 
        var radiantWinRate = totalMatches > 0
            ? Math.Round((double)radiantWins / totalMatches * 100, 1)
            : 50.0;
 
        var avgDurationSec = await _context.Matches
            .AverageAsync(m => (double?)m.Duration) ?? 0;
 
        var allDurations = await _context.Matches
            .Select(m => m.Duration / 60)
            .ToListAsync();
 
        var durationBuckets = new (int from, int to, string label)[]
        {
            (0, 20, "<20 хв"), (20, 30, "20-30"), (30, 40, "30-40"),
            (40, 50, "40-50"), (50, 60, "50-60"), (60, 75, "60-75"), (75, int.MaxValue, "75+")
        };
        var durationDistribution = durationBuckets
            .Select(b => (object)new
            {
                range = b.label,
                count = allDurations.Count(d => d >= b.from && d < b.to)
            })
            .ToList();
 
        var weekdayRaw = await _context.Matches
            .GroupBy(m => m.MatchDate.DayOfWeek)
            .Select(g => new { day = g.Key, count = g.Count() })
            .ToListAsync();
 
        var weekdayLabels = new[] { "Нд", "Пн", "Вт", "Ср", "Чт", "Пт", "Сб" };
        var weekdayActivity = weekdayLabels
            .Select((label, i) => new
            {
                day   = label,
                count = weekdayRaw.FirstOrDefault(x => (int)x.day == i)?.count ?? 0
            })
            .ToList();
 
        var recentTournamentsCount = await _context.Tournaments
            .CountAsync(t => t.Matches.Any(m => m.MatchDate >= DateTime.UtcNow.AddDays(-30)));
 
        return new
        {
            summary = new
            {
                totalTournaments,
                totalMatches,
                avgDurationMin         = Math.Round(avgDurationSec / 60, 1),
                radiantWinRate,
                direWinRate            = Math.Round(100 - radiantWinRate, 1),
                recentTournamentsCount
            },
            tierDistribution,
            activityTrend,
            topTournamentsByMatches,
            topTeams,
            sideStats = new
            {
                radiantWinRate,
                direWinRate = Math.Round(100 - radiantWinRate, 1)
            },
            durationDistribution,
            weekdayActivity
        };
    }
 
 
    public async Task<object?> GetTournamentDetailsAsync(int id)
    {
        var tournament = await _context.Tournaments
            .Select(t => new
            {
                t.TournamentId,
                t.Name,
                t.Tier,
                t.ExternalId,
                TotalMatches = t.Matches.Count
            })
            .FirstOrDefaultAsync(t => t.TournamentId == id);
 
        if (tournament == null) return null;
 
        var radiantWins = await _context.MatchTeams
            .CountAsync(mt => mt.Match.TournamentId == id && mt.Side == "Radiant" && mt.IsWinner);
 
        var avgDur = await _context.Matches
            .Where(m => m.TournamentId == id)
            .AverageAsync(m => (double?)m.Duration) ?? 0;
 
        var teamsCount = await _context.MatchTeams
            .Where(mt => mt.Match.TournamentId == id)
            .Select(mt => mt.TeamId)
            .Distinct()
            .CountAsync();
 
        var playersCount = await _context.PlayerMatchStats
            .Where(s => s.Match.TournamentId == id)
            .Select(s => s.PlayerId)
            .Distinct()
            .CountAsync();
 
        var radiantWinRate = tournament.TotalMatches > 0
            ? Math.Round((double)radiantWins / tournament.TotalMatches * 100, 1)
            : 50.0;
 
        return new
        {
            info = new
            {
                tournament.TournamentId,
                tournament.Name,
                tournament.Tier,
                tournament.ExternalId,
                tournament.TotalMatches,
                teamsCount,
                playersCount
            },
            overview = new
            {
                radiantWinRate,
                direWinRate = Math.Round(100 - radiantWinRate, 1),
                avgDuration = Math.Round(avgDur / 60, 1)
            }
        };
    }
    public async Task<object?> GetTournamentAnalyticsAsync(int id)
    {
        var totalMatches = await _context.Matches.CountAsync(m => m.TournamentId == id);
        if (totalMatches == 0) return null;
 
        var records      = await GetTournamentHallOfFame(id);
        var heroStats    = await GetTournamentHeroStats(id, totalMatches);
        var playstyles   = await GetTournamentPlaystyles(id);
        var activity     = await GetTournamentActivityTrend(id);
        var durationDist = await GetTournamentDurationDistribution(id);
        var killsDist    = await GetTournamentKillsDistribution(id);
        var h2h          = await GetTopHeadToHead(id);
        var scoreStats   = await GetTournamentScoreStats(id);
 
        var radiantWins = await _context.MatchTeams
            .CountAsync(mt => mt.Match.TournamentId == id && mt.Side == "Radiant" && mt.IsWinner);
 
        var radiantWinRate = totalMatches > 0
            ? Math.Round((double)radiantWins / totalMatches * 100, 1)
            : 50.0;
 
        var avgDur = await _context.Matches
            .Where(m => m.TournamentId == id)
            .AverageAsync(m => (double?)m.Duration) ?? 0;
 
        var avgKillsPerGame = await _context.PlayerMatchStats
            .Where(s => s.Match.TournamentId == id)
            .GroupBy(s => s.MatchId)
            .Select(g => (double)g.Sum(x => x.Kills))
            .AverageAsync() is double avg ? Math.Round(avg, 1) : 0.0;
 
        return new
        {
            radiantWinRate,
            direWinRate          = Math.Round(100 - radiantWinRate, 1),
            avgDuration          = Math.Round(avgDur / 60, 1),
            avgKillsPerGame,
            records,
            teamPlaystyles       = playstyles,
            mostPickedHeroes     = heroStats.MostPicked,
            mostSuccessfulHeroes = heroStats.MostSuccessful,
            activityTrend        = activity,
            durationDistribution = durationDist,
            killsDistribution    = killsDist,
            topHeadToHead        = h2h,
            scoreStats
        };
    }
 
     public async Task<object> GetTournamentTeamsPaged(
        int id, string? search, string sortOrder, int page, int size)
    {
        var raw = await _context.MatchTeams
            .Where(mt => mt.Match.TournamentId == id)
            .Select(mt => new
            {
                TeamId      = mt.Team.TeamId,
                TeamName    = mt.Team.TeamName,
                LogoPath    = mt.Team.LogoPath,
                CurrentRank = mt.Team.CurrentRank,
                mt.IsWinner,
                mt.Score
            })
            .ToListAsync();  
 
        var grouped = raw
            .GroupBy(mt => new { mt.TeamId, mt.TeamName, mt.LogoPath, mt.CurrentRank })
            .Select(g =>
            {
                int games  = g.Count();
                int wins   = g.Count(x => x.IsWinner);
                int losses = games - wins;
                double winRate  = games > 0 ? Math.Round((double)wins / games * 100, 1) : 0.0;
                double avgScore = games > 0 ? Math.Round(g.Average(x => (double)x.Score), 1) : 0.0;
 
                return new
                {
                    id          = g.Key.TeamId,
                    name        = g.Key.TeamName,
                    logo        = g.Key.LogoPath,
                    rank        = g.Key.CurrentRank,
                    wins,
                    losses,
                    gamesPlayed = games,
                    winRate,
                    avgScore
                };
            })
            .AsQueryable();
 
        if (!string.IsNullOrWhiteSpace(search))
            grouped = grouped.Where(t => t.name.ToLower().Contains(search.ToLower()));
 
        grouped = sortOrder.ToLower() == "asc"
            ? grouped.OrderBy(t => t.winRate)
            : grouped.OrderByDescending(t => t.winRate);
 
        var all   = grouped.ToList();
        var total = all.Count;
        var items = all.Skip((page - 1) * size).Take(size).ToList();
 
        return new { total, items };
    }
 
    public async Task<object> GetTournamentMatchesPaged(
        int id, DateTime? from, DateTime? to, string sortOrder, int page, int size)
    {
        var query = _context.Matches
            .Where(m => m.TournamentId == id)
            .AsQueryable();
 
        if (from.HasValue) query = query.Where(m => m.MatchDate >= from.Value);
        if (to.HasValue)   query = query.Where(m => m.MatchDate <= to.Value);
 
        query = sortOrder.ToLower() == "asc"
            ? query.OrderBy(m => m.Duration)
            : query.OrderByDescending(m => m.MatchDate);
 
        var total = await query.CountAsync();
 
        var items = await query
            .Skip((page - 1) * size)
            .Take(size)
            .Select(m => new
            {
                id           = m.MatchId,
                playedAt     = m.MatchDate,
                duration     = m.Duration,
                radiantTeam  = m.MatchTeams
                    .Where(mt => mt.Side == "Radiant")
                    .Select(mt => new { name = mt.Team.TeamName, logo = mt.Team.LogoPath })
                    .FirstOrDefault(),
                direTeam     = m.MatchTeams
                    .Where(mt => mt.Side == "Dire")
                    .Select(mt => new { name = mt.Team.TeamName, logo = mt.Team.LogoPath })
                    .FirstOrDefault(),
                radiantScore = m.MatchTeams
                    .Where(mt => mt.Side == "Radiant")
                    .Select(mt => mt.Score)
                    .FirstOrDefault(),
                direScore    = m.MatchTeams
                    .Where(mt => mt.Side == "Dire")
                    .Select(mt => mt.Score)
                    .FirstOrDefault(),
                winner       = m.MatchTeams.Any(mt => mt.Side == "Radiant" && mt.IsWinner)
                               ? "radiant" : "dire",
                totalKills   = m.MatchTeams.Sum(mt => mt.Score)
            })
            .ToListAsync();
 
        return new { total, items };
    }
 
    private async Task<object> GetTournamentHallOfFame(int id)
    {
        var stats   = _context.PlayerMatchStats.Where(s => s.Match.TournamentId == id);
        var matches = _context.Matches.Where(m => m.TournamentId == id);
 
        var longestGame = await matches
            .OrderByDescending(m => m.Duration)
            .Select(m => new { value = m.Duration, matchId = m.MatchId })
            .FirstOrDefaultAsync();
 
        var shortestGame = await matches
            .Where(m => m.Duration > 600)
            .OrderBy(m => m.Duration)
            .Select(m => new { value = m.Duration, matchId = m.MatchId })
            .FirstOrDefaultAsync();
 
        var maxKills = await stats
            .OrderByDescending(s => s.Kills)
            .Select(s => new
            {
                value   = (double)s.Kills,
                matchId = s.MatchId,
                hero    = s.Hero.LocalizedName,
                player  = s.Player.Nickname
            })
            .FirstOrDefaultAsync();
 
        var highestGpm = await stats
            .OrderByDescending(s => s.Gpm)
            .Select(s => new
            {
                value   = (double)s.Gpm,
                matchId = s.MatchId,
                hero    = s.Hero.LocalizedName,
                player  = s.Player.Nickname
            })
            .FirstOrDefaultAsync();
 
        var maxTowerDamage = await stats
            .OrderByDescending(s => s.TowerDamage)
            .Select(s => new
            {
                value   = (double)s.TowerDamage,
                matchId = s.MatchId,
                hero    = s.Hero.LocalizedName,
                player  = s.Player.Nickname
            })
            .FirstOrDefaultAsync();
 
        var bloodiestMatch = await stats
            .GroupBy(s => s.MatchId)
            .Select(g => new { matchId = g.Key, value = (double)g.Sum(x => x.Kills) })
            .OrderByDescending(x => x.value)
            .FirstOrDefaultAsync();
 
        var maxAssists = await stats
            .OrderByDescending(s => s.Assists)
            .Select(s => new
            {
                value   = (double)s.Assists,
                matchId = s.MatchId,
                hero    = s.Hero.LocalizedName,
                player  = s.Player.Nickname
            })
            .FirstOrDefaultAsync();
 
        var highestXpm = await stats
            .OrderByDescending(s => s.Xpm)
            .Select(s => new
            {
                value   = (double)s.Xpm,
                matchId = s.MatchId,
                hero    = s.Hero.LocalizedName,
                player  = s.Player.Nickname
            })
            .FirstOrDefaultAsync();
 
        return new
        {
            longestGame,
            shortestGame,
            maxKills,
            highestGpm,
            maxTowerDamage,
            bloodiestMatch,
            maxAssists,
            highestXpm
        };
    }
 
    private async Task<(List<object> MostPicked, List<object> MostSuccessful)>
        GetTournamentHeroStats(int id, int totalMatches)
    {
        var stats = await _context.PlayerMatchStats
            .Where(s => s.Match.TournamentId == id)
            .Select(s => new { s.Hero.LocalizedName, s.MatchId, s.TeamId })
            .ToListAsync();
 
        var winners = await _context.MatchTeams
            .Where(mt => mt.Match.TournamentId == id && mt.IsWinner)
            .Select(mt => new { mt.MatchId, mt.TeamId })
            .ToListAsync();
 
        var heroAnalysis = stats
            .GroupBy(s => s.LocalizedName)
            .Select(g =>
            {
                int picks = g.Count();
                int wins  = g.Count(p => winners.Any(w => w.MatchId == p.MatchId && w.TeamId == p.TeamId));
                return new
                {
                    name     = g.Key,
                    count    = picks,
                    wins,
                    pickRate = totalMatches > 0
                               ? Math.Round((double)picks / totalMatches * 100, 1) : 0.0,
                    winRate  = picks > 0
                               ? Math.Round((double)wins / picks * 100, 1) : 0.0
                };
            })
            .ToList();
 
        return (
            heroAnalysis.OrderByDescending(x => x.count).Take(10).Cast<object>().ToList(),
            heroAnalysis.Where(x => x.count >= 2).OrderByDescending(x => x.winRate).Take(10).Cast<object>().ToList()
        );
    }
 
    private async Task<List<object>> GetTournamentPlaystyles(int id)
    {
        var raw = await _context.MatchTeams
            .Where(mt => mt.Match.TournamentId == id)
            .Select(mt => new
            {
                mt.Team.TeamId,
                mt.Team.TeamName,
                mt.Score,
                DurationMin = (double)mt.Match.Duration / 60.0,
                mt.IsWinner
            })
            .ToListAsync();
 
        var playerStats = await _context.PlayerMatchStats
            .Where(ps => ps.Match.TournamentId == id)
            .Select(ps => new
            {
                ps.TeamId,
                ps.Gpm,
                ps.Xpm,
                ps.HeroDamage,
                ps.TowerDamage,
                ps.Deaths
            })
            .ToListAsync();
 
        return raw
            .GroupBy(x => new { x.TeamId, x.TeamName })
            .Select(g =>
            {
                var ts       = playerStats.Where(p => p.TeamId == g.Key.TeamId).ToList();
                int games    = g.Count();
                int wins     = g.Count(x => x.IsWinner);
                double totDur = g.Sum(x => x.DurationMin);
 
                double kpm      = totDur > 0 ? g.Sum(x => x.Score) / totDur : 0;
                double avgGpm   = ts.Count > 0 ? ts.Average(p => (double)p.Gpm) : 0;
                double avgXpm   = ts.Count > 0 ? ts.Average(p => (double)p.Xpm) : 0;
                double avgDmg   = ts.Count > 0 ? ts.Average(p => (double)p.HeroDamage) : 0;
                double avgTower = ts.Count > 0 ? ts.Average(p => (double)p.TowerDamage) : 0;
                double avgDur   = games > 0 ? g.Average(x => x.DurationMin) : 0;
                double winRate  = games > 0 ? Math.Round((double)wins / games * 100, 1) : 0;
 
                string tag = kpm > 0.8 ? "Aggressive"
                           : avgGpm > 600 ? "Farmers"
                           : avgDur < 32 ? "Fast Push"
                           : "Balanced";
 
                return (object)new
                {
                    teamName    = g.Key.TeamName,
                    tag,
                    kpm         = Math.Round(kpm, 2),
                    avgGpm      = Math.Round(avgGpm, 1),
                    avgXpm      = Math.Round(avgXpm, 1),
                    avgHeroDmg  = Math.Round(avgDmg, 0),
                    avgTowerDmg = Math.Round(avgTower, 0),
                    avgDurMin   = Math.Round(avgDur, 1),
                    winRate,
                    games
                };
            })
            .Where(x => ((dynamic)x).games >= 2)
            .OrderByDescending(x => ((dynamic)x).avgGpm)
            .Take(8)
            .ToList();
    }
 
    private async Task<List<object>> GetTournamentActivityTrend(int id)
    {
        var first = await _context.Matches
            .Where(m => m.TournamentId == id)
            .MinAsync(m => (DateTime?)m.MatchDate);
 
        if (first == null) return new List<object>();
 
        var raw = await _context.Matches
            .Where(m => m.TournamentId == id)
            .GroupBy(m => m.MatchDate.Date)
            .Select(g => new { date = g.Key, count = g.Count() })
            .OrderBy(g => g.date)
            .ToListAsync();
 
        var spanDays = (DateTime.UtcNow - first.Value).TotalDays;
        if (spanDays > 60)
        {
            return raw
                .GroupBy(x =>
                {
                    var d = x.date;
                    return d.AddDays(-(int)d.DayOfWeek);
                })
                .Select(g => (object)new
                {
                    date  = g.Key.ToString("dd MMM"),
                    count = g.Sum(x => x.count)
                })
                .ToList();
        }
 
        return raw.Select(g => (object)new
        {
            date  = g.date.ToString("dd MMM"),
            count = g.count
        }).ToList();
    }
 
    private async Task<List<object>> GetTournamentDurationDistribution(int id)
    {
        var durations = await _context.Matches
            .Where(m => m.TournamentId == id)
            .Select(m => m.Duration / 60)
            .ToListAsync();
 
        var buckets = new (int from, int to, string label)[]
        {
            (0, 20, "<20 хв"), (20, 30, "20-30"), (30, 40, "30-40"),
            (40, 50, "40-50"), (50, 60, "50-60"), (60, 75, "60-75"), (75, int.MaxValue, "75+ хв")
        };
 
        return buckets
            .Select(b => (object)new
            {
                range = b.label,
                count = durations.Count(d => d >= b.from && d < b.to)
            })
            .ToList();
    }
 
    private async Task<List<object>> GetTournamentKillsDistribution(int id)
    {
        var matchKills = await _context.PlayerMatchStats
            .Where(s => s.Match.TournamentId == id)
            .GroupBy(s => s.MatchId)
            .Select(g => g.Sum(x => x.Kills))
            .ToListAsync();
 
        if (!matchKills.Any()) return new List<object>();
 
        var buckets = new (int from, int to, string label)[]
        {
            (0, 20, "0-20"), (20, 35, "20-35"), (35, 50, "35-50"),
            (50, 70, "50-70"), (70, 100, "70-100"), (100, int.MaxValue, "100+")
        };
 
        return buckets
            .Select(b => (object)new
            {
                range = b.label,
                count = matchKills.Count(k => k >= b.from && k < b.to)
            })
            .ToList();
    }
 
    private async Task<List<object>> GetTopHeadToHead(int id)
    {
        var matches = await _context.Matches
            .Where(m => m.TournamentId == id)
            .Select(m => new
            {
                m.MatchId,
                Teams = m.MatchTeams
                    .Select(mt => new { mt.Team.TeamName, mt.IsWinner })
                    .ToList()
            })
            .ToListAsync();
 
        var pairs = new Dictionary<string, (int total, int firstWins)>();
 
        foreach (var match in matches)
        {
            if (match.Teams.Count < 2) continue;
            var sorted = match.Teams.OrderBy(t => t.TeamName).ToList();
            string key    = $"{sorted[0].TeamName}|||{sorted[1].TeamName}";
            bool firstWon = sorted[0].IsWinner;
 
            if (pairs.TryGetValue(key, out var e))
                pairs[key] = (e.total + 1, e.firstWins + (firstWon ? 1 : 0));
            else
                pairs[key] = (1, firstWon ? 1 : 0);
        }
 
        return pairs
            .Where(p => p.Value.total >= 2)
            .OrderByDescending(p => p.Value.total)
            .Take(5)
            .Select(p =>
            {
                var teams = p.Key.Split("|||");
                return (object)new
                {
                    team1     = teams[0],
                    team2     = teams[1],
                    total     = p.Value.total,
                    team1Wins = p.Value.firstWins,
                    team2Wins = p.Value.total - p.Value.firstWins
                };
            })
            .ToList();
    }
 
    private async Task<object> GetTournamentScoreStats(int id)
    {
        var scores = await _context.MatchTeams
            .Where(mt => mt.Match.TournamentId == id)
            .Select(mt => new { mt.Score, mt.IsWinner })
            .ToListAsync();
 
        if (!scores.Any())
            return new { avgWinnerScore = 0.0, avgLoserScore = 0.0, avgScoreDiff = 0.0 };
 
        var winnerScores = scores.Where(s => s.IsWinner).Select(s => (double)s.Score).ToList();
        var loserScores  = scores.Where(s => !s.IsWinner).Select(s => (double)s.Score).ToList();
 
        double avgWinner = winnerScores.Any() ? Math.Round(winnerScores.Average(), 1) : 0;
        double avgLoser  = loserScores.Any()  ? Math.Round(loserScores.Average(), 1)  : 0;
 
        return new
        {
            avgWinnerScore = avgWinner,
            avgLoserScore  = avgLoser,
            avgScoreDiff   = Math.Round(avgWinner - avgLoser, 1)
        };
    }

}
