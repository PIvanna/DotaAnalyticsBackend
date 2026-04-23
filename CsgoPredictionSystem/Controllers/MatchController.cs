using CsgoPredictionSystem.Helpers;
using CsgoPredictionSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Player,Spectator")]
public class MatchController : ControllerBase
{
    private readonly MatchService _matchService;
    private readonly ILogger<MatchController> _logger;

    public MatchController(MatchService matchService, ILogger<MatchController> logger)
    {
        _matchService = matchService;
        _logger = logger;
    }
    
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        try
        {
            var result = await _matchService.GetMatchDashboardAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetDashboard");
            return StatusCode(500, new { message = DatabaseErrorHelper.MapDatabaseError(ex) });
        }
    }
    
    [HttpGet]
    public async Task<IActionResult> GetMatches(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int? teamId = null,
        [FromQuery] int? tournamentId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string sortOrder = "desc")
    {
        try
        {
            var result = await _matchService.GetMatchesPagedAsync(
                page, pageSize, teamId, tournamentId, from, to, sortOrder);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetMatches");
            return StatusCode(500, new { message = DatabaseErrorHelper.MapDatabaseError(ex) });
        }
    }
    
    [HttpGet("analytics")]
    public async Task<IActionResult> GetAnalytics()
    {
        try
        {
            var result = await _matchService.GetMatchAnalyticsAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetAnalytics");
            return StatusCode(500, new { message = DatabaseErrorHelper.MapDatabaseError(ex) });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetMatchDetails(int id)
    {
        try
        {
            var result = await _matchService.GetMatchDetailsAsync(id);
            if (result == null)
                return NotFound(new { message = "Матч не знайдено." });
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetMatchDetails for id={Id}", id);
            return StatusCode(500, new { message = DatabaseErrorHelper.MapDatabaseError(ex) });
        }
    }
}