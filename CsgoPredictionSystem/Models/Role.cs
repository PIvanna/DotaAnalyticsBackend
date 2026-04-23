namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations;

using System.ComponentModel.DataAnnotations.Schema;

[Table("roles")] 
public class Role
{
    [Key] public int RoleId { get; set; }
    
    [MaxLength(20)]
    public string RoleName { get; set; }
    public ICollection<User> Users { get; set; }
}