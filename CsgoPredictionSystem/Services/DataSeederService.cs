using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Models;
using CsgoPredictionSystem.Hubs;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using CsgoPredictionSystem.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore.Storage;

namespace CsgoPredictionSystem.Services;

public class DataSeederService
{
    private readonly DotaDbContext _context;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DataSeederService> _logger;

    public DataSeederService(DotaDbContext context, IHubContext<SyncHub> hubContext, ILogger<DataSeederService> logger)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DotaAnalyticsApp");
    }
    
    private async Task Notify(string message, int progressPercent, string status = "Processing")
    {
        Console.WriteLine($"[SEEDER] {progressPercent}%: {message}");
        
        await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new
        {
            syncType = "InitialSeed",
            lastRunAt = DateTime.UtcNow,
            status = $"{progressPercent}% - {message}",
            isAutoSyncEnabled = false
        });
    }
    
    private long ParseId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        try
        {
            double temp = double.Parse(value.Trim(), CultureInfo.InvariantCulture);
            return (long)Math.Truncate(temp);
        }
        catch
        {
            return 0;
        }
    }

    public async Task SeedAll(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        
        using var transaction = await _context.Database.BeginTransactionAsync(ct);

    try
    {
        _context.ChangeTracker.AutoDetectChangesEnabled = false;

        await Notify("Preparing to fill the base...", 0);
        await Task.Delay(500); 
        ct.ThrowIfCancellationRequested();

        await SeedRoles(ct);
        await Notify("User roles added", 10);
        await Task.Delay(500);
        ct.ThrowIfCancellationRequested();

        await SeedSyncStatus(ct);
        await Notify("Synchronization statuses are initialized", 15);
        await Task.Delay(500);
        ct.ThrowIfCancellationRequested();

        await SeedHeroes(ct); 
        await Notify("Heroes and hero roles are loaded", 30);
        await Task.Delay(500);
        ct.ThrowIfCancellationRequested();

        await SeedTournaments(ct);
        await Notify("Tournaments loaded successfully", 45);
        await Task.Delay(500);
        ct.ThrowIfCancellationRequested();

        await SeedTeams(ct);
        await Notify("Teams and rankings added", 60);
        await Task.Delay(500);
        ct.ThrowIfCancellationRequested();

        await SeedPlayers(ct);
        await Notify("Players and profiles imported", 75);
        await Task.Delay(500);
        ct.ThrowIfCancellationRequested();

        await SeedMatchesChainFromCsv(ct); 
        await Notify("Matches and player statistics are loaded", 90);
        await Task.Delay(500);
        ct.ThrowIfCancellationRequested();

        await SeedUsers(ct); 
        await Notify("An administrator account has been created", 95);
        await Task.Delay(500);

        await _context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        sw.Stop();
        await Notify($"THE BASE IS COMPLETELY FILLED! Lead time: {sw.Elapsed.TotalSeconds:F1} sec.", 100, "Success");
    }
    catch (OperationCanceledException)
    {
        await transaction.RollbackAsync();
        await Notify("Seeding was cancelled", 0, "Cancelled");
        _logger.LogWarning("Data seeding was cancelled by the user/system.");
    }
    catch (Exception ex)
    {
        if (transaction.GetDbTransaction().Connection != null)
            await transaction.RollbackAsync();
                
        string friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
        await Notify(friendlyError, 0, "Error");
        throw; 
    }
    finally
    {
        _context.ChangeTracker.AutoDetectChangesEnabled = true;
    }
}

    private async Task SeedRoles(CancellationToken ct)
    {
        if (await _context.Roles.AnyAsync(ct)) return;
        _context.Roles.AddRange(
            new Role { RoleName = "Admin" },
            new Role { RoleName = "Player" },
            new Role { RoleName = "Spectator" }
        );
        await _context.SaveChangesAsync(ct);
    }

    private async Task SeedSyncStatus(CancellationToken ct)
    {
        var existingTypes = await _context.ApiSyncStatus
            .Select(s => s.SyncType)
            .ToListAsync(ct);

        var newStatuses = new List<ApiSyncStatus>();

        foreach (var type in SyncTypes.All)
        {
            if (!existingTypes.Contains(type))
            {
                newStatuses.Add(new ApiSyncStatus
                {
                    SyncType = type,
                    LastRunAt = DateTime.UtcNow.AddDays(-1),
                    Status = "Idle",
                    IsAutoSyncEnabled = false
                });
            }
        }

        if (newStatuses.Any())
        {
            _context.ApiSyncStatus.AddRange(newStatuses);
            await _context.SaveChangesAsync(ct);
        }
    }

    private async Task SeedHeroes(CancellationToken ct)
        {
            if (await _context.Heroes.AnyAsync(ct)) return;
        
        string csvFolder = Path.Combine(Directory.GetCurrentDirectory(), "Data", "CSV");
    
        var roleDefsPath = Path.Combine(csvFolder, "hero_role_definitions.csv");
        if (File.Exists(roleDefsPath))
        {
            var existingRoles = await _context.HeroRoleDefinitions.Select(r => r.RoleName).ToHashSetAsync(ct);
            var lines = await File.ReadAllLinesAsync(roleDefsPath, ct);
            var newRoles = new List<HeroRoleDefinition>();
    
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                var roleName = parts[1].Trim();
                
                if (!existingRoles.Contains(roleName))
                {
                    newRoles.Add(new HeroRoleDefinition { RoleName = roleName });
                    existingRoles.Add(roleName); 
                }
            }
            if (newRoles.Any())
            {
                _context.HeroRoleDefinitions.AddRange(newRoles);
                await _context.SaveChangesAsync(ct);
            }
        }
    
        var roleDict = await _context.HeroRoleDefinitions.ToDictionaryAsync(r => r.RoleName, r => r.RoleId, ct);
    
        var heroesPath = Path.Combine(csvFolder, "heroes.csv");
        if (File.Exists(heroesPath))
        {
            var lines = await File.ReadAllLinesAsync(heroesPath, ct);
            var heroesList = new List<Hero>();
    
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 4) continue;
                
                heroesList.Add(new Hero {
                    HeroId = (int)ParseId(parts[0]),
                    HeroName = parts[1].Trim(),
                    LocalizedName = parts[2].Trim(),
                    PrimaryAttr = parts[3].Trim()
                });
            }
            _context.Heroes.AddRange(heroesList);
            await _context.SaveChangesAsync(ct);
        }
    
        var rolesMapPath = Path.Combine(csvFolder, "hero_roles_map.csv");
        if (File.Exists(rolesMapPath) && File.Exists(roleDefsPath))
        {
            var defLines = await File.ReadAllLinesAsync(roleDefsPath, ct);
            var csvIdToName = defLines.Skip(1)
                .Select(l => l.Split(','))
                .Where(p => p.Length >= 2)
                .ToDictionary(p => (int)ParseId(p[0]), p => p[1].Trim());
    
            var mapLines = await File.ReadAllLinesAsync(rolesMapPath, ct);
            var heroRolesList = new List<HeroRole>();
    
            foreach (var line in mapLines.Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
    
                int hId = (int)ParseId(parts[0]);
                int csvRoleId = (int)ParseId(parts[1]);
    
                if (csvIdToName.TryGetValue(csvRoleId, out string rName) && roleDict.TryGetValue(rName, out int dbRoleId))
                {
                    if (!heroRolesList.Any(hr => hr.HeroId == hId && hr.RoleId == dbRoleId))
                    {
                        heroRolesList.Add(new HeroRole { HeroId = hId, RoleId = dbRoleId });
                    }
                }
            }
            _context.HeroRoles.AddRange(heroRolesList);
            await _context.SaveChangesAsync(ct);
        }
    }

    private async Task SeedTournaments(CancellationToken ct)
    {
        if (await _context.Tournaments.AnyAsync(ct)) return;
        
        string csvPath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "CSV", "tournaments.csv");
        if (!File.Exists(csvPath)) return;
    
        var lines = await File.ReadAllLinesAsync(csvPath, ct);
        var addedIds = new HashSet<long>();
        var tournamentsToAdd = new List<Tournament>(); 
    
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue; 
    
            var parts = line.Split(',');
            
            if (parts.Length < 3) continue; 
    
            long extId = ParseId(parts[0]);
            
            if (extId == 0 || addedIds.Contains(extId)) continue;
    
            tournamentsToAdd.Add(new Tournament 
            { 
                ExternalId = extId, 
                Name = parts[1].Trim(), 
                Tier = parts[2].Trim() 
            });
    
            addedIds.Add(extId);
        }
    
        if (tournamentsToAdd.Any())
        {
            _context.Tournaments.AddRange(tournamentsToAdd); 
            await _context.SaveChangesAsync(ct);
        }
    }

    private async Task SeedTeams(CancellationToken ct)
    {
        if (await _context.Teams.AnyAsync(ct)) return;
        
        string csvPath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "CSV", "teams.csv");
        if (!File.Exists(csvPath)) return;
    
        var lines = await File.ReadAllLinesAsync(csvPath, ct);
        var addedIds = new HashSet<long>();
        var teamsToAdd = new List<Team>(); 
    
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
    
            var parts = line.Split(',');
            
            if (parts.Length < 7) continue;
    
            long extId = ParseId(parts[0]);
            if (extId == 0 || addedIds.Contains(extId)) continue;
    
            var team = new Team {
                ExternalId = extId,
                TeamName = parts[1].Trim().Length > 100 ? parts[1].Trim().Substring(0, 100) : parts[1].Trim(),
                Acronym = parts[2].Trim().Length > 20 ? parts[2].Trim().Substring(0, 20) : parts[2].Trim(),
                CurrentRank = (int)ParseId(parts[3]),
                LogoPath = parts[4].Trim(),
                Wins = (int)ParseId(parts[5]),
                Losses = (int)ParseId(parts[6])
            };
    
            if (parts.Length > 7 && long.TryParse(parts[7], out long lastMatch) && lastMatch > 0)
            {
                team.LastMatchTime = lastMatch;
            }
            
            teamsToAdd.Add(team);
            addedIds.Add(extId);
        }
    
        if (teamsToAdd.Any())
        {
            _context.Teams.AddRange(teamsToAdd);
            await _context.SaveChangesAsync(ct);
        }
    }

    private async Task SeedPlayers(CancellationToken ct)
    {
        if (await _context.Players.AnyAsync(ct)) return;
        
        string csvPath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "CSV", "players.csv");
        if (!File.Exists(csvPath)) return;
    
        var teamMap = await _context.Teams
            .Where(t => t.ExternalId != null)
            .ToDictionaryAsync(t => (long)t.ExternalId, t => t.TeamId, ct);
    
        var lines = await File.ReadAllLinesAsync(csvPath, ct);
        var addedIds = new HashSet<long>();
        var playersToAdd = new List<Player>();
    
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            
            if (parts.Length < 4) continue;
    
            long steamId = ParseId(parts[0]);
            if (steamId == 0 || addedIds.Contains(steamId)) continue;
    
            int? internalTeamId = null;
            if (parts.Length > 4) {
                long extTid = ParseId(parts[4]);
                if (teamMap.TryGetValue(extTid, out int tId)) 
                    internalTeamId = tId;
            }
    
            var player = new Player {
                SteamId = steamId,
                Nickname = parts[1].Trim().Length > 100 ? parts[1].Trim().Substring(0, 99) : parts[1].Trim(),
                PhotoPath = parts[2].Trim(),
                Country = parts[3].Trim().Length > 50 ? parts[3].Trim().Substring(0, 49) : parts[3].Trim(),
                TeamId = internalTeamId
            };
    
            if (parts.Length > 5 && long.TryParse(parts[5], out long lastMatch) && lastMatch > 0)
            {
                player.LastMatchTime = DateTimeOffset.FromUnixTimeSeconds(lastMatch).UtcDateTime;
            }
    
            playersToAdd.Add(player);
            addedIds.Add(steamId);
        }
    
        if (playersToAdd.Any())
        {
            _context.Players.AddRange(playersToAdd);
            await _context.SaveChangesAsync(ct);
        }
    }
    private async Task SeedMatchesChainFromCsv(CancellationToken ct)
    {
        if (await _context.PlayerMatchStats.AnyAsync(ct)) return;
    
        string csvFolder = Path.Combine(Directory.GetCurrentDirectory(), "Data", "CSV");
    
        var tournamentMap = await _context.Tournaments.Where(t => t.ExternalId != null).ToDictionaryAsync(t => (long)t.ExternalId, t => t.TournamentId, ct);
        var teamMap = await _context.Teams.Where(t => t.ExternalId != null).ToDictionaryAsync(t => (long)t.ExternalId, t => t.TeamId, ct);;
        var playerMap = await _context.Players.ToDictionaryAsync(p => p.SteamId, p => p.PlayerId, ct);
    
        var matchesPath = Path.Combine(csvFolder, "matches.csv");
        if (File.Exists(matchesPath)) {
            var matchLines = await File.ReadAllLinesAsync(matchesPath, ct); 
            var matchesToAdd = new List<Match>();
            foreach (var line in matchLines.Skip(1)) {
                var parts = line.Split(',');
                long extMatchId = ParseId(parts[0]);
                
                int? tId = tournamentMap.TryGetValue(ParseId(parts[4]), out int foundId) ? foundId : null;
    
                matchesToAdd.Add(new Match {
                    ExternalId = extMatchId,
                    MatchDate = DateTime.Parse(parts[1]).ToUniversalTime(),
                    Duration = (int)ParseId(parts[2]),
                    TournamentId = tId
                });
            }
            _context.Matches.AddRange(matchesToAdd);
            await _context.SaveChangesAsync(ct);
        }
    
        var matchMap = await _context.Matches.ToDictionaryAsync(m => m.ExternalId, m => m.MatchId, ct);
    
        var matchTeamsPath = Path.Combine(csvFolder, "match_teams.csv");
        if (File.Exists(matchTeamsPath)) {
            var mtLines = await File.ReadAllLinesAsync(matchTeamsPath, ct); 
            var mtToAdd = new List<MatchTeam>();
            foreach (var line in mtLines.Skip(1)) {
                var parts = line.Split(',');
                if (matchMap.TryGetValue(ParseId(parts[0]), out int mId) && teamMap.TryGetValue(ParseId(parts[1]), out int tId)) {
                    mtToAdd.Add(new MatchTeam { MatchId = mId, TeamId = tId, Score = (int)ParseId(parts[2]), IsWinner = bool.Parse(parts[3]), Side = parts[4].Trim() });
                }
            }
            _context.MatchTeams.AddRange(mtToAdd);
            await _context.SaveChangesAsync(ct); 
        }
    
        var statsPath = Path.Combine(csvFolder, "player_stats.csv");
        if (File.Exists(statsPath)) {
            using var reader = new StreamReader(statsPath);
            await reader.ReadLineAsync(); 
    
            var statsBatch = new List<PlayerMatchStats>();
            int totalProcessed = 0;

            while (await reader.ReadLineAsync() is { } line) {
                var parts = line.Split(',');
                if (parts.Length < 15) continue;
    
                if (matchMap.TryGetValue(ParseId(parts[0]), out int mId) && playerMap.TryGetValue(ParseId(parts[1]), out int pId)) {
                    statsBatch.Add(new PlayerMatchStats {
                        MatchId = mId, PlayerId = pId, HeroId = (int)ParseId(parts[2]),
                        TeamId = teamMap.TryGetValue(ParseId(parts[3]), out int teamId) ? teamId : null,
                        Kills = (int)ParseId(parts[4]), Deaths = (int)ParseId(parts[5]), Assists = (int)ParseId(parts[6]),
                        Gpm = (int)ParseId(parts[7]), Xpm = (int)ParseId(parts[8]), NetWorth = (int)ParseId(parts[9]),
                        LastHits = (int)ParseId(parts[10]), Denies = (int)ParseId(parts[11]),
                        HeroDamage = (int)ParseId(parts[12]), TowerDamage = (int)ParseId(parts[13]), PlayerSlot = (int)ParseId(parts[14])
                    });
                }
    
                if (statsBatch.Count >= 500) {
                    _context.PlayerMatchStats.AddRange(statsBatch);
                    await _context.SaveChangesAsync(ct);
                
                    _context.ChangeTracker.Clear(); 
                
                    statsBatch.Clear();
                    totalProcessed += 500;
                    _logger.LogInformation("Saved {Count} stats rows...", totalProcessed);
                }
            }
            if (statsBatch.Any()) {
                _context.PlayerMatchStats.AddRange(statsBatch);
                await _context.SaveChangesAsync(ct);
                _context.ChangeTracker.Clear();
            }
        }
    }

    private async Task SeedUsers(CancellationToken ct)
{
    if (await _context.Users.AnyAsync(ct)) return;

    var roles = await _context.Roles.ToListAsync(ct);
    var adminRole = roles.FirstOrDefault(r => r.RoleName == "Admin");
    var spectatorRole = roles.FirstOrDefault(r => r.RoleName == "Spectator");
    var playerRole = roles.FirstOrDefault(r => r.RoleName == "Player");

    if (adminRole == null || spectatorRole == null)
    {
        Console.WriteLine("[SEEDER] Error: Roles 'Admin' or 'Spectator' not found. Run SeedRoles first.");
        return;
    }
    
    ct.ThrowIfCancellationRequested();


    var users = new List<User>();

    users.Add(new User 
    { 
        Username = "admin", 
        Email = "admin@app.com", 
        PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"), 
        RoleId = adminRole.RoleId 
    });

    users.Add(new User 
    { 
        Username = "lucky_user", 
        Email = "user@test.com", 
        PasswordHash = BCrypt.Net.BCrypt.HashPassword("user123"), 
        RoleId = spectatorRole.RoleId 
    });

    var firstPlayer = await _context.Players.FirstOrDefaultAsync(ct);
    if (firstPlayer != null && playerRole != null)
    {
        users.Add(new User 
        { 
            Username = "pro_player", 
            Email = "player@test.com", 
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("player123"), 
            RoleId = playerRole.RoleId,
            PlayerId = firstPlayer.PlayerId 
        });
    }
    ct.ThrowIfCancellationRequested();


    _context.Users.AddRange(users);
    await _context.SaveChangesAsync(ct);
    
    Console.WriteLine($"[SEEDER] Successfully created {users.Count} initial users.");
}
}