using Microsoft.AspNetCore.Mvc;
using CsgoPredictionSystem.Services;
using CsgoPredictionSystem.DTO;
using CsgoPredictionSystem.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PredictionController : ControllerBase
{
    private readonly MlPredictionService _mlService;
    private readonly DotaDbContext _context;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PredictionController> _logger;

    public PredictionController(
        MlPredictionService mlService,
        DotaDbContext context,
        IServiceScopeFactory scopeFactory,
        ILogger<PredictionController> logger)
    {
        _mlService    = mlService;
        _context      = context;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    [HttpPost("predict")]
    public async Task<IActionResult> PredictWinner([FromBody] PredictionRequestDto request)
    {
        var team1 = await _context.Teams
            .FirstOrDefaultAsync(t => t.ExternalId == request.Team1ExternalId);

        var team2 = await _context.Teams
            .FirstOrDefaultAsync(t => t.ExternalId == request.Team2ExternalId);

        if (team1 == null)
            return BadRequest(new { message = $"Team with ExternalId={request.Team1ExternalId} not found." });

        if (team2 == null)
            return BadRequest(new { message = $"Team with ExternalId={request.Team2ExternalId} not found." });

        if (!team1.ExternalId.HasValue || !team2.ExternalId.HasValue)
            return BadRequest(new { message = "Teams lack external IDs for AI." });
        
        long t1ExtId    = team1.ExternalId.Value;
        long t2ExtId    = team2.ExternalId.Value;
        string t1Name   = team1.TeamName;
        string t2Name   = team2.TeamName;
        string? t1Logo  = team1.LogoPath;
        string? t2Logo  = team2.LogoPath;

        _logger.LogInformation("Prediction requested: {T1} ({T1Ext}) vs {T2} ({T2Ext})",
            t1Name, t1ExtId, t2Name, t2ExtId);
        
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var mlSvc = scope.ServiceProvider.GetRequiredService<MlPredictionService>();

                await mlSvc.PredictWinnerInBackground(
                    t1ExtId, t2ExtId,
                    t1Name, t2Name,
                    t1Logo, t2Logo
                );
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Unhandled error in prediction Task.Run");
            }
        });

        return Accepted(new
        {
            message = "Ml handles request...",
            team1   = new { name = t1Name, externalId = t1ExtId },
            team2   = new { name = t2Name, externalId = t2ExtId }
        });
    }
    
    [HttpGet("compare-teams")]
    public async Task<IActionResult> GetTeamsComparison(
        [FromQuery] long? t1ExtId = null,
        [FromQuery] long? t2ExtId = null,
        [FromQuery] long? t1Id    = null,
        [FromQuery] long? t2Id    = null)
    {
        long resolvedT1 = t1ExtId ?? t1Id ?? 0;
        long resolvedT2 = t2ExtId ?? t2Id ?? 0;

        if (resolvedT1 == 0 || resolvedT2 == 0)
            return BadRequest(new { message = "Specify t1Id and t2Id (or t1ExtId and t2ExtId)." });

        var teams = await _context.Teams
            .Where(t => t.ExternalId == resolvedT1 || t.ExternalId == resolvedT2)
            .Select(t => new
            {
                t.TeamId,
                t.TeamName,
                t.Acronym,
                t.CurrentRank,
                t.Wins,
                t.Losses,
                t.LogoPath,
                t.ExternalId,
                totalGames = t.Wins + t.Losses,
                WinsRaw    = t.Wins,
                LossesRaw  = t.Losses
            })
            .ToListAsync();

        var t1 = teams.FirstOrDefault(t => t.ExternalId == resolvedT1);
        var t2 = teams.FirstOrDefault(t => t.ExternalId == resolvedT2);

        if (t1 == null || t2 == null)
            return NotFound(new { message = "One or both commands were not found." });

        static object BuildTeamDto(dynamic t)
        {
            int total = t.WinsRaw + t.LossesRaw;
            return new
            {
                teamId     = t.TeamId,
                teamName   = t.TeamName,
                acronym    = t.Acronym,
                currentRank = t.CurrentRank,
                wins       = t.WinsRaw,
                losses     = t.LossesRaw,
                totalGames = total,
                winRate    = total > 0 ? Math.Round((double)t.WinsRaw / total * 100, 1) : 0.0,
                logoPath   = t.LogoPath,
                externalId = t.ExternalId
            };
        }

        return Ok(new
        {
            team1 = BuildTeamDto(t1),
            team2 = BuildTeamDto(t2)
        });
    }
    
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] int? userId  = null)
    {
        var result = await _mlService.GetTrainingHistoryPagedAsync(page, pageSize, userId);
        return Ok(result);
    }


    [HttpPost("set-active/{sessionId:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SetActiveModel(int sessionId)
    {
        var success = await _mlService.SetActiveModel(sessionId);
        if (!success)
            return NotFound(new { message = $"Training session #{sessionId} not found." });

        return Ok(new { message = $"Model #{sessionId} set as active." });
    }
}