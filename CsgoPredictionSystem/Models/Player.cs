namespace CsgoPredictionSystem.Models;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

[Table("players")]
public class Player
{
    [Key] public int PlayerId { get; set; }
    public long SteamId { get; set; }
    public string Nickname { get; set; }
    public string? Country { get; set; }
    public string? PhotoPath { get; set; }
    
    public DateTime? LastMatchTime { get; set; } 
    
    public int? TeamId { get; set; }
    [ForeignKey("TeamId")] public Team? Team { get; set; }
}