namespace CsgoPredictionSystem.DTO;
using Newtonsoft.Json;

public class TeamApiDto
{
    [JsonProperty("team_id")] public long TeamId { get; set; } 
    [JsonProperty("name")] public string Name { get; set; } 
    [JsonProperty("tag")] public string Tag { get; set; } 
    [JsonProperty("rating")] public float Rating { get; set; } 
    [JsonProperty("logo_url")] public string LogoUrl { get; set; }
    [JsonProperty("wins")] public int Wins { get; set; }
    [JsonProperty("losses")] public int Losses { get; set; }
    [JsonProperty("last_match_time")] public long LastMatchTime { get; set; }
}
