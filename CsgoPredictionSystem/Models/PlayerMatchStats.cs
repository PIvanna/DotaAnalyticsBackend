namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("player_match_stats")] 
public class PlayerMatchStats
{
    [Key] public int StatsId { get; set; }
    public int MatchId { get; set; }
    public int PlayerId { get; set; }
    public int HeroId { get; set; }
    
    public int? TeamId { get; set; } 
    [ForeignKey("TeamId")] public Team? Team { get; set; }

    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Gpm { get; set; }
    public int Xpm { get; set; }
    public int NetWorth { get; set; }
    public int LastHits { get; set; }
    public int Denies { get; set; }
    public int HeroDamage { get; set; }
    public int TowerDamage { get; set; }
    public int PlayerSlot { get; set; }

    [ForeignKey("MatchId")] public Match Match { get; set; }
    [ForeignKey("PlayerId")] public Player Player { get; set; }
    [ForeignKey("HeroId")] public Hero Hero { get; set; }
}