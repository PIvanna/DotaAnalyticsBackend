using Microsoft.AspNetCore.Mvc;
using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Services;
using Microsoft.EntityFrameworkCore;
using CsgoPredictionSystem.Helpers;
using CsgoPredictionSystem.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/admin/system")]
public class SystemController : ControllerBase
{
    private readonly DotaDbContext _context;
    private readonly DataSeederService _seeder;
    private readonly ILogger<SystemController> _logger;
    private readonly SystemStateService _stateService;
    private readonly IHubContext<SyncHub> _hubContext;

    public SystemController(DotaDbContext context, DataSeederService seeder, ILogger<SystemController> logger, SystemStateService stateService, IHubContext<SyncHub> hubContext)
    {
        _context = context; 
        _seeder = seeder;
        _logger = logger;
        _stateService = stateService;
        _hubContext = hubContext;
    }

    [HttpPost("seed-initial")]
    public async Task<IActionResult> SeedInitial()
    {
        try 
        {
            await _seeder.SeedAll(_stateService.GlobalCancellationToken); 
            return Ok(new { message = "Initial filling completed" });
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);

            _logger.LogError(ex, "Error during SeedInitial");

            return BadRequest(new { message = friendlyError });
        }
    }
    
    [HttpDelete("clear-db")]
    public async Task<IActionResult> ClearDb()
    {
        _stateService.StartMaintenance();

        await _hubContext.Clients.All.SendAsync("SystemStatusUpdate", new { 
            isMaintenance = true, 
            message = "Cleaning database, please wait..." 
        });
        
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            _logger.LogWarning("Database cleanup started...");

            await _context.PlayerMatchStats.ExecuteDeleteAsync();
            await _context.MatchTeams.ExecuteDeleteAsync();
            await _context.HeroRoles.ExecuteDeleteAsync();
            await _context.Matches.ExecuteDeleteAsync();
            await _context.Users.ExecuteDeleteAsync(); 
            await _context.Players.ExecuteDeleteAsync();
            await _context.Tournaments.ExecuteDeleteAsync();
            await _context.Heroes.ExecuteDeleteAsync();
            await _context.Teams.ExecuteDeleteAsync();
            await _context.HeroRoleDefinitions.ExecuteDeleteAsync();
            await _context.Roles.ExecuteDeleteAsync(); 
            await _context.MlTrainingHistory.ExecuteDeleteAsync();
            await _context.ApiSyncStatus.ExecuteDeleteAsync();


            await transaction.CommitAsync();
            
            _stateService.StopMaintenance();

            await _hubContext.Clients.All.SendAsync("SystemStatusUpdate", new { 
                isMaintenance = false, 
                message = "Database cleared. System is fresh." 
            });
            
            return Ok("The database has been completely cleaned.");
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }

}