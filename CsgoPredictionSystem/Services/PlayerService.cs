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

public async Task<object> GetPlayersPagedAsync(int page, int pageSize, string? search, int? teamId, string? country, string sortBy = "nickname")
{
    var query = _context.Players.Include(p => p.Team).AsQueryable();

    if (!string.IsNullOrEmpty(search))
        query = query.Where(p => p.Nickname.ToLower().Contains(search.ToLower()));

    if (teamId.HasValue)
        query = query.Where(p => p.TeamId == teamId);

    if (!string.IsNullOrEmpty(country))
        query = query.Where(p => p.Country == country);

    query = sortBy.ToLower() switch {
        "gpm" => query.OrderByDescending(p => _context.PlayerMatchStats.Where(s => s.PlayerId == p.PlayerId).Average(s => (double?)s.Gpm) ?? 0),
        "kills" => query.OrderByDescending(p => _context.PlayerMatchStats.Where(s => s.PlayerId == p.PlayerId).Average(s => (double?)s.Kills) ?? 0),
        _ => query.OrderBy(p => p.Nickname)
    };

    var total = await query.CountAsync();
    var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
        .Select(p => new {
            p.PlayerId,
            p.Nickname,
            p.Country,
            p.PhotoPath,
            teamName = p.Team != null ? p.Team.TeamName : "No Team",
            avgGpm = _context.PlayerMatchStats.Where(s => s.PlayerId == p.PlayerId).Average(s => (double?)s.Gpm) ?? 0,
            avgKda = _context.PlayerMatchStats.Where(s => s.PlayerId == p.PlayerId)
                .Average(s => (double)(s.Kills + s.Assists) / Math.Max(1, s.Deaths))
        }).ToListAsync();

    return new { total, items };
}

public async Task<object> GetPlayerDashboardAsync()
{
    return new {
        totalPlayers = await _context.Players.CountAsync(),
        gpmKing = await _context.PlayerMatchStats
            .GroupBy(s => s.Player.Nickname)
            .Select(g => new { name = g.Key, value = g.Average(s => s.Gpm) })
            .OrderByDescending(x => x.value).FirstOrDefaultAsync(),
        countryDistribution = await _context.Players
            .Where(p => p.Country != null)
            .GroupBy(p => p.Country)
            .Select(g => new { name = g.Key, value = g.Count() })
            .OrderByDescending(x => x.value).Take(5).ToListAsync()
    };
}

public async Task<object?> GetPlayerDetailsAsync(int id)
{
    var player = await _context.Players.Include(p => p.Team).FirstOrDefaultAsync(p => p.PlayerId == id);
    if (player == null) return null;

    var stats = _context.PlayerMatchStats.Where(s => s.PlayerId == id);

    return new {
        info = new {
            player.Nickname,
            player.Country,
            player.PhotoPath,
            teamName = player.Team?.TeamName,
            teamLogo = player.Team?.LogoPath
        },
        performance = new {
            avgGpm = await stats.AverageAsync(s => (double?)s.Gpm) ?? 0,
            avgXpm = await stats.AverageAsync(s => (double?)s.Xpm) ?? 0,
            avgKills = await stats.AverageAsync(s => (double?)s.Kills) ?? 0,
            totalMatches = await stats.CountAsync()
        },
        signatureHeroes = await stats
            .GroupBy(s => s.Hero.LocalizedName)
            .Select(g => new { name = g.Key, games = g.Count(), winRate = (double)g.Count(x => _context.MatchTeams.Any(mt => mt.MatchId == x.MatchId && mt.TeamId == x.TeamId && mt.IsWinner)) / g.Count() * 100 })
            .OrderByDescending(x => x.games).Take(3).ToListAsync(),
        recentMatches = await stats
            .OrderByDescending(s => s.Match.MatchDate)
            .Take(10)
            .Select(s => new {
                s.MatchId,
                hero = s.Hero.LocalizedName,
                s.Kills, s.Deaths, s.Assists,
                result = _context.MatchTeams.Any(mt => mt.MatchId == s.MatchId && mt.TeamId == s.TeamId && mt.IsWinner) ? "Win" : "Loss"
            }).ToListAsync()
    };
}

public async Task<object> GetAdvancedPlayerAnalyticsAsync()
{
    var regionalIndex = await _context.Players
        .Where(p => p.Country != null)
        .GroupBy(p => p.Country)
        .Select(g => new {
            country = g.Key,
            playerCount = g.Count(),
            avgKda = _context.PlayerMatchStats
                .Where(s => s.Player.Country == g.Key)
                .Average(s => (double)(s.Kills + s.Assists) / Math.Max(1, s.Deaths)),
            winRate = (double)_context.MatchTeams
                .Count(mt => _context.Players.Where(p2 => p2.Country == g.Key)
                .Any(p2 => _context.PlayerMatchStats.Any(s => s.MatchId == mt.MatchId && s.PlayerId == p2.PlayerId && s.TeamId == mt.TeamId)) && mt.IsWinner) /
                Math.Max(1, _context.MatchTeams.Count(mt => _context.Players.Where(p2 => p2.Country == g.Key)
                .Any(p2 => _context.PlayerMatchStats.Any(s => s.MatchId == mt.MatchId && s.PlayerId == p2.PlayerId && s.TeamId == mt.TeamId)))) * 100
        })
        .OrderByDescending(x => x.avgKda)
        .Take(10)
        .ToListAsync();

    var roleBenchmarks = await _context.HeroRoles
        .GroupBy(hr => hr.Role.RoleName)
        .Select(g => new {
            role = g.Key,
            avgGpm = _context.PlayerMatchStats.Where(s => _context.HeroRoles.Any(hr => hr.HeroId == s.HeroId && hr.Role.RoleName == g.Key)).Average(s => (double?)s.Gpm) ?? 0,
            avgXpm = _context.PlayerMatchStats.Where(s => _context.HeroRoles.Any(hr => hr.HeroId == s.HeroId && hr.Role.RoleName == g.Key)).Average(s => (double?)s.Xpm) ?? 0
        }).ToListAsync();

    var scatterData = await _context.PlayerMatchStats
        .GroupBy(s => new { s.PlayerId, s.Player.Nickname })
        .Select(g => new {
            name = g.Key.Nickname,
            gpm = g.Average(s => s.Gpm),
            kda = g.Average(s => (double)(s.Kills + s.Assists) / Math.Max(1, s.Deaths)),
            matches = g.Count()
        })
        .Where(x => x.matches >= 5) 
        .ToListAsync();

    var greediest = await _context.PlayerMatchStats
        .GroupBy(s => s.Player.Nickname)
        .Select(g => new { name = g.Key, value = g.Average(s => s.Gpm) })
        .OrderByDescending(x => x.value).FirstOrDefaultAsync();

    var survivor = await _context.PlayerMatchStats
        .GroupBy(s => s.Player.Nickname)
        .Select(g => new { name = g.Key, value = g.Average(s => s.Deaths), count = g.Count() })
        .Where(x => x.count >= 10)
        .OrderBy(x => x.value).FirstOrDefaultAsync();

    var heroOcean = await _context.PlayerMatchStats
        .GroupBy(s => s.Player.Nickname)
        .Select(g => new { name = g.Key, value = g.Select(s => s.HeroId).Distinct().Count() })
        .OrderByDescending(x => x.value).FirstOrDefaultAsync();

    return new {
        regionalIndex,
        roleBenchmarks,
        scatterData,
        outliers = new { greediest, survivor, heroOcean }
    };
}

public async Task<object> GetAvailablePlayersLookupAsync()
{
    var linkedPlayerIds = await _context.Users
        .Where(u => u.PlayerId != null)
        .Select(u => u.PlayerId.Value)
        .ToListAsync();

    var availablePlayers = await _context.Players
        .Where(p => !linkedPlayerIds.Contains(p.PlayerId))
        .Select(p => new 
        { 
            id = p.PlayerId, 
            nickname = p.Nickname,
            photo = p.PhotoPath,
            country = p.Country 
        })
        .OrderBy(p => p.nickname)
        .ToListAsync();

    return availablePlayers;
}
}