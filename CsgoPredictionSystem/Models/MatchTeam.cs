namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations.Schema;

[Table("match_teams")] 
public class MatchTeam
{
    public int MatchId { get; set; }
    public int TeamId { get; set; }
    public int Score { get; set; }
    public bool IsWinner { get; set; }
    public string Side { get; set; } 
    
    [ForeignKey("MatchId")] public Match Match { get; set; }
    [ForeignKey("TeamId")] public Team Team { get; set; }
}