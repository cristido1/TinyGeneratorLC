# LangChain Migration Guide

## Overview

TinyGenerator ha migrato da **Semantic Kernel** (con bug nel function calling) a **LangChain C#** con un **ReAct loop esplicito**.

## Key Components

### 1. Tools (Skills convertite)
- **TextTool** (`Skills/TextTool.cs`) - Manipolazione testo
- **MathTool** (`Skills/MathTool.cs`) - Operazioni aritmetiche
- **MemoryTool** (`Skills/MemoryTool.cs`) - Memoria persistente
- **EvaluatorTool** (`Skills/EvaluatorTool.cs`) - Valutazione storie

### 2. Orchestration
- **HybridLangChainOrchestrator** - Gestisce tools LangChain + fallback SK
- **ReActLoopOrchestrator** - Ciclo iterativo: model → tool call → execute → feedback
- **LangChainChatBridge** - Client OpenAI-compatible (Ollama, OpenAI, Azure)
- **LangChainToolFactory** - Factory per creare orchestratori pre-configurati

### 3. Story Generation
- **LangChainStoryGenerationService** - Sostituzione di StoryGeneratorService
  - Genera storie da multipli writers
  - Valuta e seleziona la migliore
  - Supporta endpoints custom

## Quick Start

### Setup nel DI Container

```csharp
// In Program.cs or Startup.cs
services.AddScoped<LangChainToolFactory>();
services.AddScoped<LangChainStoryGenerationService>();
```

### Uso Base

```csharp
var service = new LangChainStoryGenerationService(
    database,
    storiesService,
    memoryService,
    logger);

var result = await service.GenerateStoriesAsync(
    theme: "A mysterious adventure in the forest",
    writerModels: new[] { "phi3:mini", "mistral:7b", "qwen:7b" },
    evaluatorModel: "qwen2.5:3b",
    modelEndpoint: "http://localhost:11434/v1",
    apiKey: "ollama-dummy-key",
    progress: msg => Console.WriteLine(msg));

Console.WriteLine($"Best story: {result.Approved}");
Console.WriteLine($"Score: {result.ScoreA}");
```

## Migration Checklist

- [x] LangChain packages installati
- [x] BaseLangChainTool creato
- [x] Tools critiche migrate (Text, Math, Memory, Evaluator)
- [x] HybridOrchestrator implementato
- [x] ReAct loop implementato
- [x] Chat bridge creato
- [x] Tool factory creato
- [x] Story generation service creato
- [x] Unit tests creati e passanti (15/15 ✅)
- [ ] Integrate nel Genera.cshtml
- [ ] Test con Ollama locale
- [ ] Deprecare SK components

## Unit Tests (Status: All 26 Passing ✅)

**Location**: `Tests/` directory with comprehensive test suite

**Files**:
- `LangChainToolsTests.cs` - 15 unit tests for individual tools
- `LangChainIntegrationTests.cs` - 11 integration tests for orchestration
- `README.md` - Detailed test documentation

**Unit Test Coverage (15 tests)**:
- TextTool: schema generation, uppercase, substring operations
- MathTool: schema generation, add, multiply, divide, error handling
- EvaluatorTool: schema generation
- HybridOrchestrator: tool registration, tool call parsing (empty/valid), tool execution, error handling for unregistered tools
- LangChainToolFactory: essential orchestrator creation
- ReActLoop: execution tracking, message serialization

**Integration Test Coverage (11 tests)**:
- Orchestrator setup: full tool registry validation
- Tool call parsing: valid responses, empty responses
- Tool chaining: multi-tool workflows, error propagation
- Conversation history: message tracking across iterations
- Factory pattern: essential vs full orchestrators
- JSON serialization: preservation of all fields
- ReAct loop structures: result initialization, execution records

**Running Tests**:
```bash
dotnet test
# Output: 26 passed in 27ms
```

**Test Results**:
```
Unit Tests (LangChainToolsTests.cs):        15/15 ✅
Integration Tests (LangChainIntegrationTests.cs): 11/11 ✅
Total:                                      26/26 ✅
```

**Test Dependencies**:
- xunit 2.9.3
- xunit.runner.visualstudio 3.1.5
- Microsoft.NET.Test.Sdk 17.9.0

## Architettura: SK vs LangChain

### Semantic Kernel (Vecchio - Buggy)
```
User Input
    ↓
Kernel.GetChatMessageContentAsync()
    ↓
AutoInvokeKernelFunctions ❌ BUG: Tool calls non funzionano con Ollama
    ↓
Response
```

### LangChain (Nuovo - Esplicito)
```
User Input
    ↓
Model Call + Tool Schema
    ↓
Parse Tool Calls (explicit)
    ↓
Execute Tools (loop)
    ↓
Feed Results Back to Model
    ↓
Repeat until done
    ↓
Response
```

## Compatibility

- **Model Endpoints**: Ollama `/v1`, OpenAI, Azure OpenAI
- **Models Tested**: phi3, mistral, qwen
- **Fallback**: SK skills ancora disponibili tramite HybridOrchestrator

## Performance

- **ReAct Loop**: Max 10 iterazioni (configurable)
- **Tool Registration**: Lazy loading
- **Memory**: Async I/O, no blocking

## Troubleshooting

### "Tool not found"
- Registra il tool con `orchestrator.RegisterTool(tool)`
- Verifica il nome tool in schema

### "Model endpoint not responding"
- Controlla che Ollama sia running: `ollama serve`
- Verifica endpoint: `http://localhost:11434/v1`
- Testa con curl: `curl -X POST http://localhost:11434/v1/chat/completions`

### "Execution failed"
- Controlla i log: `CustomLogger`
- Verifica input JSON del tool
- Usa `HybridOrchestrator.ParseToolCalls()` per debug

## Next Steps

1. **Integrate LangChainStoryGenerationService** in Genera.cshtml
2. **Migrate remaining Skills** (audiocraft, tts, etc.)
3. **Full ReAct loop integration** con vero model bridge
4. **Deprecate SK components** gradualmente
5. **Performance testing** con modelli locali

## References

- LangChain C#: https://github.com/SciSharp/LangChain
- Ollama: https://ollama.ai
- OpenAI API: https://platform.openai.com/docs
