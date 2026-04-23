using System.Security.Claims;
using CsgoPredictionSystem.Data; 
using CsgoPredictionSystem.Helpers;
using CsgoPredictionSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PlayerController : ControllerBase
{
    private readonly PlayerService _playerService;
    private readonly DotaDbContext _context; 
    private readonly ILogger<PlayerController> _logger; 

    public PlayerController(
        PlayerService playerService, 
        DotaDbContext context, 
        ILogger<PlayerController> logger)
    {
        _playerService = playerService;
        _context = context;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetPlayers(
        [FromQuery] int page = 1, 
        [FromQuery] string? search = null, 
        [FromQuery] int? teamId = null, 
        [FromQuery] string? sortBy = "nickname") 
    {
        var result = await _playerService.GetPlayersPagedAsync(page, 12, search, teamId, null, sortBy);
        return Ok(result);
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard() 
    {
        var stats = await _playerService.GetPlayerDashboardAsync();
        return Ok(stats);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetDetails(int id) 
    {
        var result = await _playerService.GetPlayerDetailsAsync(id);
        if (result == null) return NotFound();
        return Ok(result);
    }
    
    [HttpGet("analytics/advanced")]
    public async Task<IActionResult> GetAdvancedAnalytics()
    {
        var data = await _playerService.GetAdvancedPlayerAnalyticsAsync();
        return Ok(data);
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
            _logger.LogError(ex, "Error fetching player lookup");
        
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }
}