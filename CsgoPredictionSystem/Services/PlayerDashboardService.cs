using CsgoPredictionSystem.Data;
using Dapper;
using Npgsql;
using CsgoPredictionSystem.DTO.Dashboard;

namespace CsgoPredictionSystem.Services;

public class PlayerDashboardService
{
    private readonly DotaDbContext _context;
    private readonly string _connectionString;

    public PlayerDashboardService(DotaDbContext context, IConfiguration configuration)
    {
        _context = context;
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
                            ?? "Host=localhost;Port=5432;Database=dota_analytics;Username=postgres;Password=1234";
    }
    private NpgsqlConnection CreateConnection() =>
        new NpgsqlConnection(_connectionString);
 public async Task<PlayerProfileDto?> GetProfileAsync(int playerId)
{
    using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();

    const string sql = """
        SELECT
            p.player_id         AS PlayerId,
            p.nickname          AS Nickname,
            p.country           AS Country,
            p.photo_path        AS PhotoPath,
            p.steam_id          AS SteamId,
            p.last_match_time   AS LastMatchTime,
            CAST(COUNT(mt.match_id) AS INTEGER)                             AS TotalMatches,
            CAST(COUNT(mt.match_id) FILTER (WHERE mt.is_winner = true) AS INTEGER) AS TotalWins,
            ROUND(
                COUNT(mt.match_id) FILTER (WHERE mt.is_winner = true)::numeric
                / NULLIF(COUNT(mt.match_id), 0) * 100, 2)                   AS WinratePct,

            t.team_id           AS TeamId,
            t.team_name         AS TeamName,
            t.acronym           AS Acronym,
            t.current_rank      AS TeamRank,
            t.logo_path         AS TeamLogoPath,
            t.wins              AS TeamWins,
            t.losses            AS TeamLosses
        FROM public.players p
        LEFT JOIN public.teams t ON p.team_id = t.team_id
        LEFT JOIN public.player_match_stats pms ON pms.player_id = p.player_id
        LEFT JOIN public.match_teams mt
            ON mt.match_id = pms.match_id AND mt.team_id = pms.team_id
        WHERE p.player_id = @PlayerId
        GROUP BY
            p.player_id, p.nickname, p.country, p.photo_path, p.steam_id,
            p.last_match_time, t.team_id, t.team_name, t.acronym,
            t.current_rank, t.logo_path, t.wins, t.losses
        """;
    
    var result = await conn.QueryAsync<PlayerProfileDto, TeamShortDto, PlayerProfileDto>(
        sql,
        (profile, team) => 
        {
            if (team != null && team.TeamId > 0) 
            {
                profile.Team = team;
            }
            return profile;
        },
        new { PlayerId = playerId },
        splitOn: "TeamId");

    return result.FirstOrDefault();
}


    public async Task<StatsSummaryDto> GetSummaryAsync(int playerId)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        var current  = await GetMetricsForPeriodAsync(conn, playerId, isCurrentMonth: true);
        var previous = await GetMetricsForPeriodAsync(conn, playerId, isCurrentMonth: false);

        var delta = new Dictionary<string, decimal>
        {
            ["AvgKda"]        = current.AvgKda        - previous.AvgKda,
            ["AvgGpm"]        = current.AvgGpm        - previous.AvgGpm,
            ["AvgXpm"]        = current.AvgXpm        - previous.AvgXpm,
            ["AvgNetWorth"]   = current.AvgNetWorth   - previous.AvgNetWorth,
            ["AvgLastHits"]   = current.AvgLastHits   - previous.AvgLastHits,
            ["AvgHeroDamage"] = current.AvgHeroDamage - previous.AvgHeroDamage,
            ["AvgTowerDamage"]= current.AvgTowerDamage- previous.AvgTowerDamage,
        };

        return new StatsSummaryDto(current, delta);
    }

    private static async Task<StatsMetrics> GetMetricsForPeriodAsync(
        NpgsqlConnection conn, int playerId, bool isCurrentMonth)
    {
        const string sql = """
            SELECT
                ROUND(AVG(
                    (pms.kills + pms.assists)::numeric / NULLIF(pms.deaths, 0)
                ), 2)                            AS avg_kda,
                ROUND(AVG(pms.kills), 2)         AS avg_kills,
                ROUND(AVG(pms.deaths), 2)        AS avg_deaths,
                ROUND(AVG(pms.assists), 2)       AS avg_assists,
                ROUND(AVG(pms.gpm), 0)           AS avg_gpm,
                ROUND(AVG(pms.xpm), 0)           AS avg_xpm,
                ROUND(AVG(pms.net_worth), 0)     AS avg_net_worth,
                ROUND(AVG(pms.last_hits), 0)     AS avg_last_hits,
                ROUND(AVG(pms.denies), 0)        AS avg_denies,
                ROUND(AVG(pms.hero_damage), 0)   AS avg_hero_damage,
                ROUND(AVG(pms.tower_damage), 0)  AS avg_tower_damage,
                COUNT(*)                          AS matches_count
            FROM player_match_stats pms
            JOIN matches m ON m.match_id = pms.match_id
            WHERE pms.player_id = @PlayerId
              AND (@IsCurrentMonth = true
                    AND m.match_date >= date_trunc('month', CURRENT_DATE)
                  OR @IsCurrentMonth = false
                    AND m.match_date >= date_trunc('month', CURRENT_DATE) - INTERVAL '1 month'
                    AND m.match_date <  date_trunc('month', CURRENT_DATE))
            """;

        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
            sql, new { PlayerId = playerId, IsCurrentMonth = isCurrentMonth });

        if (row is null)
            return new StatsMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        return new StatsMetrics(
            AvgKda:         (decimal)(row.avg_kda         ?? 0),
            AvgKills:       (decimal)(row.avg_kills        ?? 0),
            AvgDeaths:      (decimal)(row.avg_deaths       ?? 0),
            AvgAssists:     (decimal)(row.avg_assists      ?? 0),
            AvgGpm:         (int)    (row.avg_gpm          ?? 0),
            AvgXpm:         (int)    (row.avg_xpm          ?? 0),
            AvgNetWorth:    (int)    (row.avg_net_worth    ?? 0),
            AvgLastHits:    (int)    (row.avg_last_hits    ?? 0),
            AvgDenies:      (int)    (row.avg_denies       ?? 0),
            AvgHeroDamage:  (int)    (row.avg_hero_damage  ?? 0),
            AvgTowerDamage: (int)    (row.avg_tower_damage ?? 0),
            MatchesCount:   (int)    (row.matches_count    ?? 0));
    }

    // ══════════════════════════════════════════════════════════
    public async Task<PlayerTrendsDto> GetTrendsAsync(
        int playerId, int limit, int? heroId, int? tournamentId)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

       const string timelineSql = """
    SELECT
        m.match_id::int                                  AS "MatchId",
        m.match_date                                     AS "MatchDate",
        ROUND(m.duration / 60.0, 1)::numeric            AS "DurationMinutes",
        h.hero_name                                      AS "HeroName",
        h.primary_attr                                   AS "PrimaryAttr",
        pms.kills::int                                   AS "Kills",
        pms.deaths::int                                  AS "Deaths",
        pms.assists::int                                 AS "Assists",
        ROUND((pms.kills + pms.assists)::numeric / NULLIF(pms.deaths,0), 2)::numeric AS "Kda",
        pms.gpm::int                                     AS "Gpm",
        pms.xpm::int                                     AS "Xpm",
        pms.net_worth::int                               AS "NetWorth",
        pms.last_hits::int                               AS "LastHits",
        pms.hero_damage::int                             AS "HeroDamage",
        pms.tower_damage::int                            AS "TowerDamage",
        mt.is_winner                                     AS "IsWinner",
        mt.side                                          AS "Side",
        t_n.name                                         AS "TournamentName",
        t_n.tier                                         AS "TournamentTier"
    FROM player_match_stats pms
    JOIN matches m          ON m.match_id  = pms.match_id
    JOIN heroes h           ON h.hero_id   = pms.hero_id
    JOIN match_teams mt     ON mt.match_id = pms.match_id AND mt.team_id = pms.team_id
    LEFT JOIN tournaments t_n ON t_n.tournament_id = m.tournament_id
    WHERE pms.player_id = @PlayerId
      AND (@HeroId IS NULL OR pms.hero_id = @HeroId)
      AND (@TournamentId IS NULL OR m.tournament_id = @TournamentId)
    ORDER BY m.match_date DESC
    LIMIT @Limit
    """;

const string weeklySql = """
    SELECT
        date_trunc('week', m.match_date)::timestamp      AS "WeekStart",
        COUNT(*)::int                                    AS "TotalMatches",
        COUNT(*) FILTER (WHERE mt.is_winner = true)::int AS "Wins",
        ROUND(
            COUNT(*) FILTER (WHERE mt.is_winner = true)::numeric
            / NULLIF(COUNT(*), 0) * 100, 1
        )::numeric                                       AS "WinratePct"
    FROM player_match_stats pms
    JOIN matches m      ON m.match_id  = pms.match_id
    JOIN match_teams mt ON mt.match_id = pms.match_id AND mt.team_id = pms.team_id
    WHERE pms.player_id = @PlayerId
      AND m.match_date >= CURRENT_DATE - INTERVAL '3 months'
    GROUP BY date_trunc('week', m.match_date)
    ORDER BY date_trunc('week', m.match_date) ASC
    """;

        var p = new { PlayerId = playerId, Limit = limit, HeroId = heroId, TournamentId = tournamentId };

        var timeline = (await conn.QueryAsync<MatchTimelineDto>(timelineSql, p)).ToList();
        var weekly   = (await conn.QueryAsync<WeeklyWinrateDto>(weeklySql, new { PlayerId = playerId })).ToList();

        return new PlayerTrendsDto(timeline, weekly);
    }


    public async Task<HeroPoolDto> GetHeroPoolAsync(int playerId)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

       const string heroSql = """
    SELECT
        h.hero_id::int                                      AS "HeroId",
        h.hero_name                                         AS "HeroName",
        h.localized_name                                    AS "LocalizedName",
        h.primary_attr                                      AS "PrimaryAttr",
        COALESCE(ARRAY_AGG(DISTINCT hrd.role_name)
                 FILTER (WHERE hrd.role_name IS NOT NULL), '{}')::text[] AS "Roles",
        COUNT(pms.stats_id)::int                            AS "GamesPlayed",
        COUNT(*) FILTER (WHERE mt.is_winner)::int           AS "Wins",
        ROUND(
            COUNT(*) FILTER (WHERE mt.is_winner)::numeric
            / NULLIF(COUNT(*), 0) * 100, 1
        )::numeric                                          AS "WinratePct",
        ROUND(AVG(
            (pms.kills + pms.assists)::numeric / NULLIF(pms.deaths,0)
        ), 2)::numeric                                      AS "AvgKda",
        ROUND(AVG(pms.gpm), 0)::int                         AS "AvgGpm",
        ROUND(AVG(pms.net_worth), 0)::int                   AS "AvgNetWorth"
    FROM player_match_stats pms
    JOIN matches m       ON m.match_id = pms.match_id
    JOIN heroes h        ON h.hero_id  = pms.hero_id
    JOIN match_teams mt  ON mt.match_id = pms.match_id AND mt.team_id = pms.team_id
    LEFT JOIN hero_roles hr ON hr.hero_id = h.hero_id
    LEFT JOIN hero_role_definitions hrd ON hrd.role_id = hr.role_id
    WHERE pms.player_id = @PlayerId
    GROUP BY h.hero_id, h.hero_name, h.localized_name, h.primary_attr
    ORDER BY "GamesPlayed" DESC
    """;

const string roleSql = """
    SELECT
        hrd.role_name                                       AS "RoleName",
        COUNT(pms.stats_id)::int                            AS "GamesInRole"
    FROM player_match_stats pms
    JOIN hero_roles hr ON hr.hero_id = pms.hero_id
    JOIN hero_role_definitions hrd ON hrd.role_id = hr.role_id
    WHERE pms.player_id = @PlayerId
    GROUP BY hrd.role_name
    ORDER BY "GamesInRole" DESC
    """;
        var heroes = (await conn.QueryAsync<HeroStatDto>(heroSql, new { PlayerId = playerId })).ToList();
        var roles  = (await conn.QueryAsync<RoleDistributionDto>(roleSql, new { PlayerId = playerId })).ToList();

        return new HeroPoolDto(heroes, roles);
    }

    public async Task<RadarDto> GetRadarAsync(int playerId)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        
        const string sql = """
            WITH all_players AS (
                SELECT
                    pms.player_id,
                    AVG(pms.gpm) AS avg_gpm,
                    AVG(pms.last_hits) AS avg_lh,
                    AVG((pms.kills + pms.assists)::numeric / NULLIF(pms.deaths,0)) AS avg_kda,
                    AVG(pms.hero_damage) AS avg_hero_dmg,
                    AVG(pms.assists::numeric / NULLIF(pms.kills + pms.assists,0)) AS avg_assist_ratio,
                    AVG(pms.tower_damage) AS avg_tower_dmg,
                    1.0 / NULLIF(AVG(pms.deaths), 0) AS avg_survive,
                    AVG(pms.denies) AS avg_denies
                FROM player_match_stats pms
                GROUP BY pms.player_id
            ),
            ranked AS (
                SELECT
                    player_id,
                    ROUND(PERCENT_RANK() OVER (ORDER BY avg_gpm)          * 100) AS farming_gpm_pct,
                    ROUND(PERCENT_RANK() OVER (ORDER BY avg_lh)           * 100) AS farming_lh_pct,
                    ROUND(PERCENT_RANK() OVER (ORDER BY avg_kda)          * 100) AS fighting_kda_pct,
                    ROUND(PERCENT_RANK() OVER (ORDER BY avg_hero_dmg)     * 100) AS fighting_dmg_pct,
                    ROUND(PERCENT_RANK() OVER (ORDER BY avg_assist_ratio) * 100) AS supporting_pct,
                    ROUND(PERCENT_RANK() OVER (ORDER BY avg_tower_dmg)    * 100) AS pushing_pct,
                    ROUND(PERCENT_RANK() OVER (ORDER BY avg_survive)      * 100) AS surviving_pct,
                    ROUND(PERCENT_RANK() OVER (ORDER BY avg_denies)       * 100) AS vision_pct
                FROM all_players
            )
            SELECT *
            FROM ranked
            WHERE player_id = @PlayerId;
            """;

        var row = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { PlayerId = playerId });

        if (row is null)
            return new RadarDto(new Dictionary<string, int>(), new List<RadarInsightDto>());

        var radar = new Dictionary<string, int>
        {
            ["farming"]    = (int)(((row.farming_gpm_pct  ?? 0m) + (row.farming_lh_pct  ?? 0m)) / 2),
            ["fighting"]   = (int)(((row.fighting_kda_pct ?? 0m) + (row.fighting_dmg_pct ?? 0m)) / 2),
            ["supporting"] = (int)(row.supporting_pct ?? 0m),
            ["pushing"]    = (int)(row.pushing_pct    ?? 0m),
            ["surviving"]  = (int)(row.surviving_pct  ?? 0m),
            ["vision"]     = (int)(row.vision_pct     ?? 0m),
        };

        var insights = BuildInsights(radar);

        return new RadarDto(radar, insights);
    }

    private static List<RadarInsightDto> BuildInsights(Dictionary<string, int> radar)
    {
        var tips = new Dictionary<string, string>
        {
            ["farming"]    = "Focus on early pharma and last hit practice",
            ["fighting"]   = "More aggressive tempo actions in mid game",
            ["supporting"] = "Increase participation in team battles, ward-y",
            ["pushing"]    = "More tower damage in late game",
            ["surviving"]  = "Avoid unnecessary death, positioning",
            ["vision"]     = "Increase ward/deward activity every game",
        };

        var successTip = new Dictionary<string, string>
        {
            ["farming"]    = "Top farm — stable advantage",
            ["fighting"]   = "Excellent aggression in team battles",
            ["supporting"] = "Strong team game — hold",
            ["pushing"]    = "Effective demolition of buildings",
            ["surviving"]  = "Few deaths — good positioning",
            ["vision"]     = "Excellent map control",
        };

        var result = new List<RadarInsightDto>();

        foreach (var (axis, pct) in radar.OrderBy(x => x.Value))
        {
            if (pct < 35)
                result.Add(new RadarInsightDto(axis, pct, "danger", tips[axis]));
            else if (pct < 55)
                result.Add(new RadarInsightDto(axis, pct, "warning", tips[axis]));
        }

        var best = radar.MaxBy(x => x.Value);
        if (best.Value > 70)
            result.Add(new RadarInsightDto(best.Key, best.Value, "success", successTip[best.Key]));

        return result;
    }


    public async Task<PaginatedMatchesDto> GetMatchesAsync(
    int playerId, int page, int perPage, int? heroId, int? tournamentId, string result)
{
    using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();

    const string dataSql = """
        SELECT
            m.match_id::int                                  AS "MatchId",
            m.match_date                                     AS "MatchDate",
            ROUND(m.duration / 60.0, 1)::numeric            AS "DurationMinutes",
            h.hero_name                                      AS "HeroName",
            h.primary_attr                                   AS "PrimaryAttr",
            pms.kills::int                                   AS "Kills",
            pms.deaths::int                                  AS "Deaths",
            pms.assists::int                                 AS "Assists",
            ROUND((pms.kills + pms.assists)::numeric / NULLIF(pms.deaths,0), 2)::numeric AS "Kda",
            pms.gpm::int                                     AS "Gpm",
            pms.xpm::int                                     AS "Xpm",
            pms.net_worth::int                               AS "NetWorth",
            pms.last_hits::int                               AS "LastHits",
            pms.hero_damage::int                             AS "HeroDamage",
            pms.tower_damage::int                            AS "TowerDamage",
            mt.is_winner                                     AS "IsWinner",
            mt.side                                          AS "Side",
            t_n.name                                         AS "TournamentName",
            t_n.tier                                         AS "TournamentTier"
        FROM player_match_stats pms
        JOIN matches m          ON m.match_id  = pms.match_id
        JOIN heroes h           ON h.hero_id   = pms.hero_id
        JOIN match_teams mt     ON mt.match_id = pms.match_id AND mt.team_id = pms.team_id
        LEFT JOIN tournaments t_n ON t_n.tournament_id = m.tournament_id
        WHERE pms.player_id = @PlayerId
          AND (@HeroId IS NULL OR pms.hero_id = @HeroId)
          AND (@TournamentId IS NULL OR m.tournament_id = @TournamentId)
          AND (@Result = 'all'
               OR (@Result = 'win'  AND mt.is_winner = true)
               OR (@Result = 'loss' AND mt.is_winner = false))
        ORDER BY m.match_date DESC
        LIMIT @PerPage OFFSET @Offset
        """;

    const string countSql = """
        SELECT COUNT(*)::int
        FROM player_match_stats pms
        JOIN matches m      ON m.match_id  = pms.match_id
        JOIN match_teams mt ON mt.match_id = pms.match_id AND mt.team_id = pms.team_id
        WHERE pms.player_id = @PlayerId
          AND (@HeroId IS NULL OR pms.hero_id = @HeroId)
          AND (@TournamentId IS NULL OR m.tournament_id = @TournamentId)
          AND (@Result = 'all'
               OR (@Result = 'win'  AND mt.is_winner = true)
               OR (@Result = 'loss' AND mt.is_winner = false))
        """;

    var p = new
    {
        PlayerId = playerId,
        HeroId = heroId,
        TournamentId = tournamentId,
        Result = result,
        PerPage = perPage,
        Offset = (page - 1) * perPage,
    };

    var matches = (await conn.QueryAsync<MatchTimelineDto>(dataSql, p)).ToList();
    var total = await conn.ExecuteScalarAsync<int>(countSql, p);

    return new PaginatedMatchesDto(
        new PaginationMeta(page, perPage, total),
        matches);
}


    public async Task<MatchDetailsDto?> GetMatchDetailsAsync(int matchId, int currentPlayerId)
{
    using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();

    const string sql = """
        SELECT
            p.player_id::int                                  AS "PlayerId",
            p.nickname                                         AS "Nickname",
            p.photo_path                                       AS "PhotoPath",
            (p.player_id = @CurrentPlayerId)                  AS "IsCurrentPlayer",
            h.hero_name                                        AS "HeroName",
            h.primary_attr                                     AS "PrimaryAttr",
            pms.kills::int                                     AS "Kills",
            pms.deaths::int                                    AS "Deaths",
            pms.assists::int                                   AS "Assists",
            ROUND((pms.kills + pms.assists)::numeric / NULLIF(pms.deaths,0), 2)::numeric AS "Kda",
            pms.gpm::int                                       AS "Gpm",
            pms.xpm::int                                       AS "Xpm",
            pms.net_worth::int                                 AS "NetWorth",
            pms.last_hits::int                                 AS "LastHits",
            pms.denies::int                                    AS "Denies",
            pms.hero_damage::int                               AS "HeroDamage",
            pms.tower_damage::int                              AS "TowerDamage",
            mt.is_winner                                       AS "IsWinner",
            mt.side                                            AS "Side"
        FROM player_match_stats pms
        JOIN players p      ON p.player_id = pms.player_id
        JOIN heroes h       ON h.hero_id   = pms.hero_id
        JOIN match_teams mt ON mt.match_id = pms.match_id AND mt.team_id = pms.team_id
        WHERE pms.match_id = @MatchId
        ORDER BY mt.side, pms.player_slot
        """;

    var rows = (await conn.QueryAsync<MatchPlayerDto>(
        sql, new { MatchId = matchId, CurrentPlayerId = currentPlayerId })).ToList();

    if (rows.Count == 0) return null;

    return new MatchDetailsDto(
        matchId,
        rows.Where(r => r.IsWinner).ToList(),
        rows.Where(r => !r.IsWinner).ToList());
}

    
    public async Task<TeamCompareDto?> GetTeamCompareAsync(int playerId)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        
        var team = await conn.QueryFirstOrDefaultAsync<(int? teamId, string? teamName)>(
            "SELECT t.team_id, t.team_name FROM players p JOIN teams t ON t.team_id = p.team_id WHERE p.player_id = @PlayerId",
            new { PlayerId = playerId });

        if (team.teamId is null) return null;

        const string sql = """
                           SELECT
                               p.player_id::int                                  AS "PlayerId",
                               p.nickname                                         AS "Nickname",
                               p.photo_path                                       AS "PhotoPath",
                               (p.player_id = @PlayerId)                          AS "IsCurrentPlayer",
                               ROUND(AVG(pms.gpm), 0)::int                        AS "AvgGpm",
                               ROUND(AVG(pms.xpm), 0)::int                        AS "AvgXpm",
                               ROUND(AVG(pms.net_worth), 0)::int                  AS "AvgNetWorth",
                               ROUND(AVG(pms.last_hits), 0)::int                  AS "AvgLastHits",
                               ROUND(
                                   AVG((pms.kills + pms.assists)::numeric / NULLIF(pms.deaths,0)), 2
                               )::numeric                                         AS "AvgKda",
                               COUNT(pms.stats_id)::int                           AS "GamesPlayed"
                           FROM player_match_stats pms
                           JOIN players p ON p.player_id = pms.player_id
                           JOIN matches m ON m.match_id  = pms.match_id
                           WHERE pms.team_id = @TeamId
                             AND m.match_date >= CURRENT_DATE - INTERVAL '3 months'
                           GROUP BY p.player_id, p.nickname, p.photo_path
                           ORDER BY "AvgGpm" DESC
                           """;

        var comparison = (await conn.QueryAsync<TeamPlayerStatDto>(
            sql, new { PlayerId = playerId, TeamId = team.teamId })).ToList();

        return new TeamCompareDto(team.teamName ?? "", comparison);
    }
}