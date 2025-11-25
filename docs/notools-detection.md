# Automatic NoTools Detection

## Overview
The system automatically detects when a model does not support tool calling and marks it with `NoTools = true` in the database.

## How It Works

### 1. Error Detection
When Ollama (or any provider) returns an error like:
```
Model request failed with status BadRequest: {"error":"registry.ollama.ai/library/deepseek-r1:14b does not support tools"}
```

The system detects the `"does not support tools"` phrase in the error message.

### 2. Exception Flow
- `LangChainChatBridge.cs` throws a `ModelNoToolsSupportException` when the error is detected
- This custom exception carries the model name and error details
- Test service catches this specific exception and updates the model

### 3. Database Update
When caught, the system:
1. Sets `modelInfo.NoTools = true`
2. Calls `_database.UpsertModel(modelInfo)` to persist the change
3. Logs a notification: `"[model] Model does not support tools - marked NoTools=true"`
4. Marks the test step as failed with a clear error message

### 4. Affected Test Types
The detection works in:
- Regular function-calling tests (`ExecuteTestAsync`)
- Writer tests (`ExecuteWriterTestAsync`)
- Any test that uses `LangChainChatBridge` for model communication

## Benefits
- **Automatic**: No manual database updates needed
- **Immediate**: Detected on first failed tool call attempt
- **Visible**: Progress notifications show when a model is marked
- **Persistent**: Model configuration updated in database
- **Fallback**: Generic exception handler also checks error message text

## Related Files
- `Services/ModelNoToolsSupportException.cs` - Custom exception type
- `Services/LangChainChatBridge.cs` - Detection and exception throwing
- `Services/LangChainTestService.cs` - Exception handling and database update
- `Models/ModelInfo.cs` - `NoTools` property
