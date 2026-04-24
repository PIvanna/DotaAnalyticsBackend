using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using CsgoPredictionSystem.Services;
using CsgoPredictionSystem.DTO;
using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Helpers;
using CsgoPredictionSystem.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/admin/ml")]
[Authorize(Roles = "Admin")]
public class MlController : ControllerBase
{
    private readonly MlPredictionService  _mlService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MlController> _logger;
    private readonly SystemStateService   _stateService;
    private readonly DotaDbContext        _context;

    public MlController(
        MlPredictionService  mlService,
        IServiceScopeFactory scopeFactory,
        ILogger<MlController> logger,
        SystemStateService   stateService,
        DotaDbContext        context)
    {
        _mlService    = mlService;
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _stateService = stateService;
        _context      = context;
    }

    [HttpPost("train")]
    public IActionResult TrainModel([FromBody] TrainRequest request)
    {
        var ct = _stateService.GlobalCancellationToken;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<MlPredictionService>();
                await svc.TrainModelInBackground(request, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start training background task");
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var hub = scope.ServiceProvider.GetRequiredService<IHubContext<SyncHub>>();
                    await hub.Clients.All.SendAsync("ReceiveSyncUpdate", new
                    {
                        syncType = "MLTrain",
                        status   = $"❌ Failed to start: {ex.Message}",
                        progress = 0
                    });
                }
                catch {}
            }
        });

        return Accepted(new { message = "Training is running in the background. Follow progress via SignalR." });
    }


    [HttpPost("predict")]
    public async Task<IActionResult> PredictWinner(
        [FromQuery] long t1Id,
        [FromQuery] long t2Id)
    {
        var teams = await _context.Teams
            .Where(t => t.ExternalId == t1Id || t.ExternalId == t2Id)
            .Select(t => new
            {
                t.ExternalId,
                t.TeamName,
                t.LogoPath
            })
            .ToListAsync();

        var team1 = teams.FirstOrDefault(t => t.ExternalId == t1Id);
        var team2 = teams.FirstOrDefault(t => t.ExternalId == t2Id);

        if (team1 == null)
            return BadRequest(new { message = $"Team with ExternalId={t1Id} not found." });
        if (team2 == null)
            return BadRequest(new { message = $"Team with ExternalId={t2Id} not found." });

        string t1Name  = team1.TeamName;
        string t2Name  = team2.TeamName;
        string? t1Logo = team1.LogoPath;
        string? t2Logo = team2.LogoPath;

        var ct = _stateService.GlobalCancellationToken;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<MlPredictionService>();

                await svc.PredictWinnerInBackground(
                    t1Id, t2Id,
                    t1Name, t2Name,
                    t1Logo, t2Logo,
                    ct
                );
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start prediction background task");
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var hub = scope.ServiceProvider.GetRequiredService<IHubContext<SyncHub>>();
                    await hub.Clients.All.SendAsync("ReceiveSyncUpdate", new
                    {
                        syncType = "MLPredict",
                        status   = $"❌ Launch error: {ex.Message}",
                        progress = 0
                    });
                }
                catch { }
            }
        });

        return Accepted(new
        {
            message = "ML handles request...",
            team1   = new { name = t1Name, externalId = t1Id },
            team2   = new { name = t2Name, externalId = t2Id }
        });
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetTrainingHistory(
        [FromQuery] int  page      = 1,
        [FromQuery] int  pageSize  = 10,
        [FromQuery] bool onlyMine  = false)
    {
        try
        {
            int? filterUserId = null;

            if (onlyMine)
            {
                var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!string.IsNullOrEmpty(claim) && int.TryParse(claim, out int uid))
                    filterUserId = uid;
            }

            var result = await _mlService.GetTrainingHistoryPagedAsync(page, pageSize, filterUserId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching ML history");
            return BadRequest(new { message = DatabaseErrorHelper.MapDatabaseError(ex) });
        }
    }

    [HttpPost("set-active/{sessionId:int}")]
    public async Task<IActionResult> SetActiveModel(int sessionId)
    {
        try
        {
            var success = await _mlService.SetActiveModel(sessionId);
            if (success)
                return Ok(new { message = $"Model #{sessionId} now active for all predictions." });

            return NotFound(new { message = $"Model #{sessionId} not found in the database." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set active model #{SessionId}", sessionId);
            return BadRequest(new { message = DatabaseErrorHelper.MapDatabaseError(ex) });
        }
    }
}