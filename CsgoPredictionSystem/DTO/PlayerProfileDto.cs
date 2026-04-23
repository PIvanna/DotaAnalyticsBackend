using Newtonsoft.Json;

namespace CsgoPredictionSystem.DTO;

public class PlayerProfileResponse
{
    [JsonProperty("profile")] public PlayerProfileDto Profile { get; set; }
    [JsonProperty("last_match_time")] public DateTime? LastMatchTime { get; set; }

}

public class PlayerProfileDto
{
    [JsonProperty("personaname")] public string Personaname { get; set; } 
    [JsonProperty("avatarfull")] public string Avatar { get; set; } 
    [JsonProperty("loccountrycode")] public string LocCountryCode { get; set; }
}