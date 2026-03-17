@echo off
REM vllm_start.bat
REM Avvia (o riavvia) il container vLLM per TinyGenerator con parametri stabili.

SETLOCAL EnableExtensions

set "CONTAINER_NAME=tinygenerator-vllm"
set "IMAGE=vllm/vllm-openai:nightly"
set "HOST_PORT=8000"
set "CONTAINER_PORT=8000"
set "HOST_MODELS_PATH=C:\vllm_models"
set "CONTAINER_MODELS_PATH=/models"
set "MODEL_SUBDIR=Vishva007--Qwen3.5-9B-W4A16-AutoRound-GPTQ"
set "MODEL_PATH=%CONTAINER_MODELS_PATH%/%MODEL_SUBDIR%"
set "TOKENIZER_SUBDIR=QuantTrio--Qwen3.5-9B-AWQ"
set "TOKENIZER_PATH=%CONTAINER_MODELS_PATH%/%TOKENIZER_SUBDIR%"
set "SERVED_MODEL_NAME=QuantTrio/Qwen3.5-9B-AWQ"

REM Parametri runtime
set "GPU_MEMORY_UTIL=0.90"
set "MAX_MODEL_LEN=16384"
set "MAX_NUM_SEQS=4"
set "MAX_NUM_BATCHED_TOKENS=2048"
set "TENSOR_PARALLEL=1"
set "CPU_OFFLOAD_GB=0"

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

echo [vLLM] Rimozione container precedente (se presente)...
docker rm -f "%CONTAINER_NAME%" >nul 2>&1

set "API_KEY_ARG="
if not "%VLLM_API_KEY%"=="" set "API_KEY_ARG=-e VLLM_API_KEY=%VLLM_API_KEY%"

echo [vLLM] Avvio container "%CONTAINER_NAME%"...
echo [vLLM] Model: %MODEL_PATH%
echo [vLLM] Tokenizer: %TOKENIZER_PATH%
echo [vLLM] Served model: %SERVED_MODEL_NAME%
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
  --tokenizer "%TOKENIZER_PATH%" ^
  --host 0.0.0.0 ^
  --port %CONTAINER_PORT% ^
  --served-model-name "%SERVED_MODEL_NAME%" ^
  --gpu-memory-utilization %GPU_MEMORY_UTIL% ^
  --max-model-len %MAX_MODEL_LEN% ^
  --max-num-seqs %MAX_NUM_SEQS% ^
  --max-num-batched-tokens %MAX_NUM_BATCHED_TOKENS% ^
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
