$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$dest = "data/storage_backup_before_narrative_story_blocks_$ts.db"
Copy-Item -Path 'data/storage.db' -Destination $dest -Force
Write-Host "BACKUP_DONE: $dest"
Get-ChildItem data -Filter storage_backup_before_narrative_story_blocks_*.db | Select-Object -Last 5 | ForEach-Object { Write-Host $_.FullName }
