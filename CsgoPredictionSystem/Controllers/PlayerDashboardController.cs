using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using CsgoPredictionSystem.DTO.Dashboard;
using CsgoPredictionSystem.Services;

namespace DotaPredictionSystem.Controllers;

[ApiController]
[Route("api/v1/player")]
[Authorize]
public class PlayerDashboardController : ControllerBase
{
    private readonly PlayerDashboardService _svc;

    public PlayerDashboardController(PlayerDashboardService svc)
    {
        _svc = svc;
    }

    private int CurrentPlayerId =>
        int.TryParse(User.FindFirstValue("player_id"), out var id) ? id : 0;
    private bool HasAccess(int playerId) =>
        User.IsInRole("Admin") || CurrentPlayerId == playerId;

    [HttpGet("{playerId:int}/profile")]
    public async Task<IActionResult> GetProfile(int playerId)
    {
        if (!HasAccess(playerId))
            return Forbid();

        try 
        {
            var data = await _svc.GetProfileAsync(playerId);
            return data is null ? NotFound() : Ok(data);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{playerId:int}/stats/summary")]
    public async Task<IActionResult> GetSummary(int playerId)
    {
        if (!HasAccess(playerId))
            return Forbid();

        try 
        {
            var data = await _svc.GetSummaryAsync(playerId);
            return Ok(data);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }
    [HttpGet("{playerId:int}/stats/trends")]
    public async Task<IActionResult> GetTrends(int playerId, int limit = 30, int? heroId = null, int? tournamentId = null)
    {
        if (!HasAccess(playerId))
            return Forbid();

        try 
        {
            var data = await _svc.GetTrendsAsync(playerId, limit, heroId, tournamentId);
            return Ok(data);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{playerId:int}/heroes/pool")]
    public async Task<IActionResult> GetHeroPool(int playerId)
    {
        if (!HasAccess(playerId))
            return Forbid();

        try 
        {
            var data = await _svc.GetHeroPoolAsync(playerId);
            return Ok(data);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{playerId:int}/radar")]
    [ProducesResponseType(typeof(RadarDto), 200)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<IActionResult> GetRadar(int playerId)
    {
        if (!HasAccess(playerId))
        {
            return Forbid();
        }

        try
        {
            var data = await _svc.GetRadarAsync(playerId);

            return Ok(data);
        }
        catch (Exception ex)
        {
            var friendlyErrorMessage = DatabaseErrorHelper.MapDatabaseError(ex);
            
            return StatusCode(500, new { message = friendlyErrorMessage });
        }
    }

    [HttpGet("{playerId:int}/matches")]
    [ProducesResponseType(typeof(PaginatedMatchesDto), 200)]
    [ProducesResponseType(typeof(object), 400)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<IActionResult> GetMatches(
        int playerId,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 20,
        [FromQuery] int? heroId = null,
        [FromQuery] int? tournamentId = null,
        [FromQuery] string result = "all")  
    {
        if (!HasAccess(playerId))
            return Forbid();

        if (page < 1 || perPage is < 1 or > 100)
            return BadRequest(new { message = "page >= 1, perPage від 1 до 100" });

        if (result is not ("all" or "win" or "loss"))
            return BadRequest(new { message = "result має бути: all, win або loss" });

        try 
        {
            var data = await _svc.GetMatchesAsync(playerId, page, perPage, heroId, tournamentId, result);
            return Ok(data);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{playerId:int}/matches/{matchId:int}")]
    [ProducesResponseType(typeof(MatchDetailsDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<IActionResult> GetMatchDetails(int playerId, int matchId)
    {
        if (!HasAccess(playerId))
            return Forbid();

        try 
        {
            var data = await _svc.GetMatchDetailsAsync(matchId, playerId);
            if (data is null)
                return NotFound(new { message = $"Match {matchId} not found" });

            return Ok(data);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{playerId:int}/team/compare")]
    public async Task<IActionResult> GetTeamCompare(int playerId)
    {
        if (!HasAccess(playerId))
            return Forbid();

        try 
        {
            var data = await _svc.GetTeamCompareAsync(playerId);
        
            if (data is null)
                return NotFound(new { message = "Гравець не є членом жодної команди" });

            return Ok(data);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }
}