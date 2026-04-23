namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("hero_role_definitions")] 
public class HeroRoleDefinition
{
    [Key] public int RoleId { get; set; }
    public string RoleName { get; set; }
    public ICollection<HeroRole> HeroRoles { get; set; }
}