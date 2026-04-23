namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("heroes")] 
public class Hero
{
    [Key] public int HeroId { get; set; } 
    public string HeroName { get; set; }
    public string LocalizedName { get; set; }
    public string PrimaryAttr { get; set; }
    public ICollection<HeroRole> HeroRoles { get; set; }
    public ICollection<PlayerMatchStats> Stats { get; set; }
}