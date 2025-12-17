@echo off
REM ollama_start.bat
REM Avvia il server Ollama su Windows impostando la variabile d'ambiente OLLAMA_CONTEXT_LENGTH

SETLOCAL
set "OLLAMA_CONTEXT_LENGTH=8192"

REM Controlla che ollama sia disponibile nel PATH
where ollama >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
  echo Errore: "ollama" non trovato nel PATH.
  echo Assicurati di aver installato Ollama e di avere il suo eseguibile nella variabile PATH.
  echo Visita: https://ollama.ai/ per istruzioni di installazione.
  pause
  ENDLOCAL
  exit /b 1
)

echo Avvio Ollama server con OLLAMA_CONTEXT_LENGTH=%OLLAMA_CONTEXT_LENGTH%
REM Se vuoi passare argomenti addizionali, puoi chiamare: ollama_start.bat --port 11434 --num-ctx 8192
if "%*"=="" (
  ollama serve
) else (
  ollama serve %*
)

ENDLOCAL

REM Alcune alternative (commentate):
REM ollama serve --port 11434 --num-ctx 2048
REM ollama serve --port 11435 --num-ctx 8192
