import csv
import os
import sqlite3
import sys
import time
import zipfile
from collections import Counter
from datetime import datetime


ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
DATASET_DIR = os.path.join(ROOT, "data", "datasets", "TAU2020mobile")
META_DIR = os.path.join(DATASET_DIR, "meta_extracted", "TAU-urban-acoustic-scenes-2020-mobile-development")
META_CSV = os.path.join(META_DIR, "meta.csv")
DB_PATH = os.path.join(ROOT, "data", "storage.db")
LIB_ROOT = r"C:\Users\User\Documents\ai\sounds_library\TAU-Urban-2020-Mobile"
LIB_INNER_ROOT = "TAU-urban-acoustic-scenes-2020-mobile-development"
LIB_AUDIO_DIR = os.path.join(LIB_ROOT, LIB_INNER_ROOT, "audio")
LIB_NAME = "TAU-Urban-2020-Mobile"


SCENE_TAG_MAP = {
    "airport": ["airport", "terminal", "indoor", "inside", "travel", "transit", "announcements", "pa"],
    "bus": ["bus", "coach", "vehicle", "transport", "interior", "inside", "engine", "motor"],
    "metro": ["metro", "subway", "train", "rail", "underground", "tunnel", "carriage", "wagon"],
    "metro_station": ["metro_station", "subway_station", "station", "platform", "indoor", "inside", "crowd", "people"],
    "park": ["park", "garden", "outdoor", "outside", "nature", "green", "birds", "wildlife"],
    "public_square": ["public_square", "plaza", "square", "open_space", "outdoor", "outside", "crowd", "people"],
    "shopping_mall": ["shopping_mall", "mall", "indoor", "inside", "retail", "shops", "crowd", "people"],
    "street_pedestrian": ["street_pedestrian", "pedestrian_street", "street", "road", "outdoor", "outside", "crowd", "people"],
    "street_traffic": ["street_traffic", "traffic", "street", "road", "vehicles", "cars", "urban", "city"],
    "tram": ["tram", "streetcar", "rail", "train", "urban", "city", "transport", "transit"],
}


def normalize_token(token: str) -> str:
    t = (token or "").strip().lower()
    t = t.replace(" ", "_").replace("-", "_")
    while "__" in t:
        t = t.replace("__", "_")
    return "".join(ch for ch in t if ch.isalnum() or ch == "_").strip("_")


def build_tags(scene_label: str, identifier: str, source_label: str) -> str:
    tags = []
    seen = set()

    def add(x: str):
        x = normalize_token(x)
        if not x or x in seen:
            return
        seen.add(x)
        tags.append(x)

    # Core ambient pairs / generic context
    for t in ["ambience", "soundscape", "environment", "background", "urban", "city", "tau2020mobile", "dataset"]:
        add(t)

    scene = normalize_token(scene_label)
    if scene:
        add(scene)
    for t in SCENE_TAG_MAP.get(scene_label, []):
        add(t)

    # Identifier usually like city-seq
    ident = (identifier or "").strip().lower()
    city = ident.rsplit("-", 1)[0] if "-" in ident else ident
    city = normalize_token(city)
    if city:
        add(city)

    src = normalize_token(source_label)
    if src:
        add(f"device_{src}")

    return ", ".join(tags)


def extract_all_audio_zips() -> None:
    os.makedirs(LIB_ROOT, exist_ok=True)
    zips = [
        os.path.join(DATASET_DIR, f)
        for f in os.listdir(DATASET_DIR)
        if f.endswith(".zip") and ".audio." in f and f.startswith("TAU-urban-acoustic-scenes-2020-mobile-development.audio.")
    ]
    zips.sort(key=lambda p: int(os.path.basename(p).split(".audio.")[1].split(".zip")[0]))
    print(f"Audio zip trovati: {len(zips)}", flush=True)
    for idx, zp in enumerate(zips, 1):
        t0 = time.time()
        with zipfile.ZipFile(zp, "r") as zf:
            members = [m for m in zf.infolist() if not m.is_dir() and m.filename.lower().endswith('.wav')]
            total = len(members)
            print(f"[extract {idx}/{len(zips)}] {os.path.basename(zp)} wav={total}", flush=True)
            for i, m in enumerate(members, 1):
                dest = os.path.join(LIB_ROOT, m.filename.replace("/", os.sep))
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                # skip if same size already present
                if os.path.exists(dest) and os.path.getsize(dest) == m.file_size:
                    continue
                with zf.open(m, "r") as src, open(dest, "wb") as dst:
                    while True:
                        chunk = src.read(1024 * 1024)
                        if not chunk:
                            break
                        dst.write(chunk)
                if i == 1 or i == total or i % 500 == 0:
                    pct = (i * 100.0 / total) if total else 100.0
                    print(f"[extract {idx}/{len(zips)}] {os.path.basename(zp)} {i}/{total} ({pct:.1f}%)", flush=True)
        print(f"[extract {idx}/{len(zips)}] done in {(time.time()-t0)/60:.1f} min", flush=True)


def import_to_db() -> None:
    if not os.path.exists(META_CSV):
        raise FileNotFoundError(f"Meta CSV non trovato: {META_CSV}")

    rows = []
    with open(META_CSV, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f, delimiter="\t")
        for r in reader:
            rel = (r.get("filename") or "").replace("/", os.sep)
            abs_path = os.path.join(LIB_ROOT, LIB_INNER_ROOT, rel)
            if not os.path.exists(abs_path):
                continue
            filename = os.path.basename(abs_path)
            scene = (r.get("scene_label") or "").strip()
            ident = (r.get("identifier") or "").strip()
            src_label = (r.get("source_label") or "").strip()
            desc = f"TAU 2020 mobile ambient: {scene}; identifier={ident}; source={src_label}"
            tags = build_tags(scene, ident, src_label)
            rows.append(
                (
                    "amb",
                    LIB_NAME,
                    abs_path,
                    filename,
                    desc,
                    tags,
                    10.0,
                    1,
                    datetime.now().isoformat(timespec="seconds"),
                    None,  # license unknown/not set
                )
            )

    print(f"Righe candidate per import: {len(rows)}", flush=True)
    con = sqlite3.connect(DB_PATH)
    try:
        cur = con.cursor()
        before = cur.execute("SELECT COUNT(*) FROM sounds WHERE library = ?", (LIB_NAME,)).fetchone()[0]
        print(f"Record esistenti sounds library={LIB_NAME}: {before}", flush=True)
        sql = (
            "INSERT OR IGNORE INTO sounds "
            "(type, library, filepath, filename, description, tags, duration_seconds, enabled, insert_date, license) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"
        )
        batch = 500
        inserted_est = 0
        for i in range(0, len(rows), batch):
            chunk = rows[i:i+batch]
            cur.executemany(sql, chunk)
            con.commit()
            inserted_est += cur.rowcount if cur.rowcount != -1 else 0
            print(f"[db] {min(i+batch, len(rows))}/{len(rows)} rows processed", flush=True)
        after = cur.execute("SELECT COUNT(*) FROM sounds WHERE library = ?", (LIB_NAME,)).fetchone()[0]
        print(f"Record finali sounds library={LIB_NAME}: {after} (delta={after-before})", flush=True)
    finally:
        con.close()


def main() -> int:
    t0 = time.time()
    extract_all_audio_zips()
    import_to_db()
    # quick sanity summary
    c = Counter()
    for root, _, files in os.walk(LIB_AUDIO_DIR):
        for fn in files:
            if fn.lower().endswith(".wav"):
                c["wav"] += 1
    print(f"File WAV in libreria TAU audio/: {c['wav']}", flush=True)
    print(f"Completato in {(time.time()-t0)/60:.1f} min", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
