using System;
using System.Collections.Generic;
using System.Text.Json;
using TinyGenerator.Services;
using Xunit;

namespace TinyGenerator.Tests
{
    public class MultiStepOrchestrationTests
    {
        [Fact]
        public void ReconstructToolCallsJsonFromToolRecords_BuildsValidJson()
        {
            // Arrange
            var executed = new List<ReActLoopOrchestrator.ToolExecutionRecord>
            {
                new ReActLoopOrchestrator.ToolExecutionRecord
                {
                    ToolName = "add_narration",
                    Input = "{\"text\":\"Hello world\"}",
                    Output = "{\"result\": \"Narration added\"}",
                    IterationNumber = 1
                },
                new ReActLoopOrchestrator.ToolExecutionRecord
                {
                    ToolName = "add_phrase",
                    Input = "{\"character\":\"Bob\",\"text\":\"Hi\"}",
                    Output = "{\"result\": \"Phrase added\"}",
                    IterationNumber = 1
                }
            };

            // Act
            var json = MultiStepOrchestrationService.ReconstructToolCallsJsonFromToolRecords(executed);

            // Assert
            Assert.False(string.IsNullOrWhiteSpace(json));

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("tool_calls", out var tcArray));
            Assert.Equal(JsonValueKind.Array, tcArray.ValueKind);
            Assert.Equal(2, tcArray.GetArrayLength());

            var first = tcArray[0];
            var function = first.GetProperty("function");
            Assert.Equal("add_narration", function.GetProperty("name").GetString());

            var args = function.GetProperty("arguments");
            // arguments may be an object
            Assert.True(args.ValueKind == JsonValueKind.Object || args.ValueKind == JsonValueKind.String);

            // Second item check
            var second = tcArray[1];
            var fn2 = second.GetProperty("function");
            Assert.Equal("add_phrase", fn2.GetProperty("name").GetString());
        }

        [Fact]
        public void ReconstructToolCallsJsonFromToolRecords_ReturnsEmptyForEmptyList()
        {
            // Arrange
            var executed = new List<ReActLoopOrchestrator.ToolExecutionRecord>();

            // Act
            var json = MultiStepOrchestrationService.ReconstructToolCallsJsonFromToolRecords(executed);

            // Assert
            Assert.True(string.IsNullOrWhiteSpace(json));
        }

        [Fact]
        public void ReconstructToolCallsJsonFromToolRecords_PreservesNonJsonArgumentsAsString()
        {
            // Arrange: input is plain text (not JSON)
            var executed = new List<ReActLoopOrchestrator.ToolExecutionRecord>
            {
                new ReActLoopOrchestrator.ToolExecutionRecord
                {
                    ToolName = "add_narration",
                    Input = "This is a plain text argument",
                    Output = "{\"result\":\"Narration added\"}",
                    IterationNumber = 1
                }
            };

            // Act
            var json = MultiStepOrchestrationService.ReconstructToolCallsJsonFromToolRecords(executed);

            // Assert
            Assert.False(string.IsNullOrWhiteSpace(json));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tcArray = root.GetProperty("tool_calls");
            var first = tcArray[0];
            var args = first.GetProperty("function").GetProperty("arguments");
            Assert.Equal(JsonValueKind.String, args.ValueKind);
            Assert.Equal("This is a plain text argument", args.GetString());
        }

        [Fact]
        public void ReconstructToolCallsJsonFromToolRecords_ParsesJsonArguments()
        {
            // Arrange: input is a JSON string
            var executed = new List<ReActLoopOrchestrator.ToolExecutionRecord>
            {
                new ReActLoopOrchestrator.ToolExecutionRecord
                {
                    ToolName = "add_phrase",
                    Input = "{\"character\":\"Alice\",\"text\":\"Hello\"}",
                    Output = "{\"result\":\"Phrase added\"}",
                    IterationNumber = 1
                }
            };

            // Act
            var json = MultiStepOrchestrationService.ReconstructToolCallsJsonFromToolRecords(executed);

            // Assert
            Assert.False(string.IsNullOrWhiteSpace(json));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tcArray = root.GetProperty("tool_calls");
            var first = tcArray[0];
            var args = first.GetProperty("function").GetProperty("arguments");
            Assert.Equal(JsonValueKind.Object, args.ValueKind);
            Assert.Equal("Alice", args.GetProperty("character").GetString());
            Assert.Equal("Hello", args.GetProperty("text").GetString());
        }

        [Fact]
        public void EndToEnd_ReconstructAndValidate_TtsSchema_CoverageSufficient()
        {
            // Arrange: original story chunk (shortened version from logs)
            var chunk = @"Il comandante Andrea Carta fissava il vuoto oltre la finestra della sala comando, mentre il suo cuoio capelluto grigio si agitava nervosamente. La nave spaziale Cavour era appena atterrata sul nuovo pianeta, chiamato inizialmente 'Nexa-9' prima di essere rinominato ufficialmente 'Luminaria'. Carta, settantenne ma ancora agile come un uomo vent'anni più giovane, aveva guidato la nave attraverso decenni di guerra e sopravvivenza. \nComandante, disse la Prima Ufficiale Sofia Rodriguez, il dottor Patel vuole parlarle.";

            // Create executed tool records similar to the run in logs
            var executed = new List<ReActLoopOrchestrator.ToolExecutionRecord>
            {
                new ReActLoopOrchestrator.ToolExecutionRecord { ToolName = "add_narration", Input = System.Text.Json.JsonSerializer.Serialize(new { text = "Il comandante Andrea Carta fissava il vuoto oltre la finestra della sala comando, mentre il suo cuoio capelluto grigio si agitava nervosamente." }) },
                new ReActLoopOrchestrator.ToolExecutionRecord { ToolName = "add_narration", Input = System.Text.Json.JsonSerializer.Serialize(new { text = "La nave spaziale Cavour era appena atterrata sul nuovo pianeta, chiamato inizialmente 'Nexa-9' prima di essere rinominato ufficialmente 'Luminaria'." }) },
                new ReActLoopOrchestrator.ToolExecutionRecord { ToolName = "add_narration", Input = System.Text.Json.JsonSerializer.Serialize(new { text = "Carta, settantenne ma ancora agile come un uomo vent'anni più giovane, aveva guidato la nave attraverso decenni di guerra e sopravvivenza." }) },
                new ReActLoopOrchestrator.ToolExecutionRecord { ToolName = "add_phrase", Input = System.Text.Json.JsonSerializer.Serialize(new { character = "Sofia Rodriguez", emotion = "neutral", text = "Comandante, il dottor Patel vuole parlarle." }) }
            };

            // Act
            var json = MultiStepOrchestrationService.ReconstructToolCallsJsonFromToolRecords(executed);

            // Create a minimal IHttpClientFactory stub and logger
            var httpFactory = new TestHttpFactory();
            var logger = new TestLogger();

            var checker = new TinyGenerator.Services.ResponseCheckerService(null!, null!, logger, httpFactory);
            var ttsResult = checker.ValidateTtsSchemaResponse(json, chunk, 0.90);

            // Assert - we expect the reconstructed tool_calls to cover most of the chunk
            Assert.True(ttsResult.IsValid, "Expected TTS coverage to be valid after reconstruction");
        }

        // Minimal IHttpClientFactory stub for tests
        private class TestHttpFactory : System.Net.Http.IHttpClientFactory
        {
            public System.Net.Http.HttpClient CreateClient(string name) => new System.Net.Http.HttpClient();
        }

        private class TestLogger : TinyGenerator.Services.ICustomLogger
        {
            public void Log(string level, string category, string message, string? exception = null, string? state = null, string? result = null) { }
            public System.Threading.Tasks.Task FlushAsync() => System.Threading.Tasks.Task.CompletedTask;
            public void LogPrompt(string modelName, string prompt) { }
            public void LogResponse(string modelName, string response) { }
            public void LogRequestJson(string modelName, string requestJson) { }
            public void LogResponseJson(string modelName, string responseJson) { }
        }
    }
}
