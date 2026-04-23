using CsgoPredictionSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Player,Spectator")]
public class TeamController : ControllerBase
{
    private readonly TeamService _teamService;
    private readonly ILogger<TeamController> _logger;

    public TeamController(TeamService teamService, ILogger<TeamController> logger)
    {
        _teamService = teamService;
        _logger = logger;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetTeamsDashboard()
    {
        try
        {
            var result = await _teamService.GetTeamsDashboardAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetTeams(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 12,
        [FromQuery] string? search = null,
        [FromQuery] int? minRank = null,
        [FromQuery] int? maxRank = null,
        [FromQuery] string sortBy = "rank")
    {
        try
        {
            var result = await _teamService.GetTeamsPagedAsync(
                page, pageSize, search, minRank, maxRank, sortBy);
            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("lookup")]
    public async Task<IActionResult> GetTeamsLookup()
    {
        try
        {
            var teams = await _teamService.GetTeamsLookupAsync();
            return Ok(teams);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetTeamDetails(int id)
    {
        try
        {
            var result = await _teamService.GetTeamDetailsAsync(id);
            if (result == null)
                return NotFound(new { message = "The command was not found." });
            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{id:int}/analytics")]
    public async Task<IActionResult> GetAdvancedAnalytics(int id)
    {
        try
        {
            var result = await _teamService.GetAdvancedTeamAnalyticsAsync(id);
            if (result == null)
                return NotFound(new { message = "Analytics not available — command not found." });
            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }
}