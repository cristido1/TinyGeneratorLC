param(
    [string]$RepoRoot = "."
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

function Assert-Rg {
    if (-not (Get-Command rg -ErrorAction SilentlyContinue)) {
        throw "ripgrep (rg) non trovato. Installalo o usa un controllo alternativo."
    }
}

Assert-Rg

$commandFiles = Get-ChildItem -Path "Code/Commands" -Filter "*.cs" -Recurse -File |
    Where-Object {
        $_.FullName -notmatch "\\bin\\" -and
        $_.FullName -notmatch "\\obj\\" -and
        $_.Name -notmatch "\.bak"
    }

$violations = New-Object System.Collections.Generic.List[string]

# Whitelist minima per casi legacy/transitori in cui il comando esegue ancora un check direttamente
# (es. normalizzazione post-call e fallback locale).
$whitelist = @{
    "Code/Commands/AddVoiceTagsToStoryCommand.cs" = @(
        "check.Execute(mappingText)"
    )
}

foreach ($file in $commandFiles) {
    $relative = $file.FullName.Replace((Get-Location).Path + "\", "").Replace("\", "/")
    $content = Get-Content -Path $file.FullName
    if ($null -eq $content) { continue }

    # 1) DeterministicValidator inline nei comandi (da spostare nel CallCenter)
    for ($i = 0; $i -lt $content.Count; $i++) {
        $line = $content[$i]
        if ($line -match "DeterministicValidator\s*=") {
            $violations.Add("[Inline-DeterministicValidator] ${relative}:$($i+1): $($line.Trim())")
        }
        if ($line -match "\bCheckRunner\b") {
            $violations.Add("[CheckRunner-In-Command] ${relative}:$($i+1): $($line.Trim())")
        }
    }

    # 2) Esecuzione diretta di un check (new CheckXxx + variable.Execute(...))
    $checkVars = @{}
    for ($i = 0; $i -lt $content.Count; $i++) {
        $line = $content[$i]
        if ($line -match "var\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+Check[A-Za-z0-9_]+") {
            $varName = $Matches[1]
            $checkVars[$varName] = $i + 1
        }
    }

    for ($i = 0; $i -lt $content.Count; $i++) {
        $line = $content[$i]
        foreach ($varName in $checkVars.Keys) {
            if ($line -match "\b$([regex]::Escape($varName))\.Execute\s*\(") {
                $isWhitelisted = $false
                if ($whitelist.ContainsKey($relative)) {
                    foreach ($allowedSnippet in $whitelist[$relative]) {
                        if ($line.Contains($allowedSnippet)) {
                            $isWhitelisted = $true
                            break
                        }
                    }
                }

                if (-not $isWhitelisted) {
                    $declLine = $checkVars[$varName]
                    $violations.Add("[Direct-Check-Execute] ${relative}:$($i+1): $($line.Trim()) (declared at line $declLine)")
                }
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "VIOLAZIONI VALIDAZIONI DETERMINISTICHE NEI COMANDI:" -ForegroundColor Red
    $violations | Sort-Object | ForEach-Object {
        Write-Host " - $_"
    }
    Write-Host ""
    Write-Host "Usa il CallCenter (CallOptions.DeterministicChecks) oppure aggiungi whitelist temporanea motivata." -ForegroundColor Yellow
    exit 1
}

Write-Host "OK: nessuna validazione deterministica diretta non autorizzata trovata nei comandi." -ForegroundColor Green
exit 0
