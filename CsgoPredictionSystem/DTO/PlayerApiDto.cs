namespace CsgoPredictionSystem.DTO;
using Newtonsoft.Json;


public class PlayerApiDto
{
    [JsonProperty("account_id")] public long AccountId { get; set; } 
    [JsonProperty("name")] public string Name { get; set; } 
    [JsonProperty("country_code")] public string CountryCode { get; set; } 
    [JsonProperty("team_id")] public long? TeamId { get; set; } 
    [JsonProperty("avatar")] public string Avatar { get; set; }
    
    [JsonProperty("last_match_time")] public DateTime? LastMatchTime { get; set; }
}