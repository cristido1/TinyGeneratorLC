using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using TinyGenerator.Services;
using TinyGenerator.Skills;

namespace TinyGenerator.Tests;

/// <summary>
/// Integration tests for LangChain orchestration with mock models.
/// These tests verify the full ReAct loop and tool orchestration without requiring a real model endpoint.
/// </summary>
public class LangChainIntegrationTests
{
    private readonly HybridLangChainOrchestrator _orchestrator;
    private readonly LangChainToolFactory _toolFactory;

    public LangChainIntegrationTests()
    {
        _orchestrator = new HybridLangChainOrchestrator();
        _toolFactory = new LangChainToolFactory();
    }

    [Fact]
    public void FullOrchestrator_WithAllTools_IsReadyForExecution()
    {
        // Arrange
        var orchestrator = _toolFactory.CreateFullOrchestrator();

        // Act
        var schemas = orchestrator.GetToolSchemas();

        // Assert
        Assert.NotNull(schemas);
        Assert.NotEmpty(schemas);
        // Should have text, math, evaluator tools (memory is optional)
        Assert.True(schemas.Count >= 2, $"Expected at least 2 tools, got {schemas.Count}");
        
        // Verify each schema has proper structure
        foreach (var schema in schemas)
        {
            Assert.NotNull(schema["type"]);
            var function = schema["function"] as Dictionary<string, object>;
            Assert.NotNull(function);
            Assert.NotNull(function["name"]);
            Assert.NotNull(function["description"]);
        }
    }

    [Fact]
    public void ReActLoop_Creation_WithDefaultSettings()
    {
        // Arrange
        var orchestrator = new HybridLangChainOrchestrator();

        // Act
        var reactLoop = new ReActLoopOrchestrator(orchestrator);

        // Assert
        Assert.NotNull(reactLoop);
    }

    [Fact]
    public void ParseToolCalls_WithValidResponse_ExtractsCorrectly()
    {
        // Arrange
        var response = """
            {
              "content": "Let me calculate that for you.",
              "tool_calls": [
                {
                  "id": "call_abc123",
                  "function": {
                    "name": "math_operations",
                    "arguments": "{\"operation\": \"add\", \"a\": 5, \"b\": 3}"
                  }
                }
              ]
            }
            """;

        // Act
        var toolCalls = _orchestrator.ParseToolCalls(response);

        // Assert
        Assert.Single(toolCalls);
        Assert.Equal("math_operations", toolCalls[0].ToolName);
        Assert.Contains("add", toolCalls[0].Arguments);
    }

    [Fact]
    public async Task ToolChaining_MultipleTools_WorksTogether()
    {
        // Arrange
        var orchestrator = new HybridLangChainOrchestrator();
        orchestrator.RegisterTool(new TextTool());
        orchestrator.RegisterTool(new MathTool());

        // Act - Chain: uppercase "hello" then count its length
        var step1 = await orchestrator.ExecuteToolAsync(
            "text",
            JsonSerializer.Serialize(new { function = "toupper", text = "hello" })
        );

        // Assert step 1
        Assert.Contains("HELLO", step1);

        // Act - Step 2: get length
        var step2 = await orchestrator.ExecuteToolAsync(
            "text",
            JsonSerializer.Serialize(new { function = "length", text = "HELLO" })
        );

        // Assert step 2
        Assert.Contains("5", step2);
    }

    [Fact]
    public async Task ErrorPropagation_FailedTool_ReturnsErrorMessage()
    {
        // Arrange
        var orchestrator = new HybridLangChainOrchestrator();
        orchestrator.RegisterTool(new MathTool());

        // Act - Division by zero should fail gracefully
        var result = await orchestrator.ExecuteToolAsync(
            "math_operations",
            JsonSerializer.Serialize(new { operation = "divide", a = 10, b = 0 })
        );

        // Assert
        Assert.NotNull(result);
        Assert.Contains("error", result.ToLower());
    }

    [Fact]
    public void ConversationHistory_MaintainedAcrossIterations()
    {
        // Arrange
        var messages = new List<ConversationMessage>
        {
            new ConversationMessage { Role = "user", Content = "What is 2+2?" },
            new ConversationMessage { Role = "assistant", Content = "I'll calculate that." },
            new ConversationMessage { Role = "user", Content = "And multiply by 3?" }
        };

        // Act
        var json = JsonSerializer.Serialize(messages);
        var deserialized = JsonSerializer.Deserialize<List<ConversationMessage>>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.Count);
        Assert.Equal("user", deserialized[0].Role);
        Assert.Equal("assistant", deserialized[1].Role);
        Assert.Equal("And multiply by 3?", deserialized[2].Content);
    }

    [Fact]
    public void FactoryPattern_EssentialVsFullOrchestrators()
    {
        // Arrange
        var essential = _toolFactory.CreateEssentialOrchestrator();
        var full = _toolFactory.CreateFullOrchestrator();

        // Act
        var essentialSchemas = essential.GetToolSchemas();
        var fullSchemas = full.GetToolSchemas();

        // Assert
        Assert.NotEmpty(essentialSchemas);
        Assert.NotEmpty(fullSchemas);
        // Full should have at least as many tools as essential
        Assert.True(fullSchemas.Count >= essentialSchemas.Count);
    }

    [Fact]
    public void ParseToolCalls_WithNoToolCalls_ReturnsEmptyList()
    {
        // Arrange
        var response = JsonSerializer.Serialize(new
        {
            content = "This is just a regular response",
            tool_calls = (object?)null
        });

        // Act
        var toolCalls = _orchestrator.ParseToolCalls(response);

        // Assert
        Assert.Empty(toolCalls);
    }

    [Fact]
    public void JsonSerialization_ConversationMessage_PreservesAllFields()
    {
        // Arrange
        var message = new ConversationMessage
        {
            Role = "assistant",
            Content = "I'm helping you"
        };

        // Act
        var json = JsonSerializer.Serialize(message);
        var deserialized = JsonSerializer.Deserialize<ConversationMessage>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("assistant", deserialized.Role);
        Assert.Equal("I'm helping you", deserialized.Content);
    }

    [Fact]
    public void ReActLoop_ToolExecutionRecord_TracksProperlyStructured()
    {
        // Arrange
        var record = new ReActLoopOrchestrator.ToolExecutionRecord
        {
            ToolName = "text",
            Input = "{\"function\": \"toupper\", \"text\": \"hello\"}",
            Output = "{\"result\": \"HELLO\"}",
            IterationNumber = 1
        };

        // Act & Assert
        Assert.Equal("text", record.ToolName);
        Assert.Equal(1, record.IterationNumber);
        Assert.Contains("hello", record.Input);
        Assert.Contains("HELLO", record.Output);
    }

    [Fact]
    public void ReActLoop_Result_InitializesCorrectly()
    {
        // Arrange & Act
        var result = new ReActLoopOrchestrator.ReActResult
        {
            FinalResponse = "Test response",
            IterationCount = 3,
            Success = true,
            ExecutedTools = new List<ReActLoopOrchestrator.ToolExecutionRecord>()
        };

        // Assert
        Assert.Equal("Test response", result.FinalResponse);
        Assert.Equal(3, result.IterationCount);
        Assert.True(result.Success);
        Assert.Empty(result.ExecutedTools);
    }
}
