namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("users")] 

public class User
{
    [Key] public int UserId { get; set; }
    
    [MaxLength(50)]   
    public string Username { get; set; }
    public string PasswordHash { get; set; }
    public string Email { get; set; }
    public int RoleId { get; set; }
    public int? PlayerId { get; set; } 
    
    [ForeignKey("RoleId")] public Role Role { get; set; }
    [ForeignKey("PlayerId")] public Player Player { get; set; }
}
