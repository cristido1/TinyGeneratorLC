using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using TinyGenerator.Services;
using TinyGenerator.Skills;

namespace TinyGenerator.Tests
{
    /// <summary>
    /// Unit tests for LangChain Tools to verify they work correctly.
    /// These tests are independent of the model endpoint and test tool schema + execution.
    /// </summary>
    public class LangChainToolsTests
    {
        [Fact]
        public void TextTool_GetSchema_ReturnsValidSchema()
        {
            // Arrange
            var tool = new TextTool();

            // Act
            var schema = tool.GetSchema();

            // Assert
            Assert.NotNull(schema);
            Assert.Equal("function", schema["type"]);
            Assert.True(schema.ContainsKey("function"));
            
            var function = schema["function"] as Dictionary<string, object>;
            Assert.NotNull(function);
            Assert.Equal("text", function["name"]);
            Assert.Contains("text functions", function["description"]?.ToString()?.ToLower() ?? "");
        }

        [Fact]
        public async Task TextTool_ToUpper_ExecutesCorrectly()
        {
            // Arrange
            var tool = new TextTool();
            var input = System.Text.Json.JsonSerializer.Serialize(new { function = "toupper", text = "hello" });

            // Act
            var result = await tool.ExecuteAsync(input);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("HELLO", result);
        }

        [Fact]
        public async Task TextTool_Substring_ExecutesCorrectly()
        {
            // Arrange
            var tool = new TextTool();
            var input = System.Text.Json.JsonSerializer.Serialize(new 
            { 
                function = "substring", 
                text = "hello world",
                startIndex = 0,
                length = 5
            });

            // Act
            var result = await tool.ExecuteAsync(input);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("hello", result.ToLower());
        }

        [Fact]
        public void MathTool_GetSchema_ReturnsValidSchema()
        {
            // Arrange
            var tool = new MathTool();

            // Act
            var schema = tool.GetSchema();

            // Assert
            Assert.NotNull(schema);
            Assert.Equal("function", schema["type"]);
        }

        [Fact]
        public async Task MathTool_Add_ExecutesCorrectly()
        {
            // Arrange
            var tool = new MathTool();
            var input = System.Text.Json.JsonSerializer.Serialize(new { operation = "add", a = 2.0, b = 3.0 });

            // Act
            var result = await tool.ExecuteAsync(input);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("5", result);
        }

        [Fact]
        public async Task MathTool_Divide_WithZeroReturnError()
        {
            // Arrange
            var tool = new MathTool();
            var input = System.Text.Json.JsonSerializer.Serialize(new { operation = "divide", a = 10.0, b = 0.0 });

            // Act
            var result = await tool.ExecuteAsync(input);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("error", result.ToLower());
        }

        [Fact]
        public async Task MathTool_Multiply_ExecutesCorrectly()
        {
            // Arrange
            var tool = new MathTool();
            var input = System.Text.Json.JsonSerializer.Serialize(new { operation = "multiply", a = 4.0, b = 5.0 });

            // Act
            var result = await tool.ExecuteAsync(input);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("20", result);
        }

        [Fact]
        public void HybridOrchestrator_RegisterTool_StoresToolCorrectly()
        {
            // Arrange
            var orchestrator = new HybridLangChainOrchestrator();
            var tool = new MathTool();

            // Act
            orchestrator.RegisterTool(tool);
            var schemas = orchestrator.GetToolSchemas();

            // Assert
            Assert.NotEmpty(schemas);
            Assert.Single(schemas);
        }

        [Fact]
        public void HybridOrchestrator_ParseToolCalls_ReturnsEmptyForNoToolCalls()
        {
            // Arrange
            var orchestrator = new HybridLangChainOrchestrator();
            var modelResponse = System.Text.Json.JsonSerializer.Serialize(new { content = "This is just text" });

            // Act
            var calls = orchestrator.ParseToolCalls(modelResponse);

            // Assert
            Assert.Empty(calls);
        }

        [Fact]
        public void HybridOrchestrator_ParseToolCalls_ParsesValidToolCalls()
        {
            // Arrange
            var orchestrator = new HybridLangChainOrchestrator();
            var modelResponse = System.Text.Json.JsonSerializer.Serialize(new
            {
                tool_calls = new[]
                {
                    new
                    {
                        id = "call_123",
                        function = new
                        {
                            name = "math",
                            arguments = "{\"operation\": \"add\", \"a\": 2, \"b\": 3}"
                        }
                    }
                }
            });

            // Act
            var calls = orchestrator.ParseToolCalls(modelResponse);

            // Assert
            Assert.Single(calls);
            Assert.Equal("math", calls[0].ToolName);
            Assert.Contains("add", calls[0].Arguments);
        }

        [Fact]
        public async Task HybridOrchestrator_ExecuteToolAsync_CallsToolCorrectly()
        {
            // Arrange
            var orchestrator = new HybridLangChainOrchestrator();
            var tool = new MathTool();
            orchestrator.RegisterTool(tool);

            var input = System.Text.Json.JsonSerializer.Serialize(new { operation = "add", a = 5.0, b = 3.0 });

            // Act
            var result = await orchestrator.ExecuteToolAsync("math", input);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("8", result);
        }

        [Fact]
        public async Task HybridOrchestrator_ExecuteToolAsync_ReturnsErrorForUnregisteredTool()
        {
            // Arrange
            var orchestrator = new HybridLangChainOrchestrator();
            var input = System.Text.Json.JsonSerializer.Serialize(new { });

            // Act
            var result = await orchestrator.ExecuteToolAsync("nonexistent", input);

            // Assert
            Assert.NotNull(result);
            Assert.Contains("not found", result.ToLower());
        }

        [Fact]
        public void LangChainToolFactory_CreateEssentialOrchestrator_RegistersTools()
        {
            // Arrange
            var factory = new LangChainToolFactory();

            // Act
            var orchestrator = factory.CreateEssentialOrchestrator();
            var schemas = orchestrator.GetToolSchemas();

            // Assert
            Assert.NotEmpty(schemas);
            // Should have at least text and math tools
            Assert.True(schemas.Count >= 2);
        }

        [Fact]
        public void ReActLoop_ToolExecutionRecords_TracksExecutions()
        {
            // Arrange
            var tools = new HybridLangChainOrchestrator();
            var loop = new ReActLoopOrchestrator(tools);

            var record = new ReActLoopOrchestrator.ToolExecutionRecord
            {
                ToolName = "math",
                Input = "{\"operation\": \"add\", \"a\": 2, \"b\": 3}",
                Output = "{\"result\": 5}",
                IterationNumber = 1
            };

            // Assert
            Assert.Equal("math", record.ToolName);
            Assert.Equal(1, record.IterationNumber);
        }

        [Fact]
        public void ConversationMessage_Serialization_WorksCorrectly()
        {
            // Arrange
            var message = new ConversationMessage
            {
                Role = "user",
                Content = "Generate a story about a dragon"
            };

            // Act
            var json = message.ToString();

            // Assert
            Assert.NotNull(json);
            Assert.Contains("user", json);
            Assert.Contains("dragon", json);
        }
    }
}
