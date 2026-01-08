$ErrorActionPreference = "Stop"
$db = "data\storage.db"
$query = "UPDATE tts_voices SET name = voice_id WHERE name = 'Claribel Dervia'; SELECT 'Fixed ' || changes() || ' records.' AS result;"

try {
    $result = & sqlite3 $db $query
    Write-Host $result
} catch {
    Write-Error "Error executing query: $_"
}
