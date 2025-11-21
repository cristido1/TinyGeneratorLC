# KernelFactory → LangChain Equivalence Analysis

## Semantic Kernel KernelFactory - Cosa Fa

### Responsabilità Principali

1. **Creazione Kernel** - Crea SK Kernel per un modello
   - Seleziona provider (OpenAI, Azure, Ollama fallback)
   - Configura endpoint e credenziali
   - Registra chat completion service

2. **Plugin Registration** - Aggiunge skills al kernel
   - TextPlugin, MathSkill, TimeSkill
   - FileSystemSkill, HttpSkill, MemorySkill
   - AudioCraftSkill, AudioEvaluatorSkill, TtsApiSkill
   - StoryWriterSkill, StoryEvaluatorSkill
   - Supporta plugin selettivi via `allowedPlugins` parameter

3. **Caching per Agent** - Mantiene kernel per agenti
   - `_agentKernels` dictionary: agentId → KernelWithPlugins
   - Metodi: `EnsureKernelForAgent()`, `GetKernelForAgent()`

4. **Configurazione da Database**
   - Legge ModelInfo (endpoint, provider, context window)
   - Usa agentId per trovare modello associato

5. **Plugin Singleton** (a livello factory)
   - Crea istanze globali: TextPlugin, MathSkill, etc.
   - Riutilizzate per più kernel

### Flusso Logico

```
CreateKernel(model, allowedPlugins, agentId)
    ↓
Determina provider (OpenAI/Azure/Ollama)
    ↓
Legge credenziali da config/env/DB
    ↓
Crea Kernel.CreateBuilder()
    ↓
AddChatCompletion (OpenAI/Azure endpoint)
    ↓
Registra plugins (filtrati da allowedPlugins)
    ↓
Ritorna KernelWithPlugins wrapper
```

---

## LangChain C# - Equivalenti Concettuali

### 1. Creazione "Kernel" (Chat Completion)

**SK**: `Kernel.CreateBuilder().AddOpenAIChatCompletion()`

**LangChain**: 
```csharp
var chatModel = new ChatOpenAI(
    modelName: "gpt-3.5-turbo",
    openAIApiKey: apiKey,
    baseUrl: endpoint  // For Ollama compatibility
);
```

**Differenze**:
- LangChain NON ha "kernel" centralizzato
- Crea model direttamente
- Molto più semplice e modulare
- Per Ollama: configura `baseUrl` come `http://localhost:11434/v1`

### 2. Plugin Registration (Tools)

**SK**: 
```csharp
kernel.Plugins.AddFromObject(textPlugin, "text");
kernel.Plugins.AddFromObject(mathSkill, "math");
```

**LangChain**:
```csharp
var tools = new List<Tool>
{
    new TextTool(),
    new MathTool(),
    ...
};
```

**Approccio LangChain in TinyGenerator**:
- Usa `BaseLangChainTool` per tool uniformi
- Tools registrati via `HybridLangChainOrchestrator.RegisterTool()`
- NO auto-invocation: esplicito nel ReAct loop

### 3. Caching Kernel per Agent

**SK**: `ConcurrentDictionary<int, KernelWithPlugins>`

**LangChain**: 
```csharp
var orchestratorCache = new Dictionary<int, HybridLangChainOrchestrator>();

orchestratorCache[agentId] = _toolFactory.CreateFullOrchestrator()
    .WithModel(model)
    .WithEndpoint(endpoint);
```

### 4. Configurazione Dinamica

**SK KernelFactory**:
- Legge da IConfiguration
- Risolve credenziali da config/env
- Determina provider automaticamente

**LangChain Equivalente - Nuova Classe**:
```csharp
public class LangChainKernelFactory
{
    private readonly IConfiguration _config;
    private readonly DatabaseService _database;
    private readonly Dictionary<int, HybridLangChainOrchestrator> _agentOrchestrators;
    
    public HybridLangChainOrchestrator CreateOrchestrator(
        string modelId, 
        IEnumerable<string>? allowedPlugins = null, 
        int? agentId = null)
    {
        // 1. Resolve model config from DB
        var modelInfo = _database.GetModelInfo(modelId);
        
        // 2. Resolve credentials from config/env
        var (endpoint, apiKey) = ResolveCredentials(modelInfo);
        
        // 3. Create ChatOpenAI model
        var chatModel = CreateChatModel(modelId, endpoint, apiKey);
        
        // 4. Create orchestrator with tools
        var orchestrator = _toolFactory.CreateFullOrchestrator();
        orchestrator.SetChatBridge(
            new LangChainChatBridge(endpoint, modelId, chatModel));
        
        // 5. Filter tools if needed
        if (allowedPlugins?.Any() == true)
        {
            FilterTools(orchestrator, allowedPlugins);
        }
        
        // 6. Cache by agentId
        if (agentId.HasValue)
        {
            _agentOrchestrators[agentId.Value] = orchestrator;
        }
        
        return orchestrator;
    }
}
```

---

## Confronto Dettagliato

| Aspetto | Semantic Kernel | LangChain C# | Note |
|---------|-----------------|-------------|------|
| **Creazione modello** | `Kernel.CreateBuilder().Add*()` | `new ChatOpenAI()` | LangChain è diretto |
| **Plugin registration** | `kernel.Plugins.AddFromObject()` | `orchestrator.RegisterTool()` | LangChain usa lista stringa |
| **Auto-invocation** | `ToolCallBehavior.AutoInvokeKernelFunctions` | ReAct loop esplicito | LangChain dà controllo esplicito |
| **Tool schema** | Automatico da reflection | Manual con `GetSchema()` | LangChain richiede schema JSON |
| **Caching kernel** | Per agentId | Per agentId + modello | Simile, più granulare |
| **Configurazione** | Centralizzata in IConfiguration | Distribuita (config + DB) | LangChain è più modulare |
| **Credenziali** | From config/env/DB | Esplicite nel constructor | LangChain è esplicito |
| **Connettori** | Tanti built-in (OpenAI, Azure, etc.) | Basati su SDK OpenAI | LangChain meno built-in |

---

## Raccomandazione per Migrazione

### Fase 1: Wrapper Bridge
Crea `LangChainKernelFactory` che:
- Mantiene stessa interfaccia di SK KernelFactory
- Ritorna `HybridLangChainOrchestrator` al posto di `Kernel`
- Usa `LangChainToolFactory` internamente

```csharp
public class LangChainKernelFactory : IKernelFactory
{
    public IKernelFactory CreateKernel(string model, IEnumerable<string> plugins, int? agentId)
    {
        // Ritorna orchestrator LangChain
        // Ma espone stessa interfaccia SK
    }
}
```

### Fase 2: Tool Filtering
Il meccanismo `allowedPlugins` rimane uguale:
```csharp
if (allowedPlugins?.Any() == true)
{
    orchestrator.FilterTools(allowedPlugins);
}
```

### Fase 3: Agent Caching
Mantieni `_agentOrchestrators` cache:
```csharp
_agentOrchestrators[agentId] = orchestrator;
```

---

## Prossimi Passi

1. ✅ Verificare `LangChainToolFactory` esistente
2. ✅ Verificare `LangChainChatBridge` esistente  
3. ⏳ Creare wrapper `LangChainKernelFactory`
4. ⏳ Testare tool filtering e agent caching
5. ⏳ Integrare in Program.cs come DI factory

---

## Concetti Equivalenti - Quick Reference

| SK | LangChain |
|----|-----------|
| `Kernel` | `HybridLangChainOrchestrator` |
| `IKernelFactory` | `LangChainToolFactory` |
| `Plugin` | `Tool` (BaseLangChainTool) |
| `Kernel.Plugins.AddFromObject()` | `orchestrator.RegisterTool()` |
| `ToolCallBehavior.AutoInvoke` | `ReActLoopOrchestrator` |
| `ChatHistory` | `List<ConversationMessage>` |
| `IChatCompletionService` | `ChatOpenAI` / `LangChainChatBridge` |
| `CreateKernel()` | `CreateOrchestrator()` |
| `GetKernelForAgent()` | `GetOrchestratorForAgent()` |

---

**Conclusione**: LangChain NON ha un concetto di "Kernel" centralizzato. Invece:
- **Model** = `ChatOpenAI` (dal SDK OpenAI)
- **Tools** = Lista di `Tool` implementations
- **Orchestration** = `HybridLangChainOrchestrator` + `ReActLoopOrchestrator`

È più modulare e semplice di SK, ma richiede più controllo manuale.
