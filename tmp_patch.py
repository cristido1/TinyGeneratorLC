from pathlib import Path
path = Path('Services/Commands/FullStoryPipelineCommand.cs')
text = path.read_text(encoding='utf-8')
marker = '// STEP 5: Genera TTS audio'
start = text.index(marker)
end = text.index('        private async Task LogAndNotifyAsync', start)
lines = [
