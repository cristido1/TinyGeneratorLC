# Script to start AudioCraft API server
# This script starts the AudioCraft server in background and waits for it to be ready

param(
    [string]$PythonScript = ".\audiocraft_server.py",
    [int]$Port = 8003,
    [int]$TimeoutSeconds = 30
)

Write-Host "[AudioCraft] Starting AudioCraft API server..."

# Check if Python script exists
if (-not (Test-Path $PythonScript)) {
    Write-Error "[AudioCraft] Python script not found: $PythonScript"
    exit 1
}

# Kill existing AudioCraft processes
$existingProcs = Get-Process -Name python -ErrorAction SilentlyContinue | Where-Object {
    try {
        $cmdLine = (Get-WmiObject Win32_Process -Filter "ProcessId = $($_.Id)").CommandLine
        return $cmdLine -like "*audiocraft*"
    } catch {
        return $false
    }
}

if ($existingProcs) {
    Write-Host "[AudioCraft] Terminating $($existingProcs.Count) existing AudioCraft process(es)..."
    $existingProcs | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# Start AudioCraft server in background
Write-Host "[AudioCraft] Starting server: python $PythonScript"
$process = Start-Process -FilePath "python" -ArgumentList $PythonScript -NoNewWindow -PassThru -RedirectStandardOutput "audiocraft_stdout.log" -RedirectStandardError "audiocraft_stderr.log"

if (-not $process) {
    Write-Error "[AudioCraft] Failed to start process"
    exit 1
}

Write-Host "[AudioCraft] Process started with PID=$($process.Id)"

# Wait for service to be ready
$healthUrl = "http://localhost:$Port/health"
$ready = $false

for ($i = 1; $i -le $TimeoutSeconds; $i++) {
    Start-Sleep -Seconds 1
    
    try {
        $response = Invoke-WebRequest -Uri $healthUrl -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop
        if ($response.StatusCode -eq 200) {
            Write-Host "[AudioCraft] Service is ready after $i seconds"
            $ready = $true
            break
        }
    } catch {
        # Service not ready yet, keep waiting
    }
    
    if ($i -eq $TimeoutSeconds) {
        Write-Warning "[AudioCraft] Service did not respond after $TimeoutSeconds seconds"
    }
}

if ($ready) {
    Write-Host "[AudioCraft] AudioCraft API server is running on http://localhost:$Port"
    exit 0
} else {
    Write-Error "[AudioCraft] Failed to start AudioCraft service"
    exit 1
}
