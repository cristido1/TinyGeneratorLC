# TinyGenerator

> **NOTA IMPORTANTE PER GLI SVILUPPATORI**
>
> Questo progetto è stato migrato da Microsoft Semantic Kernel a LangChain.NET. Tutte le nuove funzionalità devono utilizzare LangChain per l'orchestrazione degli agenti e l'invocazione delle funzioni. Il codice legacy di Semantic Kernel è mantenuto solo per compatibilità durante la transizione.

Un'applicazione web ASP.NET Core per la generazione di storie usando agenti AI basati su LangChain e modelli locali Ollama.

## Descrizione

TinyGenerator permette di creare storie complete attraverso un processo strutturato che utilizza agenti AI specializzati. Gli agenti seguono un approccio ReAct (Reasoning + Acting) per generare narrazioni coerenti utilizzando tool specializzati per manipolazione testo, memoria persistente, operazioni matematiche, e altro.

## Caratteristiche Principali

- **Generazione basata su LangChain**: Utilizza LangChain.NET per orchestrare agenti AI con tool calling
- **Agenti specializzati**: Agenti writer e evaluator basati su modelli locali Ollama
- **Tool System completo**: Oltre 10 tool specializzati (text, math, memory, time, filesystem, TTS, etc.)
- **ReAct Loop**: Implementazione completa del pattern Reasoning + Acting per tool usage
- **Controllo costi e token**: Monitoraggio dettagliato dei costi di generazione
- **Persistenza completa**: Database SQLite per storie, log, agenti, modelli, valutazioni
- **Interfaccia utente moderna**: Design responsive con Bootstrap 5 e DataTables
- **SignalR**: Aggiornamenti real-time del progresso durante la generazione
- **Logging configurabile**: Sistema di logging avanzato con filtri per request/response JSON
- **Test framework**: Suite completa per testare function calling e tool execution

## Requisiti

- **.NET 9.0**
- **SQLite**
- **Ollama** con modelli locali:
  - Modelli principali: `phi3:mini-128k`, `llama3.1:8b`, `qwen2.5:7b`
  - Modelli leggeri: `qwen2.5:3b`, `llama3.2:3b`

## Installazione

1. **Clona il repository:**
   ```bash
   git clone https://github.com/cristido1/TinyGeneratorLC.git
   cd TinyGeneratorLC
   ```

2. **Installa dipendenze:**
   ```bash
   dotnet restore
   ```

3. **Configura Ollama:**
   ```bash
   # Avvia Ollama
   ./scripts/start_ollama.sh

   # Scarica i modelli richiesti
   ollama pull phi3:mini-128k
   ollama pull llama3.1:8b
   ollama pull qwen2.5:7b
   ollama pull qwen2.5:3b
   ollama pull llama3.2:3b
   ```

4. **Avvia l'applicazione:**
   ```bash
   dotnet run
   ```

5. **Apri nel browser:** http://localhost:5077

## Utilizzo

### Generazione Storie
1. Nella pagina principale, inserisci un tema per la storia
2. Seleziona il tipo di generazione (Tutti/A/B/C agenti)
3. Monitora il progresso real-time via SignalR
4. La storia viene valutata automaticamente e salvata se supera il punteggio minimo

### Amministrazione
- **Agenti**: Gestisci agenti AI con configurazioni JSON personalizzate
- **Modelli**: Testa function calling sui modelli Ollama
- **Log**: Visualizza log dettagliati con filtri per categoria
- **Storie**: Gestisci storie generate e valutazioni
- **Test**: Esegui test di function calling e tool execution

## Architettura

### Servizi Core (`Services/`)
- **LangChain Services**:
  - `LangChainChatBridge`: Bridge per chiamate ai modelli con tool support
  - `ReActLoopOrchestrator`: Implementazione del pattern ReAct
  - `HybridLangChainOrchestrator`: Orchestrazione ibrida LangChain + legacy
  - `LangChainKernelFactory`: Factory per creazione orchestratori
  - `LangChainAgentService`: Gestione agenti con configurazioni DB

- **Tool System**:
  - `LangChainToolFactory`: Factory per creazione tool specializzati
  - Tool implementati in `Skills/`: TextTool, MathTool, MemoryTool, TimeTool, etc.

- **Persistence & Monitoring**:
  - `DatabaseService`: Gestione database SQLite
  - `CustomLogger`: Logging configurabile con batching
  - `ProgressService`: Aggiornamenti real-time via SignalR
  - `NotificationService`: Sistema notifiche

- **External Services**:
  - `OllamaManagementService`: Gestione modelli Ollama
  - `TtsService`: Text-to-Speech via API esterna
  - `CostController`: Monitoraggio costi token

### Tool Disponibili (`Skills/`)
- **BaseLangChainTool**: Classe base astratta per tool
- **TextTool**: Manipolazione testo (toupper, tolower, trim, substring, etc.)
- **MathTool**: Operazioni aritmetiche (add, subtract, multiply, divide)
- **MemoryTool**: Memoria persistente SQLite (remember, recall, forget)
- **TimeTool**: Operazioni temporali (now, today, adddays, addhours)
- **FileSystemTool**: Operazioni file system
- **HttpTool**: Chiamate HTTP
- **TtsApiTool**: Text-to-Speech via API
- **AudioCraftTool**: Generazione audio
- **StoryWriterTool**: Scrittura storie assistita
- **EvaluatorTool**: Valutazione storie (la legacy StoryEvaluatorTool è stata rimossa — usare EvaluatorTool)
- **TtsSchemaTool**: Gestione schemi TTS

### Interfaccia Utente (`Pages/`)
- **Pagine principali**:
  - `Index`: Homepage con generazione storie
  - `Genera`: Pagina generazione con progresso real-time
  - `Admin`: Dashboard amministrativa

- **Gestione**:
  - `Agents/`: CRUD agenti con configurazioni JSON
  - `Models/`: Test modelli e function calling
  - `Stories/`: Gestione storie generate
  - `Logs/`: Visualizzazione log filtrati

- **Test & Monitoraggio**:
  - `LangChainTest`: Test framework LangChain
  - `OllamaMonitor`: Monitoraggio modelli Ollama
  - `Chat`: Interfaccia chat diretta

### Modelli Dati (`Models/`)
- **Agent.cs**: Configurazione agenti
- **ModelInfo.cs**: Metadati modelli
- **StoryRecord.cs**: Storie generate
- **LogEntry.cs**: Record log
- **TestDefinition.cs**: Definizioni test

### API (`Controllers/`)
- **StoriesApiController**: API REST per storie
- **LogsApiController**: API per log
- **ChatController**: Controller chat

## Configurazione

### appsettings.json
```json
{
  "AppLog": {
    "LogRequestResponse": true,
    "OtherLogs": false
  },
  "CommandDispatcher": {
    "MaxParallelCommands": 2
  },
  "Ollama": {
    "endpoint": "http://localhost:11434"
  }
}
```

### Database
Il database SQLite (`data/storage.db`) contiene:
- `agents`: Configurazioni agenti
- `models`: Metadati modelli
- `stories`: Storie generate
- `tts_voices`: Catalogo delle voci TTS importate (usato dagli agenti e dai fallback automatici)
- `logs`: Record di log
- `calls`: Tracciamento function calls
- `Log_analysis`: Risultati delle analisi sui log collegati ai comandi eseguiti

## Workflow di Sviluppo

1. **Aggiunta Tool**: Implementare classe in `Skills/` ereditando `BaseLangChainTool`
2. **Registrazione**: Aggiungere al `LangChainToolFactory.CreateOrchestratorWithTools()`
3. **Test**: Creare test in `Tests/` per validare function calling
4. **UI**: Aggiungere pagine Razor se necessario

## Best Practices

- **LangChain First**: Tutte le nuove funzionalità usano LangChain
- **Tool Schema**: Mantenere description concise nei tool schemas
- **Logging**: Usare `ICustomLogger` per logging consistente
- **Database**: Usare Dapper per query, transazioni per operazioni critiche
- **UI**: Bootstrap 5 + DataTables per tabelle, SignalR per real-time
- **Error Handling**: Fail-fast per problemi di integrazione, graceful degradation per errori runtime

## Contributi

Contributi benvenuti! Seguire il workflow:
1. Fork del repository
2. Branch per feature (`feature/nome-feature`)
3. Pull request con descrizione dettagliata
4. Code review obbligatoria

## Licenza

MIT
