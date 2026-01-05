param(
  [Parameter(Mandatory=$true)][string]$Folder
)

$ErrorActionPreference = 'Stop'

function Get-WavDurationMs([string]$path) {
  $fs = [System.IO.File]::OpenRead($path)
  try {
    $br = New-Object System.IO.BinaryReader($fs)

    $riff = -join ($br.ReadChars(4))
    if ($riff -ne 'RIFF') { return 0 }
    [void]$br.ReadUInt32()
    $wave = -join ($br.ReadChars(4))
    if ($wave -ne 'WAVE') { return 0 }

    $byteRate = 0
    $dataSize = 0

    while ($fs.Position -lt $fs.Length) {
      $chunkId = -join ($br.ReadChars(4))
      $chunkSize = [int]$br.ReadUInt32()

      if ($chunkId -eq 'fmt ') {
        [void]$br.ReadUInt16() # audioFormat
        [void]$br.ReadUInt16() # channels
        [void]$br.ReadUInt32() # sampleRate
        $byteRate = $br.ReadUInt32()
        [void]$br.ReadUInt16() # blockAlign
        [void]$br.ReadUInt16() # bitsPerSample
        $remaining = $chunkSize - 16
        if ($remaining -gt 0) { [void]$br.ReadBytes($remaining) }
      }
      elseif ($chunkId -eq 'data') {
        $dataSize = $chunkSize
        [void]$br.ReadBytes($chunkSize)
      }
      else {
        [void]$br.ReadBytes($chunkSize)
      }

      if ($byteRate -gt 0 -and $dataSize -gt 0) { break }
    }

    if ($byteRate -le 0 -or $dataSize -le 0) { return 0 }
    $seconds = [double]$dataSize / [double]$byteRate
    return [int][Math]::Round($seconds * 1000.0)
  }
  finally {
    $fs.Dispose()
  }
}

function Normalize-CharacterName([string]$raw) {
  if ([string]::IsNullOrWhiteSpace($raw)) { return 'Narratore' }

  $name = $raw.Replace('_', ' ').Trim()
  if ($name -match '^(.*)\s(\d+)$') {
    $name = ($Matches[1].TrimEnd() + '-' + $Matches[2])
  }

  if ($name.Length -gt 0) {
    $name = $name.Substring(0,1).ToUpperInvariant() + $name.Substring(1)
  }

  return $name
}

$schemaPath = Join-Path $Folder 'tts_schema.json'

$wavFiles = Get-ChildItem $Folder -File -Filter '*.wav' |
  Where-Object { $_.Name -match '^\d{3}_.+\.wav$' } |
  Sort-Object Name

if ($wavFiles.Count -eq 0) {
  throw "No numbered TTS wav files found in $Folder"
}

$timeline = @()
$charactersSet = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)

$currentMs = 0
foreach ($f in $wavFiles) {
  $base = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
  $parts = $base.Split('_', 2)
  $rawChar = if ($parts.Length -ge 2) { $parts[1] } else { 'Narratore' }
  $character = Normalize-CharacterName $rawChar
  [void]$charactersSet.Add($character)

  $durationMs = Get-WavDurationMs $f.FullName
  if ($durationMs -le 0) { $durationMs = 2000 }

  $entry = @{
    character  = $character
    text       = ''
    emotion    = 'neutral'
    fileName   = $f.Name
    startMs    = $currentMs
    durationMs = $durationMs
    endMs      = $currentMs + $durationMs
  }

  $timeline += $entry
  $currentMs += $durationMs
}

$characters = @()
foreach ($name in ($charactersSet | Sort-Object)) {
  $characters += @{
    name           = $name
    voice          = ''
    voiceId        = ''
    gender         = ''
    emotionDefault = 'neutral'
  }
}

$root = @{
  characters = $characters
  timeline   = $timeline
}

$out = $root | ConvertTo-Json -Depth 30
[System.IO.File]::WriteAllText($schemaPath, $out, [System.Text.UTF8Encoding]::new($false))

Write-Host "Rebuilt schema from WAV files: $schemaPath"
