# Comando in Esecuzione - Visualizzazione ID Storia

## Modifica Implementata

Il popup "Comandi in esecuzione / coda" ora mostra l'ID della storia quando il comando è relativo a una storia specifica.

## File Modificati

- **wwwroot/js/command-panel.js**: Aggiunto badge con ID storia nella visualizzazione del comando

## Come Funziona

Il sistema legge i metadata del comando (che già includono `storyId` per operazioni relative alle storie) e mostra un badge con icona 📖 seguito dall'ID.

### Prima

```
⚙️ Comandi in esecuzione / coda                           [1]  🟢

▶️ GenerateTTS                              Running Step 2/5
👤 TTS Generator • 🧠 tts-1
```

### Dopo

```
⚙️ Comandi in esecuzione / coda                           [1]  🟢

▶️ GenerateTTS 📖 123                       Running Step 2/5
👤 TTS Generator • 🧠 tts-1
```

## Operazioni che Mostrano Story ID

Tutte le operazioni che passano `storyId` nei metadata lo mostreranno automaticamente:

- ✅ **GenerateTTS** - Generazione tracce TTS
- ✅ **GenerateTTSJson** - Generazione schema TTS
- ✅ **GenerateMusic** - Generazione musica di sottofondo
- ✅ **GenerateEffects** - Generazione effetti sonori
- ✅ **GenerateAmbient** - Generazione rumori ambientali
- ✅ **GenerateMixedAudio** - Generazione mix finale
- ✅ **SummarizeStory** - Generazione riassunto storia
- ✅ **EvaluateStory** - Valutazione storia
- ✅ Qualsiasi altro comando che includa storyId nei metadata

## Dettagli Tecnici

### Estrazione dei Metadata

```javascript
// Extract storyId from metadata if present
const metadata = c.metadata || c.Metadata || {};
const storyId = metadata.storyId || metadata.StoryId;
```

### Rendering del Badge

```javascript
const storyIdBadge = storyId ? ` <span style="
    background: rgba(0,0,0,0.1);
    padding: 1px 6px;
    border-radius: 3px;
    font-size: 10px;
    font-weight: normal;
" title="ID Storia">📖 ${storyId}</span>` : '';
```

Il badge viene aggiunto accanto al nome dell'operazione:

```javascript
<strong>${statusIcon} ${op}${storyIdBadge}</strong>
```

## Esempio Completo

Quando si generano TTS per la storia ID 456, il popup mostrerà:

```
⚙️ Comandi in esecuzione / coda                           [3]  🟢

▶️ GenerateTTS 📖 456                       Running Step 3/12
👤 TTS Generator • 🧠 tts-1

⏳ GenerateMusic 📖 456                     Queued
👤 Music Generator • 🧠 musicgen

✅ SummarizeStory 📖 456                    Completed
👤 Story Summarizer • 🧠 qwen2.5
```

## Test

Per testare la funzionalità:

1. Avvia l'applicazione: `dotnet run`
2. Vai a una pagina con tabella storie (es. `/Stories`)
3. Avvia un'operazione su una storia (es. genera TTS)
4. Osserva il popup in basso a destra - dovrebbe mostrare "📖 [ID]" accanto al nome del comando

## Note

- Il badge appare solo se il comando ha `storyId` nei metadata
- Il badge è semi-trasparente per non distrarre
- Tooltip "ID Storia" appare al passaggio del mouse
- Funziona con SignalR in real-time, nessun refresh necessario
