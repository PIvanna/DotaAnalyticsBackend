using Microsoft.AspNetCore.Mvc;
using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Helpers;
using CsgoPredictionSystem.Services;
using CsgoPredictionSystem.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using CsgoPredictionSystem.Models;
using Microsoft.AspNetCore.Authorization;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/admin/sync")]
[Authorize(Roles = "Admin")]
public class SyncController : ControllerBase
{
    private readonly DotaDbContext _context;
    private readonly TournamentService _tournamentService;
    private readonly TeamService _teamService;
    private readonly PlayerService _playerService;
    private readonly MatchService _matchService;
    private readonly HeroService _heroService;
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly ILogger<SyncController> _logger;
    private readonly IServiceScopeFactory _scopeFactory; 
    private readonly SystemStateService _stateService; 


    public SyncController(
        IServiceScopeFactory scopeFactory,  
        DotaDbContext context,
        TournamentService tournamentService,
        TeamService teamService,
        PlayerService playerService,
        MatchService matchService,
        HeroService heroService,
        IHubContext<SyncHub> hubContext,
        ILogger<SyncController> logger,
        SystemStateService stateService) 
    {
        _scopeFactory = scopeFactory;
        _context = context;
        _tournamentService = tournamentService;
        _teamService = teamService;
        _playerService = playerService;
        _matchService = matchService;
        _heroService = heroService;
        _hubContext = hubContext;
        _logger = logger;
        _stateService = stateService;
    }

    private async Task UpdateSyncStatus(string type, string status)
    {
        var entry = await _context.ApiSyncStatus.FirstOrDefaultAsync(x => x.SyncType == type);
    
        if (entry != null)
        {
            entry.LastRunAt = DateTime.UtcNow;
            entry.Status = status;
        
            try 
            {
                await _context.SaveChangesAsync();
            
                _logger.LogInformation("Synchronization status {Type} has been updated to {Status}", type, status);

                await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new
                {
                    syncType = entry.SyncType,
                    lastRunAt = entry.LastRunAt,
                    status = entry.Status,
                    isAutoSyncEnabled = entry.IsAutoSyncEnabled
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save synchronization status in database");

                string friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);

                await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new
                {
                    syncType = type,
                    lastRunAt = DateTime.UtcNow,
                    status = friendlyError, 
                    isAutoSyncEnabled = entry.IsAutoSyncEnabled
                });
            }
        }
        else
        {
            _logger.LogWarning("Attempting to update status for non-existent type: {Type}", type);
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetSyncStatus()
    {
        try 
        {
            var status = await _context.Set<ApiSyncStatus>().ToListAsync();
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while getting sync statuses");
        
            string friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
        
            return BadRequest(new { message = friendlyError });
        }
    }
    
    
    [HttpPost("tournament")]
    public async Task<IActionResult> SyncTournament([FromQuery] int count = 100, [FromQuery] string[] tiers = null)
    {
        if (count <= 0 || count > 500)
            return BadRequest(new { message = "The number of tournaments should be within 1-500." });

        try 
        {
            var result = await _tournamentService.SyncTournamentsFromApi(count, tiers);
        
            await UpdateSyncStatus("Tournaments", "Success");
    
            return Ok(new { message = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tournament synchronization error");
            await UpdateSyncStatus("Tournaments", "Error");

            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }

    [HttpPost("teams")]
    public async Task<IActionResult> SyncTeams(
        [FromQuery] int count = 50, 
        [FromQuery] int minRating = 1000, 
        [FromQuery] int activeDays = 180)
    {
        if (count <= 0 || count > 200)
            return BadRequest(new { message = "The number of teams must be between 1 and 200." });

        if (minRating < 0 || minRating > 10000)
            return BadRequest(new { message = "The rating should be within 0-10000." });

        if (activeDays is < 1 or > 3650)
        {
            return BadRequest(new { 
                message = "The activity period must be between 1 and 3650 days (10 years)." 
            });
        }
        
        try 
        {
            var result = await _teamService.SyncTeamsFromApi(count, minRating, activeDays);
            await UpdateSyncStatus("Teams", "Success");
            return Ok(new { message = result });
        } 
        catch (Exception ex) 
        {
            _logger.LogError(ex, "Command synchronization error in Controller");
            await UpdateSyncStatus("Teams", "Error");

            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }

    [HttpPost("heroes")]
    public async Task<IActionResult> SyncHeroes()
    {
        try 
        {
            var result = await _heroService.SyncHeroesFromApi();
            await UpdateSyncStatus("Heroes", "Success");
            return Ok(new { message = result });
        } 
        catch (Exception ex) 
        {
            _logger.LogError(ex, "Error synchronizing heroes in Controller");
            await UpdateSyncStatus("Heroes", "Error");

            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }

    [HttpPost("players")]
    public async Task<IActionResult> SyncPlayers(
        [FromQuery] int count = 100, 
        [FromQuery] int daysInactive = 180,
        [FromQuery] bool mustHaveTeam = true) 
    {
        if (count <= 0 || count > 500) 
            return BadRequest(new { message = "The number of players must be between 1 and 500." });

        if (daysInactive < 0 || daysInactive > 3650)
            return BadRequest(new { message = "Incorrect period of inactivity." });

        try 
        {
            var result = await _playerService.SyncPlayersFromApi(count, daysInactive, mustHaveTeam);
            await UpdateSyncStatus("Players", "Success");
            return Ok(new { message = result });
        } 
        catch (Exception ex) 
        {
            _logger.LogError(ex, "Error synchronizing players");
            await UpdateSyncStatus("Players", "Error");
        
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }

    [HttpPost("matches")]
    public async Task<IActionResult> SyncMatches(
        [FromQuery] int count = 10,
        [FromQuery] int daysAgo = 30,
        [FromQuery] int minDurationMinutes = 20) 
    {
        if (count <= 0 || count > 100) 
            return BadRequest(new { message = "The number of matches must be between 1 and 100 per request." });

        if (daysAgo <= 0 || daysAgo > 365)
            return BadRequest(new { message = "The search period should be from 1 to 365 days." });

        if (minDurationMinutes < 10 || minDurationMinutes > 120)
            return BadRequest(new { message = "The duration of the match should be within 10-120 minutes." });

        var ct = _stateService.GlobalCancellationToken;

        
        _ = Task.Run(async () => 
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var scopedMatchService = scope.ServiceProvider.GetRequiredService<MatchService>();
            var scopedContext = scope.ServiceProvider.GetRequiredService<DotaDbContext>();

            try 
            {
                _logger.LogInformation("Background Match Sync STARTED");
                
                await scopedMatchService.SyncMatchesTask(count, daysAgo, minDurationMinutes, ct);
                
                var entry = await scopedContext.ApiSyncStatus.FirstOrDefaultAsync(x => x.SyncType == "Matches");
                if (entry != null)
                {
                    entry.LastRunAt = DateTime.UtcNow;
                    entry.Status = "Success";
                    await scopedContext.SaveChangesAsync(ct);

                    await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new {
                        syncType = entry.SyncType,
                        lastRunAt = entry.LastRunAt,
                        status = entry.Status,
                        isAutoSyncEnabled = entry.IsAutoSyncEnabled
                    });
                }
                
                _logger.LogInformation("Background Match Sync FINISHED");
            } 
            catch (Exception ex) 
            {
                _logger.LogCritical(ex, "FATAL ERROR in background Match Sync");
    
                var entry = await scopedContext.ApiSyncStatus.FirstOrDefaultAsync(x => x.SyncType == "Matches");
                if (entry != null) {
                    string friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
                    entry.Status = friendlyError; 
                    await scopedContext.SaveChangesAsync();

                    await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new {
                        syncType = "Matches",
                        status = friendlyError,
                        lastRunAt = DateTime.UtcNow
                    });
                }
            }
        }
    });

    return Accepted(new { message = "Match synchronization is running in the background." });
    }

    [HttpPost("toggle/{type}")]
    public async Task<IActionResult> ToggleAutoSync(string type, [FromQuery] bool enabled)
    {
        try
        {
            var setting = await _context.ApiSyncStatus.FirstOrDefaultAsync(x => x.SyncType == type);
            if (setting == null) 
                return NotFound(new { message = $"Sync type '{type}' not found." });

            setting.IsAutoSyncEnabled = enabled;

            if (enabled) 
            {
                setting.Status = "Pending (Auto)"; 
            }
            else 
            {
                setting.Status = "Disabled";
            }

            await _context.SaveChangesAsync();

            await _hubContext.Clients.All.SendAsync("ReceiveSyncUpdate", new {
                syncType = setting.SyncType,
                lastRunAt = setting.LastRunAt,
                status = setting.Status,
                isAutoSyncEnabled = setting.IsAutoSyncEnabled
            });

            _logger.LogInformation("Auto-sync for {Type} was set to {State}", type, enabled);

            return Ok(new { 
                message = $"Auto-sync for {type} has been {(enabled ? "enabled" : "disabled")}.",
                status = setting.Status
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling auto-sync for {Type}", type);
        
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
        
            return BadRequest(new { message = friendlyError });
        }
    }
}