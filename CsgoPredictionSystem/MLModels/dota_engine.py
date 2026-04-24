import pandas as pd
import psycopg2
import json
import sys
import os
import joblib
from datetime import datetime
from sklearn.ensemble import RandomForestClassifier
from sklearn.model_selection import train_test_split
from sklearn.metrics import accuracy_score, precision_score, f1_score

BASE_PATH = r"C:\Users\ivann\RiderProjects\CsgoPredictionSystem\CsgoPredictionSystem\MLModels"
CSV_PATH  = os.path.join(BASE_PATH, "dota_pro_dataset.csv")
DB_CONFIG = "host=localhost dbname=dota_analytics user=postgres password=1234"


def get_db_conn():
    return psycopg2.connect(DB_CONFIG)


def sync_db_to_csv():
    existing_ids = set()
    if os.path.exists(CSV_PATH):
        try:
            df_existing = pd.read_csv(CSV_PATH)
            if not df_existing.empty:
                existing_ids = set(df_existing['match_id'].unique())
        except Exception:
            pass

    query = """
    WITH team_history AS (
        SELECT
            m.match_id,
            m.match_date,
            mt.team_id,
            mt.is_winner,
            COALESCE((
                SELECT AVG(p.gpm)
                FROM public.player_match_stats p
                WHERE p.match_id = m.match_id AND p.team_id = mt.team_id
            ), 0) AS match_gpm,
            COALESCE((
                SELECT SUM(p.kills)
                FROM public.player_match_stats p
                WHERE p.match_id = m.match_id AND p.team_id = mt.team_id
            ), 0) AS match_kills,
            COALESCE((
                SELECT SUM(p.tower_damage)
                FROM public.player_match_stats p
                WHERE p.match_id = m.match_id AND p.team_id = mt.team_id
            ), 0) AS match_tower_dmg
        FROM public.matches m
        JOIN public.match_teams mt ON m.match_id = mt.match_id
    ),
    historical_stats AS (
        SELECT
            match_id,
            team_id,
            AVG(match_gpm) OVER (
                PARTITION BY team_id ORDER BY match_date
                ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
            ) AS avg_gpm_hist,
            AVG(match_kills) OVER (
                PARTITION BY team_id ORDER BY match_date
                ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
            ) AS avg_kills_hist,
            AVG(match_tower_dmg) OVER (
                PARTITION BY team_id ORDER BY match_date
                ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
            ) AS avg_tower_dmg_hist,
            AVG(CASE WHEN is_winner THEN 1.0 ELSE 0.0 END) OVER (
                PARTITION BY team_id ORDER BY match_date
                ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
            ) AS win_rate_hist,
            AVG(CASE WHEN is_winner THEN 1.0 ELSE 0.0 END) OVER (
                PARTITION BY team_id ORDER BY match_date
                ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING
            ) AS recent_form_hist,
            COUNT(match_id) OVER (
                PARTITION BY team_id ORDER BY match_date
                ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING
            ) AS match_count_hist
        FROM team_history
    )
    SELECT
        m.external_id                                          AS match_id,
        m.match_date,
        rt.external_id                                         AS radiant_team_id,
        dt.external_id                                         AS dire_team_id,
        rt.current_rank                                        AS rad_rank,
        dt.current_rank                                        AS dire_rank,
        CASE WHEN mt_r.is_winner = true THEN 1 ELSE 0 END     AS radiant_win,
        m.duration,
        hs_r.avg_gpm_hist                                      AS rad_avg_gpm,
        hs_r.avg_kills_hist                                    AS rad_total_kills,
        hs_r.avg_tower_dmg_hist                                AS rad_tower_dmg,
        hs_r.win_rate_hist                                     AS rad_win_rate,
        hs_r.recent_form_hist                                  AS rad_recent_form,
        hs_r.match_count_hist                                  AS rad_match_count,
        hs_d.avg_gpm_hist                                      AS dire_avg_gpm,
        hs_d.avg_kills_hist                                    AS dire_total_kills,
        hs_d.avg_tower_dmg_hist                                AS dire_tower_dmg,
        hs_d.win_rate_hist                                     AS dire_win_rate,
        hs_d.recent_form_hist                                  AS dire_recent_form,
        hs_d.match_count_hist                                  AS dire_match_count
    FROM public.matches m
    JOIN public.match_teams mt_r  ON m.match_id = mt_r.match_id AND mt_r.side = 'Radiant'
    JOIN public.teams rt          ON mt_r.team_id = rt.team_id
    JOIN public.match_teams mt_d  ON m.match_id = mt_d.match_id AND mt_d.side = 'Dire'
    JOIN public.teams dt          ON mt_d.team_id = dt.team_id
    JOIN historical_stats hs_r    ON m.match_id = hs_r.match_id AND hs_r.team_id = mt_r.team_id
    JOIN historical_stats hs_d    ON m.match_id = hs_d.match_id AND hs_d.team_id = mt_d.team_id
    WHERE hs_r.avg_gpm_hist IS NOT NULL
      AND hs_d.avg_gpm_hist IS NOT NULL  -- FIXED: AND замість OR, щоб не брати матчі з нулями
    ORDER BY m.match_date DESC
    """

    with get_db_conn() as conn:
        db_df = pd.read_sql(query, conn)

    new_matches = db_df[~db_df['match_id'].isin(existing_ids)]

    if not new_matches.empty:
        header = not os.path.exists(CSV_PATH)
        new_matches.to_csv(CSV_PATH, mode='a', header=header, index=False)
        return len(new_matches)
    return 0


def train(config):
    try:
        new_count = sync_db_to_csv()
    except Exception as e:
        return {"success": False, "error": f"Sync DB → CSV failed: {str(e)}"}

    if not os.path.exists(CSV_PATH):
        return {"success": False, "error": "CSV file not found after sync"}

    df = pd.read_csv(CSV_PATH).dropna()
    if df.empty:
        return {"success": False, "error": "Dataset is empty after cleaning"}

    feature_groups = {
        "gpm":     ["rad_avg_gpm",       "dire_avg_gpm",       "gpm_diff"],
        "kda":     ["rad_total_kills",    "dire_total_kills",   "kills_diff"],
        "tower":   ["rad_tower_dmg",      "dire_tower_dmg",     "tower_dmg_diff"],
        "rank":    ["rad_rank",           "dire_rank",          "rank_diff"],
        "winrate": [
            "rad_win_rate",    "dire_win_rate",    "win_rate_diff",
            "rad_recent_form", "dire_recent_form", "recent_form_diff",
            "rad_match_count", "dire_match_count"
        ]
    }

    df["gpm_diff"]         = df["rad_avg_gpm"]     - df["dire_avg_gpm"]
    df["kills_diff"]       = df["rad_total_kills"]  - df["dire_total_kills"]
    df["tower_dmg_diff"]   = df["rad_tower_dmg"]    - df["dire_tower_dmg"]
    df["rank_diff"]        = df["rad_rank"]          - df["dire_rank"]
    df["win_rate_diff"]    = df["rad_win_rate"]      - df["dire_win_rate"]
    df["recent_form_diff"] = df["rad_recent_form"]   - df["dire_recent_form"]

    selected_features = []
    for key in config.get("features", ["rank", "winrate", "gpm"]):
        if key in feature_groups:
            selected_features.extend(feature_groups[key])

    seen = set()
    selected_features = [f for f in selected_features if not (f in seen or seen.add(f))]

    if not selected_features:
        return {"success": False, "error": "No valid features selected"}

    missing = [f for f in selected_features if f not in df.columns]
    if missing:
        return {"success": False, "error": f"Missing columns in dataset: {missing}"}

    X = df[selected_features]
    y = df['radiant_win']

    X_train, X_test, y_train, y_test = train_test_split(
        X, y, test_size=0.2, random_state=42
    )

    n_estimators = config.get("n_estimators", 200)
    model = RandomForestClassifier(n_estimators=n_estimators, random_state=42, n_jobs=-1)
    model.fit(X_train, y_train)

    y_pred = model.predict(X_test)
    metrics = {
        "acc":  round(float(accuracy_score(y_test, y_pred)),  4),
        "prec": round(float(precision_score(y_test, y_pred, zero_division=0)), 4),
        "f1":   round(float(f1_score(y_test, y_pred, zero_division=0)),        4)
    }

    timestamp         = datetime.now().strftime("%Y%m%d_%H%M%S")
    unique_model_path = os.path.join(BASE_PATH, f"model_{timestamp}.pkl")
    joblib.dump({"model": model, "features": selected_features}, unique_model_path)

    try:
        with get_db_conn() as conn:
            with conn.cursor() as cur:
                u_id = config.get("userId", 1)
                cur.execute("SELECT user_id FROM public.users WHERE user_id = %s", (u_id,))
                if not cur.fetchone():
                    cur.execute("SELECT user_id FROM public.users LIMIT 1")
                    row  = cur.fetchone()
                    u_id = row[0] if row else None

                cur.execute("UPDATE ml_training_history SET is_active = false")
                cur.execute(
                    """
                    INSERT INTO public.ml_training_history
                        (accuracy, precision_score, f1_score, dataset_size,
                         features_list, model_path, trained_at, is_active, user_id)
                    VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s)
                    """,
                    (
                        metrics["acc"], metrics["prec"], metrics["f1"],
                        len(df), ",".join(selected_features), unique_model_path,
                        datetime.now(), True, u_id
                    )
                )
                conn.commit()
    except Exception as db_err:
        return {"success": False, "error": f"Database Error: {str(db_err)}"}

    return {
        "success":          True,
        "metrics":          metrics,
        "datasetSize":      len(df),
        "newMatchesSynced": new_count
    }




def predict(data):
    with get_db_conn() as conn:
        with conn.cursor() as cur:
            cur.execute(
                "SELECT model_path, features_list FROM ml_training_history "
                "WHERE is_active = true ORDER BY trained_at DESC LIMIT 1"
            )
            active_model = cur.fetchone()

            if not active_model:
                return {"success": False, "error": "No active model found. Train a model first."}

            model_file, features_str = active_model
            features = features_str.split(",")

            t1_id = data.get("team1_id")
            t2_id = data.get("team2_id")
            print(f"DEBUG: Predicting {t1_id} vs {t2_id}", file=sys.stderr)

            cur.execute(
                "SELECT external_id, current_rank FROM public.teams "
                "WHERE external_id = ANY(%s)",
                ([t1_id, t2_id],)
            )
            ranks = {row[0]: row[1] for row in cur.fetchall()}

    print(f"DEBUG: Ranks found: {ranks}", file=sys.stderr)

    if not os.path.exists(CSV_PATH):
        return {"success": False, "error": "Dataset CSV not found. Run training first."}

    df = pd.read_csv(CSV_PATH)

    if 'match_date' in df.columns:
        df['match_date'] = pd.to_datetime(df['match_date'], errors='coerce')

    h2h_mask = (
        ((df['radiant_team_id'] == t1_id) & (df['dire_team_id'] == t2_id)) |
        ((df['radiant_team_id'] == t2_id) & (df['dire_team_id'] == t1_id))
    )
    h2h_matches = df[h2h_mask].copy()
    if 'match_date' in h2h_matches.columns:
        h2h_matches = h2h_matches.sort_values('match_date', ascending=False)

    h2h_list = h2h_matches.head(5)[
        ['match_id', 'radiant_team_id', 'dire_team_id', 'radiant_win']
    ].to_dict(orient="records")

    print(f"DEBUG: H2H matches found: {len(h2h_list)}", file=sys.stderr)

    def get_team_stats(team_id):
        """Повертає статистику команди з останньої відомої гри (по даті)."""
        rad_cols = {
            'rad_avg_gpm':     'avg_gpm',
            'rad_total_kills': 'total_kills',
            'rad_tower_dmg':   'tower_dmg',
            'rad_win_rate':    'win_rate',
            'rad_recent_form': 'recent_form',
            'rad_match_count': 'match_count'
        }
        dir_cols = {
            'dire_avg_gpm':    'avg_gpm',
            'dire_total_kills':'total_kills',
            'dire_tower_dmg':  'tower_dmg',
            'dire_win_rate':   'win_rate',
            'dire_recent_form':'recent_form',
            'dire_match_count':'match_count'
        }

        m_rad = df[df['radiant_team_id'] == team_id][
            list(rad_cols.keys()) + (['match_date'] if 'match_date' in df.columns else [])
        ].rename(columns=rad_cols)

        m_dir = df[df['dire_team_id'] == team_id][
            list(dir_cols.keys()) + (['match_date'] if 'match_date' in df.columns else [])
        ].rename(columns=dir_cols)

        combined = pd.concat([m_rad, m_dir], ignore_index=True)

        if combined.empty:
            print(f"DEBUG: No stats for team {team_id}", file=sys.stderr)
            return {}

        if 'match_date' in combined.columns:
            combined = combined.sort_values('match_date', ascending=False)

        return combined.iloc[0].to_dict()

    s1 = get_team_stats(t1_id)
    s2 = get_team_stats(t2_id)

    feature_mapping = {
        "rad_avg_gpm":      s1.get('avg_gpm',     0),
        "dire_avg_gpm":     s2.get('avg_gpm',     0),
        "gpm_diff":         s1.get('avg_gpm',     0) - s2.get('avg_gpm',     0),

        "rad_total_kills":  s1.get('total_kills', 0),
        "dire_total_kills": s2.get('total_kills', 0),
        "kills_diff":       s1.get('total_kills', 0) - s2.get('total_kills', 0),

        "rad_tower_dmg":    s1.get('tower_dmg',  0),
        "dire_tower_dmg":   s2.get('tower_dmg',  0),
        "tower_dmg_diff":   s1.get('tower_dmg',  0) - s2.get('tower_dmg',  0),

        "rad_win_rate":     s1.get('win_rate',    0.5),
        "dire_win_rate":    s2.get('win_rate',    0.5),
        "win_rate_diff":    s1.get('win_rate',    0.5) - s2.get('win_rate',    0.5),

        "rad_recent_form":  s1.get('recent_form', 0.5),
        "dire_recent_form": s2.get('recent_form', 0.5),
        "recent_form_diff": s1.get('recent_form', 0.5) - s2.get('recent_form', 0.5),

        "rad_match_count":  s1.get('match_count', 0),
        "dire_match_count": s2.get('match_count', 0),

        "rad_rank":         ranks.get(t1_id, 100),
        "dire_rank":        ranks.get(t2_id, 100),
        "rank_diff":        ranks.get(t1_id, 100) - ranks.get(t2_id, 100),
    }

    missing_features = [f for f in features if f not in feature_mapping]
    if missing_features:
        print(f"DEBUG: Missing features: {missing_features}", file=sys.stderr)

    if not os.path.exists(model_file):
        return {"success": False, "error": f"Model file not found: {model_file}"}

    payload   = joblib.load(model_file)
    model     = payload["model"]
    input_row = [feature_mapping.get(f, 0) for f in features]
    proba     = model.predict_proba([input_row])[0]

    t1_chance = round(proba[1] * 100, 2)
    t2_chance = round(proba[0] * 100, 2)

    print(f"DEBUG: Result → T1={t1_chance}%, T2={t2_chance}%", file=sys.stderr)

    return {
        "success":          True,
        "team1_win_chance": t1_chance,
        "team2_win_chance": t2_chance,
        "h2h_history":      h2h_list,
        "model_used":       os.path.basename(model_file)
    }



if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(json.dumps({"success": False, "error": "No mode argument (train/predict)"}))
        sys.exit(1)

    mode = sys.argv[1]

    try:
        input_data = sys.stdin.read()

        if not input_data or not input_data.strip():
            print(json.dumps({"success": False, "error": "No input data via stdin"}))
            sys.exit(1)

        params = json.loads(input_data)

    except json.JSONDecodeError as e:
        print(json.dumps({"success": False, "error": f"JSON parse error: {str(e)}"}))
        sys.exit(1)
    except Exception as e:
        print(json.dumps({"success": False, "error": f"Input error: {str(e)}"}))
        sys.exit(1)

    if mode == "train":
        result = train(params)
    elif mode == "predict":
        result = predict(params)
    else:
        result = {"success": False, "error": f"Unknown mode: {mode}"}

    print(json.dumps(result))