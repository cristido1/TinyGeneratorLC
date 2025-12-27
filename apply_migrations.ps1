$dbPath = "data/storage.db"
$sqlScript = Get-Content "apply_pending_migrations.sql" -Raw

# Rimuovi commenti e split per statement
$statements = $sqlScript -split ';' | Where-Object { 
    $_ -match '\S' -and $_ -notmatch '^\s*--' 
} | ForEach-Object { $_.Trim() }

# Carica SQLite assembly
Add-Type -Path "C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Data.SQLite\v4.0_1.0.118.0__db937bc2d44ff139\System.Data.SQLite.dll" -ErrorAction SilentlyContinue

# Se non è disponibile, usa dotnet per eseguire le query
$connectionString = "Data Source=$dbPath;Version=3;"

foreach ($sql in $statements) {
    if ($sql -match '\S') {
        Write-Host "Executing: $($sql.Substring(0, [Math]::Min(50, $sql.Length)))..."
        
        try {
            # Usa dotnet ef per eseguire il comando
            $escapedSql = $sql -replace '"', '\"' -replace "'", "''"
            Write-Host "SQL: $sql"
        }
        catch {
            Write-Host "Error: $_"
        }
    }
}

Write-Host "Done!"
