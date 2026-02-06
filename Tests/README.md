# TinyGenerator LangChain Tests

Comprehensive test suite for the LangChain C# migration from Semantic Kernel.

Nota di policy (runtime): l'applicazione usa output TAG-only e ha `ToolCalling:Enabled=false` di default. Questa suite testa anche l'infrastruttura tool-calling **legacy/retrocompatibilita'** (schema, parsing, orchestrazione) usando mock deterministici.

## Test Files

### LangChainToolsTests.cs (15 tests)
**Unit tests for individual tool functionality and schema generation**

- **TextTool Tests** (3 tests)
  - `TextTool_GetSchema_ReturnsValidSchema` - Validates schema structure and content
  - `TextTool_ToUpper_ExecutesCorrectly` - Tests uppercase transformation
  - `TextTool_Substring_ExecutesCorrectly` - Tests substring extraction

- **MathTool Tests** (3 tests)
  - `MathTool_GetSchema_ReturnsValidSchema` - Validates math tool schema
  - `MathTool_Add_ExecutesCorrectly` - Tests addition operation
  - `MathTool_Multiply_ExecutesCorrectly` - Tests multiplication
  - `MathTool_Divide_WithZeroReturnError` - Tests error handling

- **EvaluatorTool Tests** (1 test)
  - `EvaluatorTool_GetSchema_ReturnsValidSchema` - Validates evaluator schema

- **HybridOrchestrator Tests** (4 tests)
  - `HybridOrchestrator_RegisterTool_StoresToolCorrectly` - Tool registration
  - `HybridOrchestrator_ParseToolCalls_ReturnsEmptyForNoToolCalls` - Parsing empty responses
  - `HybridOrchestrator_ParseToolCalls_ParsesValidToolCalls` - Parsing valid responses
  - `HybridOrchestrator_ExecuteToolAsync_CallsToolCorrectly` - Tool execution
  - `HybridOrchestrator_ExecuteToolAsync_ReturnsErrorForUnregisteredTool` - Error handling

- **LangChainToolFactory Tests** (1 test)
  - `LangChainToolFactory_CreateEssentialOrchestrator_RegistersTools` - Factory pattern

- **ReActLoop Tests** (2 tests)
  - `ReActLoop_ToolExecutionRecords_TracksExecutions` - Execution tracking
  - `ConversationMessage_Serialization_WorksCorrectly` - Message serialization

### LangChainIntegrationTests.cs (11 tests)
**Integration tests for end-to-end orchestration workflows**

- **Orchestrator Setup** (2 tests)
  - `FullOrchestrator_WithAllTools_IsReadyForExecution` - Validates complete tool registry
  - `ReActLoop_Creation_WithDefaultSettings` - Validates loop instantiation

- **Tool Call Parsing** (2 tests)
  - `ParseToolCalls_WithValidResponse_ExtractsCorrectly` - Parses model responses
  - `ParseToolCalls_WithNoToolCalls_ReturnsEmptyList` - Handles empty responses

- **Tool Chaining & Execution** (2 tests)
  - `ToolChaining_MultipleTools_WorksTogether` - Chain multiple tools
  - `ErrorPropagation_FailedTool_ReturnsErrorMessage` - Error handling

- **Conversation & History** (2 tests)
  - `ConversationHistory_MaintainedAcrossIterations` - Message history tracking
  - `ReActLoop_ToolExecutionRecord_TracksProperlyStructured` - Execution records

- **Factory & Serialization** (2 tests)
  - `FactoryPattern_EssentialVsFullOrchestrators` - Factory variations
  - `JsonSerialization_ConversationMessage_PreservesAllFields` - JSON handling
  - `ReActLoop_Result_InitializesCorrectly` - Result structure validation

## Running Tests

### Run all tests
```bash
dotnet test
```

### Run specific test file
```bash
dotnet test Tests/LangChainToolsTests.cs
dotnet test Tests/LangChainIntegrationTests.cs
```

### Run with verbosity
```bash
dotnet test --logger "console;verbosity=detailed"
```

### Run with code coverage
```bash
dotnet test /p:CollectCoverage=true
```

## Test Coverage Summary

| Component | Tests | Status |
|-----------|-------|--------|
| TextTool | 3 | ✅ Passing |
| MathTool | 4 | ✅ Passing |
| EvaluatorTool | 1 | ✅ Passing |
| HybridOrchestrator | 4 | ✅ Passing |
| ReActLoop | 2 | ✅ Passing |
| Factory | 1 | ✅ Passing |
| Integration Scenarios | 11 | ✅ Passing |
| **Total** | **26** | **✅ All Passing** |

## Dependencies

- **xunit** (2.9.3) - Testing framework
- **xunit.runner.visualstudio** (3.1.5) - Visual Studio runner
- **Microsoft.NET.Test.Sdk** (17.9.0) - Test infrastructure

## Architecture Validated

### ReAct Loop Pattern
Tests validate the explicit iteration pattern that fixes Semantic Kernel's broken function calling:

```
1. Model Call (with tool schema)
   ↓
2. Parse Tool Calls (explicit extraction)
   ↓
3. Execute Tools (with error handling)
   ↓
4. Feed Results Back (add to history)
   ↓
5. Repeat until done (max 10 iterations)
```

### Tool Schema Format
All tools use OpenAI-compatible function calling schema (legacy/tests):
```json
{
  "type": "function",
  "function": {
    "name": "tool_name",
    "description": "Tool description",
    "parameters": {
      "type": "object",
      "properties": { ... },
      "required": [ ... ]
    }
  }
}
```

### Mock Model Responses
Tests use JSON mock responses to simulate real model behavior without requiring:
- Live model endpoint (Ollama, OpenAI, Azure)
- Network connectivity
- Model inference time

Example mock response:
```json
{
  "content": "I'll help you calculate.",
  "tool_calls": [
    {
      "id": "call_001",
      "function": {
        "name": "math_operations",
        "arguments": "{\"operation\": \"add\", \"a\": 5, \"b\": 3}"
      }
    }
  ]
}
```

## Next Steps

1. **Integration with Ollama**: Test with real model endpoint at `http://localhost:11434/v1`
2. **Performance Testing**: Benchmark iteration counts and tool execution time
3. **Stress Testing**: Test with very large tool inputs and multiple concurrent requests
4. **Model Compatibility**: Validate with different model families (Qwen, Mistral, Deepseek, etc.)
5. **Deprecate SK**: Remove remaining Semantic Kernel components once LangChain is production-ready

## Notes

- All tests are isolated and don't require database initialization
- Mock classes simulate model responses for deterministic testing
- Tool execution is fully synchronous for test reliability
- Schema validation ensures compatibility with different model providers
