#!/usr/bin/env bash
set -euo pipefail

# Script to stop any running ollama processes and start ollama with high priority.
# It tries to use sudo for negative nice when available; otherwise it falls back to a best-effort nice value.

OLLAMA_CMD="${OLLAMA_CMD:-ollama serve}"
# Resolve absolute path for ollama if possible (helps with sudo secure_path)
if [[ "$OLLAMA_CMD" == "ollama serve" ]]; then
    OLLAMA_BIN="$(command -v ollama 2>/dev/null || true)"
    if [ -z "$OLLAMA_BIN" ]; then
        for p in /opt/homebrew/bin/ollama /usr/local/bin/ollama; do
            if [ -x "$p" ]; then OLLAMA_BIN="$p"; break; fi
        done
    fi
    if [ -n "$OLLAMA_BIN" ]; then
        OLLAMA_CMD="$OLLAMA_BIN serve"
    fi
fi
# default concurrency/config for Ollama
OLLAMA_NUM_PARALLEL_DEFAULT="2"
PER_MODEL_THREADS_DEFAULT="4"

LOG_DIR="data/logs"
mkdir -p "$LOG_DIR"
LOG="${LOG:-$LOG_DIR/ollama-restart.log}"
PIDFILE="${PIDFILE:-$LOG_DIR/ollama.pid}"

# Export recommended env vars if not already set
export OLLAMA_NUM_PARALLEL="${OLLAMA_NUM_PARALLEL:-$OLLAMA_NUM_PARALLEL_DEFAULT}"
export OMP_NUM_THREADS="${OMP_NUM_THREADS:-$PER_MODEL_THREADS_DEFAULT}"
export MKL_NUM_THREADS="${MKL_NUM_THREADS:-$PER_MODEL_THREADS_DEFAULT}"
export OLLAMA_MAX_LOADED_MODELS="${OLLAMA_MAX_LOADED_MODELS:-2}"
# By default keep model blobs (do not prune) so model artifacts remain available locally.
export OLLAMA_NOPRUNE="${OLLAMA_NOPRUNE:-1}"

echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] starting (cmd=${OLLAMA_CMD})" >> "$LOG"

# Find running ollama pids
pids=$(pgrep -f "\bollama\b" || true)
if [ -n "$pids" ]; then
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] found running pids: $pids" >> "$LOG"
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] stopping..." >> "$LOG"
    pkill -f "\bollama\b" || true
    sleep 2
    still=$(pgrep -f "\bollama\b" || true)
    if [ -n "$still" ]; then
        echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] still running, trying force kill: $still" >> "$LOG"
        # try graceful then force; allow interactive sudo only when explicitly enabled and a TTY is present
        if [ "${ALLOW_SUDO_PROMPT:-0}" = "1" ] && [ -t 1 ]; then
            sudo kill -15 $still || true
            sleep 1
            sudo kill -9 $still || true
        else
            sudo -n kill -15 $still || true
            sleep 1
            sudo -n kill -9 $still || true
        fi
    fi
fi

echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] starting with high priority" >> "$LOG"

# Choose nice value and method
if command -v sudo >/dev/null 2>&1 && [ "$(id -u)" -ne 0 ]; then
    # Try to run with sudo + nice -20.
    echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] launching with sudo nice -n -20 (env: OLLAMA_NUM_PARALLEL=$OLLAMA_NUM_PARALLEL, OMP_NUM_THREADS=$OMP_NUM_THREADS, OLLAMA_MAX_LOADED_MODELS=$OLLAMA_MAX_LOADED_MODELS, OLLAMA_NOPRUNE=$OLLAMA_NOPRUNE)" >> "$LOG"
    if [ "${ALLOW_SUDO_PROMPT:-0}" = "1" ] && [ -t 1 ]; then
        # Interactive path: allow sudo to prompt for a password
        if sudo -v; then
            sudo nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" nice -n -20 sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
        else
            echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] sudo authentication failed; fallback a nice best-effort" >> "$LOG"
            if nice -n -20 true >/dev/null 2>&1; then
                nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" nice -n -20 sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
            elif nice -n -5 true >/dev/null 2>&1; then
                nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" nice -n -5 sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
            else
                nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
            fi
        fi
    elif sudo -n true 2>/dev/null; then
        # Non-interactive sudo allowed
        sudo -n nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" nice -n -20 sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
    else
        echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] sudo non disponibile senza password; fallback a nice best-effort" >> "$LOG"
        if nice -n -20 true >/dev/null 2>&1; then
            nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" nice -n -20 sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
        elif nice -n -5 true >/dev/null 2>&1; then
            nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" nice -n -5 sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
        else
            nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
        fi
    fi
else
    # Best-effort: try negative nice (may fail if not privileged); fallback to -5 then 0
    if nice -n -20 true >/dev/null 2>&1; then
        nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" nice -n -20 sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
    elif nice -n -5 true >/dev/null 2>&1; then
        nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" nice -n -5 sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
    else
        nohup env OLLAMA_NUM_PARALLEL="$OLLAMA_NUM_PARALLEL" OMP_NUM_THREADS="$OMP_NUM_THREADS" MKL_NUM_THREADS="$MKL_NUM_THREADS" OLLAMA_MAX_LOADED_MODELS="$OLLAMA_MAX_LOADED_MODELS" OLLAMA_NOPRUNE="$OLLAMA_NOPRUNE" sh -c "$OLLAMA_CMD" > "$LOG_DIR/ollama.out" 2>&1 &
    fi
fi

echo $! > "$PIDFILE"
echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] started pid $(cat $PIDFILE)" >> "$LOG"

# Record thread count (try macOS 'thcount' then Linux 'nlwp')
PID=$(cat "$PIDFILE" 2>/dev/null || echo "")
if [ -n "$PID" ]; then
    # Try macOS style
    thcount=""
    if ps -p $PID -o thcount= >/dev/null 2>&1; then
        thcount=$(ps -p $PID -o thcount= | tr -d ' ')
    elif ps -p $PID -o nlwp= >/dev/null 2>&1; then
        thcount=$(ps -p $PID -o nlwp= | tr -d ' ')
    fi
    if [ -n "$thcount" ]; then
        echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] thread_count=${thcount}" >> "$LOG"
    else
        echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) [restart_ollama] thread_count=unknown" >> "$LOG"
    fi
fi

exit 0
