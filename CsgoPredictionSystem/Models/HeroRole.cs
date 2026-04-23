namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations.Schema;

[Table("hero_roles")] 
public class HeroRole 
{
    public int HeroId { get; set; }
    public int RoleId { get; set; }
    [ForeignKey("HeroId")] public Hero Hero { get; set; }
    [ForeignKey("RoleId")] public HeroRoleDefinition Role { get; set; }
}