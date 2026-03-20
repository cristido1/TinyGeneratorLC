@echo off
REM vllm_start_4b.bat
REM Avvia (o riavvia) il container vLLM per TinyGenerator con Qwen3.5 4B AWQ.

SETLOCAL EnableExtensions

set "CONTAINER_NAME=tinygenerator-vllm"
set "IMAGE=vllm/vllm-openai:nightly"
set "HOST_PORT=8000"
set "CONTAINER_PORT=8000"
set "HOST_MODELS_PATH=C:\vllm_models"
set "CONTAINER_MODELS_PATH=/models"
set "MODEL_SUBDIR=QuantTrio--Qwen3.5-4B-AWQ"
set "MODEL_PATH=%CONTAINER_MODELS_PATH%/%MODEL_SUBDIR%"
set "SERVED_MODEL_NAME=QuantTrio/Qwen3.5-4B-AWQ"
set "MONOMODEL_DESCRIPTION=Qwen3.5-4B-AWQ vLLM"
set "APPSETTINGS_PATH=%~dp0appsettings.json"

REM Parametri runtime
set "GPU_MEMORY_UTIL=0.90"
set "MAX_MODEL_LEN=16384"
set "MAX_NUM_SEQS=4"
set "MAX_NUM_BATCHED_TOKENS=2048"
set "TENSOR_PARALLEL=1"
set "CPU_OFFLOAD_GB=0"
set "LIMIT_MM_PER_PROMPT={\"image\":0,\"video\":0,\"audio\":0}"

where docker >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
  echo Errore: docker non trovato nel PATH.
  pause
  exit /b 1
)

if not exist "%HOST_MODELS_PATH%\%MODEL_SUBDIR%" (
  echo Errore: modello non trovato in "%HOST_MODELS_PATH%\%MODEL_SUBDIR%".
  pause
  exit /b 1
)

echo [vLLM] Sincronizzazione MonomodelMode in appsettings.json...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='%APPSETTINGS_PATH%'; $d='%MONOMODEL_DESCRIPTION%'; if(Test-Path $p){ $raw=Get-Content -Raw -Path $p; $new=[regex]::Replace($raw,'(\"ModelDescription\"\s*:\s*\")[^\"]*(\")',('$1'+$d+'$2'),1); if($new -ne $raw){ Set-Content -Path $p -Value $new -Encoding UTF8; Write-Host '[vLLM] MonomodelMode.ModelDescription aggiornato:' $d } else { Write-Host '[vLLM] Nessuna modifica MonomodelMode (pattern non trovato).' } } else { Write-Host '[vLLM] appsettings.json non trovato:' $p }"

echo [vLLM] Rimozione container precedente (se presente)...
docker rm -f "%CONTAINER_NAME%" >nul 2>&1

set "API_KEY_ARG="
if not "%VLLM_API_KEY%"=="" set "API_KEY_ARG=-e VLLM_API_KEY=%VLLM_API_KEY%"

echo [vLLM] Avvio container "%CONTAINER_NAME%"...
echo [vLLM] Model: %MODEL_PATH%
echo [vLLM] Served model: %SERVED_MODEL_NAME%
echo [vLLM] Multi-modal disabilitato: %LIMIT_MM_PER_PROMPT%
echo [vLLM] Port: %HOST_PORT%

docker run -d --restart unless-stopped ^
  --name "%CONTAINER_NAME%" ^
  --gpus all ^
  -p %HOST_PORT%:%CONTAINER_PORT% ^
  -v "%HOST_MODELS_PATH%:%CONTAINER_MODELS_PATH%" ^
  -e VLLM_SERVER_DEV_MODE=1 ^
  %API_KEY_ARG% ^
  "%IMAGE%" ^
  "%MODEL_PATH%" ^
  --host 0.0.0.0 ^
  --port %CONTAINER_PORT% ^
  --served-model-name "%SERVED_MODEL_NAME%" ^
  --gpu-memory-utilization %GPU_MEMORY_UTIL% ^
  --max-model-len %MAX_MODEL_LEN% ^
  --max-num-seqs %MAX_NUM_SEQS% ^
  --max-num-batched-tokens %MAX_NUM_BATCHED_TOKENS% ^
  --limit-mm-per-prompt "%LIMIT_MM_PER_PROMPT%" ^
  --tensor-parallel-size %TENSOR_PARALLEL% ^
  --trust-remote-code ^
  --no-enable-log-requests

if %ERRORLEVEL% NEQ 0 (
  echo [vLLM] Avvio fallito.
  pause
  exit /b 1
)

echo [vLLM] Avviato. Verifica log:
echo docker logs -f %CONTAINER_NAME%

ENDLOCAL
