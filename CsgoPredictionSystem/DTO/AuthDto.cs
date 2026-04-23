namespace CsgoPredictionSystem.DTO;

public class RegisterDto {
    public string Username { get; set; }
    public string Email { get; set; }
    public string Password { get; set; }
}

public class LoginDto {
    public string Email { get; set; }
    public string Password { get; set; }
}

public class AuthResponseDto {
    public string Token { get; set; }
    public string Username { get; set; }
    public string Role { get; set; }
    public int? PlayerId { get; set; } 
    public bool IsPlayer => PlayerId.HasValue;
}

public class UpdateProfileDto {
    public string Username { get; set; }
    public string Email { get; set; }
}

public class ChangePasswordDto {
    public string CurrentPassword { get; set; }
    public string NewPassword { get; set; }
}