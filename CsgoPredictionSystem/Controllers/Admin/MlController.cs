using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using CsgoPredictionSystem.Services;
using CsgoPredictionSystem.DTO;
using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Helpers;
using CsgoPredictionSystem.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/admin/ml")]
[Authorize(Roles = "Admin")]
public class MlController : ControllerBase
{
    private readonly MlPredictionService _mlService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MlController> _logger;
    private readonly SystemStateService _stateService; 

    public MlController(
        MlPredictionService mlService, 
        IServiceScopeFactory scopeFactory, 
        ILogger<MlController> logger,
        SystemStateService stateService) 
    {
        _mlService = mlService;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _stateService = stateService;
    }

    [HttpPost("train")]
    public IActionResult TrainModel([FromBody] TrainRequest request)
    {

        var ct = _stateService.GlobalCancellationToken;

        _ = Task.Run(async () => 
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var scopedMlService = scope.ServiceProvider.GetRequiredService<MlPredictionService>();
                try 
                {
                    await scopedMlService.TrainModelInBackground(request, ct);
                }
                catch (Exception ex)
                {
                    var hub = scope.ServiceProvider.GetRequiredService<IHubContext<SyncHub>>();
                    await hub.Clients.All.SendAsync("ReceiveSyncUpdate", new {
                        syncType = "MLTrain",
                        status = "❌ Failed to start process: " + ex.Message,
                        progress = 0
                    });
                }
            }
        });

        return Accepted(new { message = "Training is running in the background. Follow progress." });
    }

    [HttpPost("predict")]
    public IActionResult PredictWinner([FromQuery] long t1Id, [FromQuery] long t2Id)
    {
        var ct = _stateService.GlobalCancellationToken;

        _ = Task.Run(async () => 
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var scopedMlService = scope.ServiceProvider.GetRequiredService<MlPredictionService>();
                var hub = scope.ServiceProvider.GetRequiredService<IHubContext<SyncHub>>();

                try 
                {
                    await scopedMlService.PredictWinnerInBackground(t1Id, t2Id, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) {
                    await hub.Clients.All.SendAsync("ReceiveSyncUpdate", new {
                        syncType = "MLPredict",
                        status = "❌ Launch error " + ex.Message,
                        progress = 0
                    });
                }
            }
        });

        return Accepted(new { message = "Forecast calculated..." });
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetTrainingHistory(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 10,
        [FromQuery] bool onlyMine = false) 
    {
        try 
        {
            int? filterUserId = null;
        
            if (onlyMine)
            {
                var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!string.IsNullOrEmpty(userIdClaim))
                {
                    filterUserId = int.Parse(userIdClaim);
                }
            }

            var result = await _mlService.GetTrainingHistoryPagedAsync(page, pageSize, filterUserId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching ML history");
        
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }
    
    [HttpPost("set-active/{sessionId}")]
    public async Task<IActionResult> SetActiveModel(int sessionId)
    {
        try 
        {
            var success = await _mlService.SetActiveModel(sessionId);
        
            if (success) 
            {
                return Ok(new { message = $"The #{sessionId} model is now active for all predictions." });
            }
        
            return NotFound(new { message = $"Model with ID {sessionId} not found in the database." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set active model #{SessionId}", sessionId);
        
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }
}