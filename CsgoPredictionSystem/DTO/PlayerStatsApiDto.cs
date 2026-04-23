namespace CsgoPredictionSystem.DTO;
using Newtonsoft.Json;

public class PlayerStatsApiDto { 
    [JsonProperty("account_id")] public long? AccountId { get; set; } 
    [JsonProperty("hero_id")] public int HeroId { get; set; } 
    [JsonProperty("kills")] public int Kills { get; set; } 
    [JsonProperty("deaths")] public int Deaths { get; set; } 
    [JsonProperty("assists")] public int Assists { get; set; } 
    [JsonProperty("gold_per_min")] public int Gpm { get; set; } 
    [JsonProperty("xp_per_min")] public int Xpm { get; set; } 
    [JsonProperty("net_worth")] public int NetWorth { get; set; } 
    [JsonProperty("last_hits")] public int LastHits { get; set; } 
    [JsonProperty("denies")] public int Denies { get; set; }      
    [JsonProperty("hero_damage")] public int HeroDamage { get; set; } 
    [JsonProperty("tower_damage")] public int TowerDamage { get; set; } 
    [JsonProperty("player_slot")] public int PlayerSlot { get; set; } 
}