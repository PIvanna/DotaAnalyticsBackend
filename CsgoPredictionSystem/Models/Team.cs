namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations;

using System.ComponentModel.DataAnnotations.Schema;

[Table("teams")] 
public class Team
{
    [Key] public int TeamId { get; set; }
    public string TeamName { get; set; }
    public string? Acronym { get; set; } 
    public int? CurrentRank { get; set; }
    public string? LogoPath { get; set; } 
    public long? ExternalId { get; set; }
    
    public int Wins { get; set; }
    public int Losses { get; set; }
    public long? LastMatchTime { get; set; }

    public ICollection<Player>? Players { get; set; }
    public ICollection<MatchTeam>? MatchTeams { get; set; }
}