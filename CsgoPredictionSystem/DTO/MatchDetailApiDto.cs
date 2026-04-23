namespace CsgoPredictionSystem.DTO;
using Newtonsoft.Json;

public class MatchDetailApiDto { 
    [JsonProperty("radiant_score")] public int RadiantScore { get; set; }
    [JsonProperty("dire_score")] public int DireScore { get; set; }
    [JsonProperty("radiant_win")] public bool RadiantWin { get; set; }
    [JsonProperty("radiant_team_id")] public long? RadiantTeamId { get; set; }
    [JsonProperty("dire_team_id")] public long? DireTeamId { get; set; }
    [JsonProperty("players")] public List<PlayerStatsApiDto> Players { get; set; } 
}