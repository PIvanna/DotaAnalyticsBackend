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
CSV_PATH = os.path.join(BASE_PATH, "dota_pro_dataset.csv")
DB_CONFIG = "host=localhost dbname=dota_analytics user=postgres password=1234"

def get_db_conn():
    return psycopg2.connect(DB_CONFIG)

def sync_db_to_csv():
    conn = get_db_conn()
    try:
        existing_ids = set()
        if os.path.exists(CSV_PATH):
            try:
                df_existing = pd.read_csv(CSV_PATH)
                if not df_existing.empty:
                    existing_ids = set(df_existing['match_id'].unique())
            except: pass

        query = """
        WITH team_history AS (
            SELECT 
                m.match_id,
                m.match_date,
                mt.team_id,
                mt.is_winner,
                COALESCE((SELECT AVG(p.gpm) FROM public.player_match_stats p WHERE p.match_id = m.match_id AND p.team_id = mt.team_id), 0) as match_gpm,
                COALESCE((SELECT SUM(p.kills) FROM public.player_match_stats p WHERE p.match_id = m.match_id AND p.team_id = mt.team_id), 0) as match_kills,
                COALESCE((SELECT SUM(p.tower_damage) FROM public.player_match_stats p WHERE p.match_id = m.match_id AND p.team_id = mt.team_id), 0) as match_tower_dmg
            FROM public.matches m
            JOIN public.match_teams mt ON m.match_id = mt.match_id
        ),
        historical_stats AS (
            SELECT 
                match_id,
                team_id,
                AVG(match_gpm) OVER (PARTITION BY team_id ORDER BY match_date ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) as avg_gpm_hist,
                AVG(match_kills) OVER (PARTITION BY team_id ORDER BY match_date ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) as avg_kills_hist,
                AVG(match_tower_dmg) OVER (PARTITION BY team_id ORDER BY match_date ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) as avg_tower_dmg_hist,
                AVG(CASE WHEN is_winner THEN 1.0 ELSE 0.0 END) OVER (PARTITION BY team_id ORDER BY match_date ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) as win_rate_hist,
                AVG(CASE WHEN is_winner THEN 1.0 ELSE 0.0 END) OVER (PARTITION BY team_id ORDER BY match_date ROWS BETWEEN 10 PRECEDING AND 1 PRECEDING) as recent_form_hist,
                COUNT(match_id) OVER (PARTITION BY team_id ORDER BY match_date ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) as match_count_hist
            FROM team_history
        )
        SELECT 
            m.external_id as match_id,
            rt.external_id as radiant_team_id,
            dt.external_id as dire_team_id,
            rt.current_rank as rad_rank,
            dt.current_rank as dire_rank,
            CASE WHEN mt_r.is_winner = true THEN 1 ELSE 0 END as radiant_win,
            m.duration,
            hs_r.avg_gpm_hist as rad_avg_gpm,
            hs_r.avg_kills_hist as rad_total_kills,
            hs_r.avg_tower_dmg_hist as rad_tower_dmg,
            hs_r.win_rate_hist as rad_win_rate,
            hs_r.recent_form_hist as rad_recent_form,
            hs_r.match_count_hist as rad_match_count,
            hs_d.avg_gpm_hist as dire_avg_gpm,
            hs_d.avg_kills_hist as dire_total_kills,
            hs_d.avg_tower_dmg_hist as dire_tower_dmg,
            hs_d.win_rate_hist as dire_win_rate,
            hs_d.recent_form_hist as dire_recent_form,
            hs_d.match_count_hist as dire_match_count
        FROM public.matches m
        JOIN public.match_teams mt_r ON m.match_id = mt_r.match_id AND mt_r.side = 'Radiant'
        JOIN public.teams rt ON mt_r.team_id = rt.team_id
        JOIN public.match_teams mt_d ON m.match_id = mt_d.match_id AND mt_d.side = 'Dire'
        JOIN public.teams dt ON mt_d.team_id = dt.team_id
        JOIN historical_stats hs_r ON m.match_id = hs_r.match_id AND hs_r.team_id = mt_r.team_id
        JOIN historical_stats hs_d ON m.match_id = hs_d.match_id AND hs_d.team_id = mt_d.team_id
        WHERE hs_r.avg_gpm_hist IS NOT NULL OR hs_d.avg_gpm_hist IS NOT NULL
        ORDER BY m.match_date DESC
        """
        db_df = pd.read_sql(query, conn)
        new_matches = db_df[~db_df['match_id'].isin(existing_ids)]
        
        if not new_matches.empty:
            new_matches.to_csv(CSV_PATH, mode='a', header=not os.path.exists(CSV_PATH), index=False)
            return len(new_matches)
        return 0
    finally:
        conn.close()

def train(config):
    try:
        new_count = sync_db_to_csv()
    except Exception as e:
        return {"success": False, "error": f"Sync DB to CSV failed: {str(e)}"}

    if not os.path.exists(CSV_PATH): 
        return {"success": False, "error": "CSV file not found"}
    
    df = pd.read_csv(CSV_PATH).dropna()
    if df.empty: 
        return {"success": False, "error": "Dataset is empty after cleaning"}

    feature_groups = {
        "gpm": ["rad_avg_gpm", "dire_avg_gpm"],
        "kda": ["rad_total_kills", "dire_total_kills"],
        "tower": ["rad_tower_dmg", "dire_tower_dmg"],
        "rank": ["rad_rank", "dire_rank"],
        "winrate": ["rad_win_rate", "dire_win_rate", "rad_recent_form", "dire_recent_form", "rad_match_count", "dire_match_count"]
    }
    
    selected_features = []
    features_to_use = config.get("features", ["rank", "winrate", "gpm"])
    for f_key in features_to_use:
        if f_key in feature_groups: selected_features.extend(feature_groups[f_key])

    X = df[selected_features]
    y = df['radiant_win']
    X_train, X_test, y_train, y_test = train_test_split(X, y, test_size=0.2, random_state=42)

    model = RandomForestClassifier(n_estimators=config.get("epochs", 100), random_state=42)
    model.fit(X_train, y_train)

    y_pred = model.predict(X_test)
    metrics = {
        "acc": float(accuracy_score(y_test, y_pred)),
        "prec": float(precision_score(y_test, y_pred)),
        "f1": float(f1_score(y_test, y_pred))
    }

    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    unique_model_path = os.path.join(BASE_PATH, f"model_{timestamp}.pkl")
    joblib.dump({"model": model, "features": selected_features}, unique_model_path)

    conn = None
    try:
        conn = get_db_conn()
        cur = conn.cursor()
        
        u_id = config.get("userId", 1)
        cur.execute("SELECT user_id FROM public.users WHERE user_id = %s", (u_id,))
        if not cur.fetchone():
            cur.execute("SELECT user_id FROM public.users LIMIT 1")
            row = cur.fetchone()
            u_id = row[0] if row else None

        cur.execute("UPDATE ml_training_history SET is_active = false")
        
        cur.execute("""
            INSERT INTO public.ml_training_history 
            (accuracy, precision_score, f1_score, dataset_size, features_list, model_path, trained_at, is_active, user_id)
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s)
        """, (
            metrics["acc"], metrics["prec"], metrics["f1"], 
            len(df), ",".join(selected_features), unique_model_path, 
            datetime.now(), True, u_id
        ))
        
        conn.commit()
        cur.close()
    except Exception as db_error:
        return {"success": False, "error": f"Database Error: {str(db_error)}"}
    finally:
        if conn: conn.close()

    return {"success": True, "metrics": metrics, "datasetSize": len(df), "newMatchesSynced": new_count}

def predict(data):
    conn = get_db_conn()
    cur = conn.cursor()
    cur.execute("SELECT model_path, features_list FROM ml_training_history WHERE is_active = true ORDER BY trained_at DESC LIMIT 1")
    active_model = cur.fetchone()
    if not active_model: 
        cur.close()
        conn.close()
        return {"success": False, "error": "No active model found"}
    
    model_file, features_str = active_model
    features = features_str.split(",")
    
    t1_id, t2_id = data.get("team1_id"), data.get("team2_id")
    print(f"DEBUG: Python received IDs {t1_id} and {t2_id}", file=sys.stderr)

    cur.execute("SELECT external_id, current_rank FROM public.teams WHERE external_id IN (%s, %s)", (t1_id, t2_id))
    ranks = {row[0]: row[1] for row in cur.fetchall()}
    cur.close()
    conn.close()

    df = pd.read_csv(CSV_PATH)


    h2h_matches = df[
        ((df['radiant_team_id'] == t1_id) & (df['dire_team_id'] == t2_id)) | 
        ((df['radiant_team_id'] == t2_id) & (df['dire_team_id'] == t1_id))
    ].copy()


    h2h_list = h2h_matches.head(5)[['match_id', 'radiant_team_id', 'dire_team_id', 'radiant_win']].to_dict(orient="records")

    def get_team_stats(team_id):
        m1 = df[df['radiant_team_id'] == team_id][['rad_avg_gpm', 'rad_total_kills', 'rad_tower_dmg', 'rad_win_rate', 'rad_recent_form', 'rad_match_count']].rename(columns=lambda x: x.replace('rad_', ''))
        m2 = df[df['dire_team_id'] == team_id][['dire_avg_gpm', 'dire_total_kills', 'dire_tower_dmg', 'dire_win_rate', 'dire_recent_form', 'dire_match_count']].rename(columns=lambda x: x.replace('dire_', ''))
        combined = pd.concat([m1, m2])
        if combined.empty: return {}
        return combined.iloc[-1].to_dict()

    s1, s2 = get_team_stats(t1_id), get_team_stats(t2_id)
    if not s1: print(f"DEBUG: No stats found for team {t1_id}", file=sys.stderr)
    if not s2: print(f"DEBUG: No stats found for team {t2_id}", file=sys.stderr)
    mapping = {
        "rad_avg_gpm": s1.get('avg_gpm', 0), "dire_avg_gpm": s2.get('avg_gpm', 0),
        "rad_total_kills": s1.get('total_kills', 0), "dire_total_kills": s2.get('total_kills', 0),
        "rad_tower_dmg": s1.get('tower_dmg', 0), "dire_tower_dmg": s2.get('tower_dmg', 0),
        "rad_win_rate": s1.get('win_rate', 0.5), "dire_win_rate": s2.get('win_rate', 0.5),
        "rad_recent_form": s1.get('recent_form', 0.5), "dire_recent_form": s2.get('recent_form', 0.5),
        "rad_match_count": s1.get('match_count', 0), "dire_match_count": s2.get('match_count', 0),
        "rad_rank": ranks.get(t1_id, 100), "dire_rank": ranks.get(t2_id, 100)
    }

    payload = joblib.load(model_file)
    model = payload["model"]
    input_row = [mapping.get(f, 0) for f in features]
    proba = model.predict_proba([input_row])[0]
    
    return {
        "success": True,
        "team1_win_chance": round(proba[1] * 100, 2),
        "team2_win_chance": round(proba[0] * 100, 2),
        "h2h_history": h2h_list, 
        "model_used": os.path.basename(model_file)
    }

if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(1)
    
    mode = sys.argv[1]
    
    try:
        input_data = sys.stdin.read()
        if not input_data:
            print(json.dumps({"success": False, "error": "No input data received via stdin"}))
            sys.exit(1)
            
        params = json.loads(input_data)
    except Exception as e:
        print(json.dumps({"success": False, "error": f"JSON parse error: {str(e)}"}))
        sys.exit(1)

    if mode == "train": 
        print(json.dumps(train(params)))
    elif mode == "predict": 
        print(json.dumps(predict(params)))