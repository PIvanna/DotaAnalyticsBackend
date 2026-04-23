using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CsgoPredictionSystem.Data;
using CsgoPredictionSystem.Models;
using CsgoPredictionSystem.DTO;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using CsgoPredictionSystem.Helpers;
using Microsoft.AspNetCore.Authorization;

namespace CsgoPredictionSystem.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly DotaDbContext _context;
    private readonly IConfiguration _config;

    public AuthController(DotaDbContext context, IConfiguration config)
    {
        _context = context;
        _config = config;
    }
    
    
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        try 
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
            {
                return Unauthorized(new { message = "Invalid user ID in token." });
            }

            var user = await _context.Users
                .AsNoTracking() 
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.UserId == userId);

            if (user == null) 
                return NotFound(new { message = "No user found." });

            return Ok(new {
                Username = user.Username,
                Email = user.Email,
                Role = user.Role?.RoleName ?? "No Role", 
                PlayerId = user.PlayerId
            });
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        try 
        {
            if (string.IsNullOrWhiteSpace(dto.Username) || string.IsNullOrWhiteSpace(dto.Password))
                return BadRequest(new { message = "Username and password are required." });

            if (await _context.Users.AnyAsync(u => u.Username == dto.Username))
                return BadRequest(new { message = "This name is already taken" });

            if (await _context.Users.AnyAsync(u => u.Email == dto.Email))
                return BadRequest(new { message = "This Email is already registered" });

            var spectatorRole = await _context.Roles.FirstOrDefaultAsync(r => r.RoleName == "Spectator");
            if (spectatorRole == null)
            {
                return StatusCode(500, new { message = "System error: Default user role not configured." });
            }

            var user = new User {
                Username = dto.Username,
                Email = dto.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
                RoleId = spectatorRole.RoleId 
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();
            
            return Ok(new { message = "Registration is successful! Now you can log in." });
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginDto dto)
    {
        try 
        {
            var user = await _context.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Email == dto.Email);

            if (user == null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
            {
                return Unauthorized(new { message = "Invalid email or password" });
            }

            string roleName = user.Role?.RoleName ?? "Guest";

            var token = GenerateJwtToken(user);
            
            return Ok(new AuthResponseDto {
                Token = token,
                Username = user.Username,
                Role = roleName,
                PlayerId = user.PlayerId   
            });
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }

    private string GenerateJwtToken(User user)
    {
        var claims = new List<Claim> {
            new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role?.RoleName ?? "Guest"),
            new Claim("player_id", (user.PlayerId ?? 0).ToString())
        };

        var jwtKey = _config["Jwt:Key"] ?? "super_secret_key_1234567890123456";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.Now.AddDays(7),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    
    [Authorize]
[HttpPut("profile")]
public async Task<IActionResult> UpdateProfile(UpdateProfileDto dto)
{
    try 
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out int userId))
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new { message = "User not found." });

        dto.Email = dto.Email?.Trim();
        dto.Username = dto.Username?.Trim();

        if (!string.Equals(user.Email, dto.Email, StringComparison.OrdinalIgnoreCase))
        {
            if (await _context.Users.AnyAsync(u => u.Email == dto.Email))
                return BadRequest(new { message = "This email is already taken by another user." });
        }

        if (!string.Equals(user.Username, dto.Username, StringComparison.OrdinalIgnoreCase))
        {
            if (await _context.Users.AnyAsync(u => u.Username == dto.Username))
                return BadRequest(new { message = "This username is already taken." });
        }

        user.Username = dto.Username;
        user.Email = dto.Email;

        await _context.SaveChangesAsync();
        

        return Ok(new { 
            message = "Profile updated successfully", 
            username = user.Username, 
            email = user.Email 
        });
    }
    catch (Exception ex)
    {
        var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
        return BadRequest(new { message = friendlyError });
    }
}

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto dto)
    {
        try 
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
            {
                return Unauthorized(new { message = "Invalid user identity." });
            }

            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound(new { message = "User not found." });

            if (!BCrypt.Net.BCrypt.Verify(dto.CurrentPassword, user.PasswordHash))
            {
                return BadRequest(new { message = "Current password is incorrect." });
            }

            if (dto.CurrentPassword == dto.NewPassword)
            {
                return BadRequest(new { message = "New password must be different from the old one." });
            }

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
            await _context.SaveChangesAsync();


            return Ok(new { message = "Password changed successfully." });
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }
    
    
    [Authorize]
    [HttpPost("link-player/{playerId}")]
    public async Task<IActionResult> LinkPlayer(int playerId)
    {
        try 
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out int userId))
                return Unauthorized(new { message = "Invalid user ID." });

            var user = await _context.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null) return NotFound(new { message = "No user found." });

            var playerExists = await _context.Players.AnyAsync(p => p.PlayerId == playerId);
            if (!playerExists)
                return NotFound(new { message = $"Player with ID {playerId} not found." });

            var isAlreadyLinked = await _context.Users.AnyAsync(u => u.PlayerId == playerId && u.UserId != userId);
            if (isAlreadyLinked)
                return BadRequest(new { message = "This pro player is already linked to another account." });

            var playerRole = await _context.Roles.FirstOrDefaultAsync(r => r.RoleName == "Player");
            if (playerRole == null)
                return StatusCode(500, new { message = "Error configuring roles." });

            user.PlayerId = playerId;
            user.RoleId = playerRole.RoleId; 

            await _context.SaveChangesAsync();
        
            var newToken = GenerateJwtToken(user);

            return Ok(new { 
                message = "The account has been successfully linked!", 
                role = playerRole.RoleName, 
                playerId = user.PlayerId,
                token = newToken 
            });
        }
        catch (Exception ex)
        {
            var friendlyError = DatabaseErrorHelper.MapDatabaseError(ex);
            return BadRequest(new { message = friendlyError });
        }
    }
}