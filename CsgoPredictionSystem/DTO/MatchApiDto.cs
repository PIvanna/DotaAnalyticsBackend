namespace CsgoPredictionSystem.DTO;
using Newtonsoft.Json;

public class MatchApiDto
{
    [JsonProperty("match_id")] public long MatchId { get; set; } 
    [JsonProperty("start_time")] public long StartTime { get; set; } 
    [JsonProperty("duration")] public int Duration { get; set; } 
    [JsonProperty("radiant_win")] public bool RadiantWin { get; set; } 
    [JsonProperty("leagueid")] public long LeagueId { get; set; } 
    [JsonProperty("league_name")] public string LeagueName { get; set; } 
    [JsonProperty("radiant_team_id")] public long? RadiantTeamId { get; set; }
    
    [JsonProperty("dire_team_id")] public long? DireTeamId { get; set; }
    [JsonProperty("radiant_score")] public int RadiantScore { get; set; }
    [JsonProperty("dire_score")] public int DireScore { get; set; }
}