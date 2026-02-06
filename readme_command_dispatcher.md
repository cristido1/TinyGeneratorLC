# CommandDispatcher e comandi disponibili

Il `CommandDispatcher` è la coda di esecuzione background usata da UI/API per avviare operazioni lunghe (generazione, valutazione, TTS, serie, ecc.).

Punti di riferimento:
- Dispatcher: `Services/CommandDispatcher.cs`, `Services/ICommandDispatcher.cs`
- Comandi: `Services/Commands/*.cs`
- Enqueue “operativi” su storie: `Services/StoriesService.cs`

Nota organizzativa: una parte dei comandi usati nella catena stati delle storie (es. `GenerateTtsSchemaCommand`, `AssignVoicesCommand`, `Delete*`) e' implementata come classi annidate in `StoriesService`, ma e' stata separata in file dedicati sotto `Services/Commands/` (tramite `partial class`) per mantenere un file principale piu' leggibile.

## Come funziona (in breve)
- `Enqueue(operationName, handler, runId?, threadScope?, metadata?, priority?)` accoda un work item.
- Scheduling:
  - Priorità: numeri più bassi = più alta priorità.
  - A parità di priorità: FIFO.
- Workers in background: numero = `CommandDispatcher.MaxParallelCommands`.
- Stato comandi:
  - snapshot in memoria (`queued`/`running`/`completed`/`failed`/`cancelled`).
  - broadcast SignalR (hub progress).

## Metadata consigliata
Molti consumer usano metadata per UI e logging:
- `storyId`, `operation`, `trigger`, `agentName`, `agentRole`, `modelName`, `stepCurrent`, `stepMax`.

## Lista comandi (classi in Services/Commands)
Queste sono le entry “principali” implementate come command class:

- `FullStoryPipelineCommand`: pipeline completa writer→evaluator→selezione.
- `GenerateNextChunkCommand`: generazione chunk/step successivo.
- `StartMultiStepStoryCommand` / `ExecuteMultiStepTaskCommand` / `ResumeMultiStepTaskCommand`: avvio/esecuzione/resume task multi-step.
- `StartStateDrivenStoryCommand`, `StateDrivenPipelineCommands`, `GenerateStateDrivenEpisodeToDurationCommand`: pipeline state-driven.
- `PlannedStoryCommand`: storia pianificata.
- `SummarizeStoryCommand` + `BatchSummarizeStoriesCommand`: riassunti (singolo o batch).
- `AddVoiceTagsToStoryCommand`, `AddAmbientTagsToStoryCommand`, `AddFxTagsToStoryCommand`, `AddMusicTagsToStoryCommand`: tagging story/audio.
- `EnqueueNextStatusCommand`: avanza catena stati.
- Serie: `GenerateNewSerieCommand`, `GenerateSeriesEpisodeCommand`, `GenerateSeriesEpisodeFromDbCommand`, `GenerateSeriesCharacterImagesCommand`.

## Esempi di operationName usati a runtime
Esempi reali (accodati da servizi) includono:
- `TransformStoryRawToTagged` (auto-format/tagging)
- `generate_tts_audio`
- `generate_tts_schema`, `normalize_characters`, `assign_voices` (pipeline mix)

(Per la lista completa: cerca `Enqueue(` nei `Services/*`.)
