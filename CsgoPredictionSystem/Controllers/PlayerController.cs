using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Helpers;
using CsgoPredictionSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Player,Spectator")]
public class PlayerController : ControllerBase
{
    private readonly PlayerService _playerService;
    private readonly ILogger<PlayerController> _logger;

    public PlayerController(PlayerService playerService, ILogger<PlayerController> logger)
    {
        _playerService = playerService;
        _logger = logger;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        try
        {
            var result = await _playerService.GetPlayerDashboardAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetPlayers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 12,
        [FromQuery] string? search = null,
        [FromQuery] int? teamId = null,
        [FromQuery] string? country = null,
        [FromQuery] string sortBy = "nickname")
    {
        try
        {
            var result = await _playerService.GetPlayersPagedAsync(
                page, pageSize, search, teamId, country, sortBy);
            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("lookup")]
    public async Task<IActionResult> GetLookup()
    {
        try
        {
            var players = await _playerService.GetAvailablePlayersLookupAsync();
            return Ok(players);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("analytics/global")]
    public async Task<IActionResult> GetGlobalAnalytics()
    {
        try
        {
            var data = await _playerService.GetGlobalPlayerAnalyticsAsync();
            return Ok(data);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetDetails(int id)
    {
        try
        {
            var result = await _playerService.GetPlayerDetailsAsync(id);
            if (result == null)
                return NotFound(new { message = "Player not found." });
            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{id:int}/analytics")]
    public async Task<IActionResult> GetPlayerAnalytics(int id)
    {
        try
        {
            var result = await _playerService.GetPlayerAnalyticsAsync(id);
            if (result == null)
                return NotFound(new { message = "Analytics not available — player not found or no matches." });
            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }
}