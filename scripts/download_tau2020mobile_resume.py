import json
import os
import sys
import time
import urllib.request


BASE = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "data", "datasets", "TAU2020mobile"))
RECORD_URL = "https://zenodo.org/api/records/3819968"


def download(url: str, dest: str, expected: int, idx: int, total: int) -> str:
    existing = os.path.getsize(dest) if os.path.exists(dest) else 0
    if existing == expected:
        print(f"[{idx}/{total}] SKIP {os.path.basename(dest)} already complete ({existing}/{expected})", flush=True)
        return "skipped"
    if existing > expected:
        print(f"[{idx}/{total}] RESET {os.path.basename(dest)} oversized ({existing}>{expected})", flush=True)
        os.remove(dest)
        existing = 0

    headers = {"User-Agent": "TinyGeneratorLC/TAUDownloader"}
    mode = "wb"
    if existing > 0:
        headers["Range"] = f"bytes={existing}-"
        mode = "ab"
        print(
            f"[{idx}/{total}] RESUME {os.path.basename(dest)} from {existing}/{expected} ({existing/expected*100:.1f}%)",
            flush=True,
        )
    else:
        print(f"[{idx}/{total}] START {os.path.basename(dest)} size={expected}", flush=True)

    req = urllib.request.Request(url, headers=headers)
    try:
        resp = urllib.request.urlopen(req, timeout=60)
    except Exception as e:
        print(f"[{idx}/{total}] ERROR open {os.path.basename(dest)}: {e}", flush=True)
        return "error"

    code = getattr(resp, "status", None) or resp.getcode()
    if existing > 0 and code == 200:
        print(f"[{idx}/{total}] Range ignored by server, restarting file", flush=True)
        resp.close()
        existing = 0
        mode = "wb"
        req = urllib.request.Request(url, headers={"User-Agent": "TinyGeneratorLC/TAUDownloader"})
        resp = urllib.request.urlopen(req, timeout=60)

    downloaded = existing
    chunk_size = 1024 * 1024
    last_print = 0.0
    t0 = time.time()

    with open(dest, mode) as f:
        while True:
            data = resp.read(chunk_size)
            if not data:
                break
            f.write(data)
            downloaded += len(data)
            now = time.time()
            if now - last_print >= 5:
                elapsed = max(0.001, now - t0)
                speed = downloaded / elapsed / (1024 * 1024)
                pct = (downloaded / expected * 100.0) if expected else 0.0
                print(
                    f"[{idx}/{total}] {os.path.basename(dest)} {pct:.1f}% ({downloaded}/{expected}) {speed:.2f} MB/s",
                    flush=True,
                )
                last_print = now

    final_size = os.path.getsize(dest) if os.path.exists(dest) else 0
    if final_size != expected:
        print(
            f"[{idx}/{total}] ERROR size mismatch {os.path.basename(dest)} final={final_size} expected={expected}",
            flush=True,
        )
        return "error"

    elapsed = max(0.001, time.time() - t0)
    avg = final_size / elapsed / (1024 * 1024)
    print(f"[{idx}/{total}] DONE {os.path.basename(dest)} in {elapsed/60:.1f} min avg {avg:.2f} MB/s", flush=True)
    return "downloaded"


def main() -> int:
    os.makedirs(BASE, exist_ok=True)
    with urllib.request.urlopen(RECORD_URL) as r:
        rec = json.load(r)

    files = [f for f in rec.get("files", []) if ".audio." in f.get("key", "")]
    files.sort(key=lambda f: int(f["key"].split(".audio.")[1].split(".zip")[0]))

    summary = {"skipped": 0, "downloaded": 0, "error": 0}
    total = len(files)
    for i, f in enumerate(files, 1):
        res = download(f["links"]["self"], os.path.join(BASE, f["key"]), int(f["size"]), i, total)
        summary[res] = summary.get(res, 0) + 1
        if res == "error":
            print("Stopping on first error.", flush=True)
            return 2

    print(f"SUMMARY {summary}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
