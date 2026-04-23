namespace CsgoPredictionSystem.DTO.Dashboard;

public class PlayerProfileDto
{
    public int PlayerId { get; init; }
    public string Nickname { get; init; } = "";
    public string? Country { get; init; }
    public string? PhotoPath { get; init; }
    public long SteamId { get; init; }
    public DateTime? LastMatchTime { get; init; }
    public int TotalMatches { get; init; }
    public int TotalWins { get; init; }
    public decimal WinratePct { get; init; }
    public TeamShortDto? Team { get; set; }
}

public class TeamShortDto
{
    public int TeamId { get; init; }
    public string TeamName { get; init; } = "";
    public string? Acronym { get; init; }
    public int? TeamRank { get; init; }
    public string? TeamLogoPath { get; init; }
    public int TeamWins { get; init; }
    public int TeamLosses { get; init; }
}

public record StatsSummaryDto(
    StatsMetrics Current,
    Dictionary<string, decimal> Delta);

public record StatsMetrics(
    decimal AvgKda,
    decimal AvgKills,
    decimal AvgDeaths,
    decimal AvgAssists,
    int AvgGpm,
    int AvgXpm,
    int AvgNetWorth,
    int AvgLastHits,
    int AvgDenies,
    int AvgHeroDamage,
    int AvgTowerDamage,
    int MatchesCount);


public record PlayerTrendsDto(
    List<MatchTimelineDto> MatchTimeline,
    List<WeeklyWinrateDto> WeeklyWinrate);

public record MatchTimelineDto(
    int MatchId,
    DateTime MatchDate,
    decimal DurationMinutes,
    string HeroName,
    string? PrimaryAttr,
    int Kills,
    int Deaths,
    int Assists,
    decimal Kda,
    int Gpm,
    int Xpm,
    int NetWorth,
    int LastHits,
    int HeroDamage,
    int TowerDamage,
    bool IsWinner,
    string? Side,               
    string? TournamentName,
    string? TournamentTier);

public record WeeklyWinrateDto(
    DateTime WeekStart,
    int TotalMatches,
    int Wins,
    decimal WinratePct);


public record HeroPoolDto(
    List<HeroStatDto> HeroPool,
    List<RoleDistributionDto> RoleDistribution);

public record HeroStatDto
{
    public int HeroId { get; init; }
    public string HeroName { get; init; } = "";
    public string? LocalizedName { get; init; }
    public string? PrimaryAttr { get; init; }
    public string[] Roles { get; init; } = Array.Empty<string>();
    public int GamesPlayed { get; init; }
    public int Wins { get; init; }
    public decimal WinratePct { get; init; }
    public decimal AvgKda { get; init; }
    public int AvgGpm { get; init; }
    public int AvgNetWorth { get; init; }
}

public record RoleDistributionDto
{
    public string RoleName { get; init; } = "";
    public int GamesInRole { get; init; }
}


public record RadarDto(
    Dictionary<string, int> Radar,
    List<RadarInsightDto> Insights);

public record RadarInsightDto(
    string Axis,
    int Pct,
    string Severity,   
    string Tip);


public record PaginatedMatchesDto(
    PaginationMeta Pagination,
    List<MatchTimelineDto> Matches);

public record PaginationMeta(int Page, int PerPage, int Total);

public record MatchDetailsDto(
    int MatchId,
    List<MatchPlayerDto> Radiant,
    List<MatchPlayerDto> Dire);

public record MatchPlayerDto(
    int PlayerId,
    string Nickname,
    string? PhotoPath,
    bool IsCurrentPlayer,
    string HeroName,
    string? PrimaryAttr,
    int Kills,
    int Deaths,
    int Assists,
    decimal Kda,
    int Gpm,
    int Xpm,
    int NetWorth,
    int LastHits,
    int Denies,
    int HeroDamage,
    int TowerDamage,
    bool IsWinner);


public record TeamCompareDto(
    string TeamName,
    List<TeamPlayerStatDto> Comparison);

public record TeamPlayerStatDto(
    int PlayerId,
    string Nickname,
    string? PhotoPath,
    bool IsCurrentPlayer,
    int AvgGpm,
    int AvgXpm,
    int AvgNetWorth,
    int AvgLastHits,
    decimal AvgKda,
    int GamesPlayed);