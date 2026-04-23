using Newtonsoft.Json;

namespace CsgoPredictionSystem.DTO;

public class LeagueApiDto
{
    [JsonProperty("leagueid")] public long LeagueId { get; set; } 
    [JsonProperty("name")] public string Name { get; set; } 
    [JsonProperty("tier")] public string Tier { get; set; }
}