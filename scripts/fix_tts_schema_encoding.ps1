param(
  [Parameter(Mandatory=$true)][string]$Path
)

$ErrorActionPreference = 'Stop'

function Clean-TtsText([string]$s) {
  if ([string]::IsNullOrWhiteSpace($s)) { return $s }

  # Normalize curly apostrophes to ASCII apostrophe
  $s = $s.Replace([char]0x2019, "'").Replace([char]0x2018, "'")

  # Remove quote-like characters that some TTS engines mis-handle
  $s = $s.Replace('"','')
  $s = $s.Replace([char]0x00AB,'').Replace([char]0x00BB,'') # « »
  $s = $s.Replace([char]0x201C,'').Replace([char]0x201D,'').Replace([char]0x201E,'').Replace([char]0x201F,'') # “ ” „ ‟

  # Normalize whitespace
  $s = [System.Text.RegularExpressions.Regex]::Replace($s, "\s+", " ").Trim()
  return $s
}

function Walk-And-Clean($node) {
  if ($null -eq $node) { return }

  # Hashtable / PSObject
  if ($node -is [System.Collections.IDictionary]) {
    foreach ($k in @($node.Keys)) {
      $v = $node[$k]
      if ($k -is [string] -and $k.Equals('text', [System.StringComparison]::OrdinalIgnoreCase) -and $v -is [string]) {
        $node[$k] = Clean-TtsText $v
      }
      else {
        Walk-And-Clean $v
      }
    }
    return
  }

  # Arrays
  if ($node -is [System.Collections.IEnumerable] -and -not ($node -is [string])) {
    foreach ($item in $node) {
      Walk-And-Clean $item
    }
    return
  }
}

if (-not (Test-Path $Path)) { throw "File not found: $Path" }

$json = Get-Content -Raw -Encoding UTF8 $Path
$obj = $json | ConvertFrom-Json

Walk-And-Clean $obj

$out = $obj | ConvertTo-Json -Depth 60
[System.IO.File]::WriteAllText($Path, $out, [System.Text.UTF8Encoding]::new($false))

Write-Host "Fixed encoding and quotes in: $Path"