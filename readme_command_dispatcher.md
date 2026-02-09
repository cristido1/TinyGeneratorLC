# Command Dispatcher e Comandi

Il `CommandDispatcher` e' la coda di esecuzione background usata da UI/API per operazioni lunghe (generazione, valutazioni, tagging, TTS, serie, pipeline state-driven).

Riferimenti principali:
- Dispatcher: `Code/CommandDispatcher.cs`
- Contratti: `Code/Interfaces/ICommandDispatcher.cs`, `Code/Interfaces/ICommandEnqueuer.cs`
- Interfaccia comando: `Code/Interfaces/ICommand.cs`
- Comandi: `Code/Commands/*.cs`

## Regole attuali

- Il dispatcher accetta comandi tipizzati `ICommand`.
- Nei comandi si usa `ICommandEnqueuer` (non `ICommandDispatcher`) per accodare altri comandi.
- L'accodamento con `operationName + handler` e' ancora disponibile come compatibilita' tramite extension:
  - `Code/CommandDispatcherEnqueueExtensions.cs`
  - wrapper: `Code/Commands/DelegateCommand.cs`

## API di enqueue

Firma principale:

```csharp
CommandHandle Enqueue(
    ICommand command,
    string? runId = null,
    string? threadScope = null,
    IReadOnlyDictionary<string, string>? metadata = null,
    int? priority = null);
```

Note:
- `CommandName` e `Priority` arrivano dal comando (`ICommand`).
- Se `priority` e' passato esplicitamente, sovrascrive quella del comando.

## Scheduling e stato

- Priorita': numero piu' basso = priorita' piu' alta.
- A parita' di priorita': FIFO.
- Parallelismo: `CommandDispatcher.MaxParallelCommands` (`appsettings`).
- Stato tracciato: `queued`, `running`, `completed`, `failed`, `cancelled`.
- Supporto `CancelCommand(runId)` e `WaitForCompletionAsync(runId)`.
- Evento completamento: `CommandCompleted`.

## Lifecycle ICommand

`ICommand` espone:
- `CommandName`
- `Priority`
- `event Progress`
- `Start(CommandContext)`
- `End(CommandContext, CommandResult)`

L'adapter interno permette compatibilita' con comandi che implementano `ExecuteAsync(...)`.

## Metadata consigliata

Campi comuni utili in UI/log:
- `storyId`
- `operation`
- `trigger`
- `agentName`
- `agentRole`
- `modelName`
- `stepCurrent`
- `stepMax`

## Struttura cartelle correlata

- `Code/Commands/`: solo comandi.
- `Code/Services/`: servizi applicativi.
- `Code/Interfaces/`: solo interfacce.
- `Code/Options/`: configurazioni `*Options`.
