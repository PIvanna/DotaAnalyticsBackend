namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("matches")] 
public class Match
{
    [Key] public int MatchId { get; set; }
    public long ExternalId { get; set; }
    public DateTime MatchDate { get; set; }
    public int? Duration { get; set; }
    public int? TournamentId { get; set; }
    
    [ForeignKey("TournamentId")] public Tournament Tournament { get; set; }
    public ICollection<MatchTeam> MatchTeams { get; set; }
    public ICollection<PlayerMatchStats> PlayerStats { get; set; }
}
