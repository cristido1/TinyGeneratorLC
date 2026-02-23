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

$violations = New-Object System.Collections.Generic.List[string]

function Add-ViolationsFromPattern {
    param(
        [string]$Pattern,
        [string]$Description
    )

    $matches = & rg -n --no-heading --glob "!**/*.bak*" --glob "!bin/**" --glob "!obj/**" $Pattern Code/Commands Code/Services 2>$null
    if (-not $matches) { return }

    foreach ($line in $matches) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        $path = ($line -split ":", 2)[0]
        $normalized = $path.Replace("\", "/")

        $allowed = $false

        if ($normalized -eq "Code/Services/CallCenter/CallCenter.cs") {
            $allowed = $true
        }
        elseif ($normalized -eq "Code/Services/CommandModelExecutionService.cs") {
            $allowed = $true
        }
        elseif ($normalized -eq "Code/Commands/CinoOptimizeStoryCommand.cs" -and $Pattern -eq "IAgentCallService") {
            # CINO mantiene l'iniezione per una capability tecnica (repetition/embedding), non per bypassare il CallCenter.
            $allowed = $true
        }

        if (-not $allowed) {
            $violations.Add("[$Description] $line")
        }
    }
}

# 1) Nessuno fuori dal CallCenter deve costruire richieste dirette al CommandModelExecutionService.
Add-ViolationsFromPattern "new CommandModelExecutionService\.Request" "Direct-CMES-Request"

# 2) Nessuno fuori dal CallCenter deve risolvere IAgentCallService dal DI per chiamate agenti.
Add-ViolationsFromPattern "GetService<IAgentCallService>\(" "Direct-IAgentCallService-DI"

# 3) Nessuno fuori dal CallCenter dovrebbe costruire manualmente un CallCenter.
Add-ViolationsFromPattern "new CallCenter\(" "Manual-CallCenter-Construction"

if ($violations.Count -gt 0) {
    Write-Host "VIOLAZIONI BOUNDARY CALLCENTER TROVATE:" -ForegroundColor Red
    $violations | Sort-Object | ForEach-Object { Write-Host " - $_" }
    exit 1
}

Write-Host "OK: nessun bypass del CallCenter trovato in Code/Commands e Code/Services." -ForegroundColor Green
exit 0
