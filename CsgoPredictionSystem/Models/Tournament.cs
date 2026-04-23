namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;



[Table("tournaments")] 
public class Tournament
{
    [Key] public int TournamentId { get; set; }
    public string Name { get; set; }
    public string Tier { get; set; }
    public long? ExternalId { get; set; }
    public ICollection<Match> Matches { get; set; }
}