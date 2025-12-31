@echo off
REM llama_cpp_start.bat
REM Avvia llama.cpp (llama-server.exe) in modalita' OpenAI-compatible.
REM Nota: serve un modello .gguf. Imposta LLAMA_GGUF_PATH oppure passa il path come primo argomento.

SETLOCAL EnableExtensions

set "LLAMA_HOME=C:\llama.cpp.13.1"
set "LLAMA_EXE=%LLAMA_HOME%\llama-server.exe"

if not exist "%LLAMA_EXE%" (
  echo Errore: non trovo "%LLAMA_EXE%".
  echo Verifica che llama.cpp sia installato in %LLAMA_HOME%.
  pause
  exit /b 1
)

REM Modifica qui la porta se vuoi evitare conflitti con altri servizi.
set "LLAMA_HOST=127.0.0.1"
set "LLAMA_PORT=11436"

REM Context e parallelismi (tieni semplice, poi ottimizziamo)
set "LLAMA_CTX=16384"
set "LLAMA_THREADS=0"

REM GPU: 'auto' prova a usare la VRAM disponibile.
set "LLAMA_N_GPU_LAYERS=auto"

REM Modello: 1) primo argomento; 2) variabile env LLAMA_GGUF_PATH
set "MODEL_PATH=%~1"
if "%MODEL_PATH%"=="" set "MODEL_PATH=%LLAMA_GGUF_PATH%"

if "%MODEL_PATH%"=="" (
  echo Errore: modello .gguf non specificato.
  echo - Passa il path come argomento: llama_cpp_start.bat C:\models\mymodel.gguf
  echo - Oppure imposta la variabile: setx LLAMA_GGUF_PATH C:\models\mymodel.gguf
  pause
  exit /b 1
)

if not exist "%MODEL_PATH%" (
  echo Errore: modello non trovato: "%MODEL_PATH%"
  pause
  exit /b 1
)

echo Avvio llama.cpp server
echo - exe:   %LLAMA_EXE%
echo - model: %MODEL_PATH%
echo - bind:  %LLAMA_HOST%:%LLAMA_PORT%

pushd "%LLAMA_HOME%"

REM Importante: avviamo dalla cartella che contiene le DLL (ggml-cuda.dll, cublas*, ecc.)
REM per evitare errori di caricamento.
"%LLAMA_EXE%" ^
  --host "%LLAMA_HOST%" ^
  --port %LLAMA_PORT% ^
  --model "%MODEL_PATH%" ^
  --ctx-size %LLAMA_CTX% ^
  --threads %LLAMA_THREADS% ^
  --n-gpu-layers %LLAMA_N_GPU_LAYERS%

popd
ENDLOCAL
