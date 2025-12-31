param(
  [string]$CudaBin = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.4\bin",
  [string]$LlamaRoot = "C:\llama.cpp"
)

$ErrorActionPreference = 'Stop'

if (!(Test-Path -LiteralPath $CudaBin)) {
  throw "CudaBin not found: $CudaBin"
}
if (!(Test-Path -LiteralPath $LlamaRoot)) {
  throw "LlamaRoot not found: $LlamaRoot"
}

$ggmlCuda = Join-Path $LlamaRoot "ggml-cuda.dll"
if (!(Test-Path -LiteralPath $ggmlCuda)) {
  throw "ggml-cuda.dll not found in LlamaRoot: $ggmlCuda"
}

# Extract DLL-like strings from ggml-cuda.dll (cheap heuristic).
$bytes = [System.IO.File]::ReadAllBytes($ggmlCuda)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
$matches = [System.Text.RegularExpressions.Regex]::Matches($text, "[A-Za-z0-9_.-]+\\.dll")

$dllNames = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
foreach ($m in $matches) {
  $name = $m.Value
  if ($name.Length -lt 6 -or $name.Length -gt 64) { continue }
  if ($name -match "_12" -or $name -match "^(nvrtc|cublas|cudart|cufft|curand|cusolver|cusparse)") {
    [void]$dllNames.Add($name)
  }
}

# Always include the core ones.
[void]$dllNames.Add('cudart64_12.dll')
[void]$dllNames.Add('cublas64_12.dll')
[void]$dllNames.Add('cublasLt64_12.dll')

$toCopy = $dllNames | Sort-Object

Write-Host "CUDA bin:   $CudaBin"
Write-Host "Llama root: $LlamaRoot"
Write-Host "DLLs to copy (if found in CUDA bin):"
$toCopy | ForEach-Object { Write-Host "  - $_" }

$copied = 0
$missing = @()
foreach ($dll in $toCopy) {
  $src = Join-Path $CudaBin $dll
  $dst = Join-Path $LlamaRoot $dll
  if (Test-Path -LiteralPath $src) {
    Copy-Item -LiteralPath $src -Destination $dst -Force
    $copied++
  } else {
    $missing += $dll
  }
}

Write-Host "Copied $copied DLL(s) into $LlamaRoot."
if ($missing.Count -gt 0) {
  Write-Host "Not found in CUDA bin (may be OK):"
  $missing | ForEach-Object { Write-Host "  - $_" }
}

Write-Host "\nVerification (should show only C:\\llama.cpp paths first):"
cmd /c "where cudart64_12.dll & where cublas64_12.dll & where cublasLt64_12.dll" | Out-String | Write-Host

Write-Host "\nTest (list devices):"
& (Join-Path $LlamaRoot "llama-server.exe") --list-devices 2>&1 | Out-String | Write-Host
