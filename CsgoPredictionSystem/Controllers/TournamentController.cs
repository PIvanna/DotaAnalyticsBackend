using Microsoft.AspNetCore.Mvc;
using CsgoPredictionSystem.Services;
using Microsoft.AspNetCore.Authorization;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Player,Spectator")]
public class TournamentController : ControllerBase
{
    private readonly TournamentService _tournamentService;
    private readonly ILogger<TournamentController> _logger;

    public TournamentController(TournamentService tournamentService, ILogger<TournamentController> logger)
    {
        _tournamentService = tournamentService;
        _logger = logger;
    }
    
    [HttpGet]
    public async Task<IActionResult> GetTournaments(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 12,
        [FromQuery] string? search = null,
        [FromQuery] int? tier = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string sortOrder = "desc")
    {
        try 
        {
            var result = await _tournamentService.GetTournamentsPaged(
                page, pageSize, search, tier, fromDate, toDate, sortOrder);
            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }
    
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        try 
        {
            var result = await _tournamentService.GetTournamentDashboardAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }
    
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(typeof(object), 404)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<IActionResult> GetTournamentDetails(int id)
    {
        try
        {
            var data = await _tournamentService.GetTournamentDetailsAsync(id);
        
            if (data == null)
                return NotFound(new { message = $"Tournament with ID {id} not found" });

            return Ok(data);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{id:int}/analytics")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(typeof(object), 404)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<IActionResult> GetTournamentAnalytics(int id)
    {
        try
        {
            var result = await _tournamentService.GetTournamentAnalyticsAsync(id);

            if (result == null)
            {
                return NotFound(new { message = "Analytics not available — tournament not found or has no matches." });
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{id:int}/teams")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(typeof(object), 400)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<IActionResult> GetTournamentTeams(
        int id,
        [FromQuery] string? search = null,
        [FromQuery] string sortOrder = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
        {
            return BadRequest(new { message = "Incorrect pagination parameters: page >= 1, size from 1 to 100." });
        }

        try
        {
            var result = await _tournamentService.GetTournamentTeamsPaged(id, search, sortOrder, page, pageSize);

            if (result == null)
            {
                return NotFound(new { message = $"Tournament with ID {id} not found." });
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);

            return StatusCode(500, new { message = friendlyError });
        }
    }

    [HttpGet("{id:int}/matches")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(typeof(object), 400)]
    [ProducesResponseType(typeof(object), 500)]
    public async Task<IActionResult> GetTournamentMatches(
        int id,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string sortOrder = "desc",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        if (page < 1 || pageSize < 1 || pageSize > 100)
        {
            return BadRequest(new { message = "Incorrect parameters: page >= 1, size from 1 to 100." });
        }

        if (from.HasValue && to.HasValue && to < from)
        {
            return BadRequest(new { message = "The date 'to' cannot be earlier than the date 'from'." });
        }

        try
        {
            var result = await _tournamentService.GetTournamentMatchesPaged(id, from, to, sortOrder, page, pageSize);

            if (result == null)
            {
                return NotFound(new { message = $"Tournament with ID {id} not found." });
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving tournament matches {TournamentId}. Period: {From} - {To}", id, from, to);

            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);

            return StatusCode(500, new { message = friendlyError });
        }
    }
}