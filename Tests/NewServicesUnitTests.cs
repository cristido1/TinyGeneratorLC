using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinyGenerator.Data;
using TinyGenerator.Models;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;
using Xunit;

namespace TinyGenerator.Tests;

public class AgentResolutionServiceTests
{
    [Fact]
    public void Resolve_ReturnsResolvedAgent_WhenConfigurationIsValid()
    {
        using var fixture = TestDbFixture.Create();
        var role = $"unit_test_role_{Guid.NewGuid():N}";
        var modelId = fixture.InsertModel("model-ambient");
        fixture.Db.InsertAgent(new Agent
        {
            Name = "Ambient Expert",
            Role = role,
            IsActive = true,
            ModelId = modelId,
            Prompt = "Prompt base",
            Instructions = "Istruzioni"
        });

        var sut = new AgentResolutionService(fixture.Db);

        var resolved = sut.Resolve(role);

        Assert.Equal(modelId, resolved.ModelId);
        Assert.Equal("model-ambient", resolved.ModelName);
        Assert.NotNull(resolved.BaseSystemPrompt);
        Assert.Contains("Prompt base", resolved.BaseSystemPrompt);
        Assert.Contains("Istruzioni", resolved.BaseSystemPrompt);
        Assert.Contains("model-ambient", resolved.TriedModelNames);
    }

    [Fact]
    public void Resolve_Throws_WhenAgentIsMissing()
    {
        using var fixture = TestDbFixture.Create();
        var sut = new AgentResolutionService(fixture.Db);
        var role = $"missing_role_{Guid.NewGuid():N}";

        var ex = Assert.Throws<InvalidOperationException>(() => sut.Resolve(role));

        Assert.Contains("No active", ex.Message);
    }

    [Fact]
    public void Resolve_Throws_WhenAgentHasNoModel()
    {
        using var fixture = TestDbFixture.Create();
        var role = $"unit_test_role_{Guid.NewGuid():N}";
        fixture.Db.InsertAgent(new Agent
        {
            Name = "Ambient",
            Role = role,
            IsActive = true,
            ModelId = null
        });

        var sut = new AgentResolutionService(fixture.Db);

        var ex = Assert.Throws<InvalidOperationException>(() => sut.Resolve(role));
        Assert.Contains("has no model configured", ex.Message);
    }
}

public class StoryTaggingPipelineServiceTests
{
    [Fact]
    public void PrepareAmbientTagging_Throws_WhenStoryNotFound()
    {
        using var fixture = TestDbFixture.Create();
        var sut = new StoryTaggingPipelineService(fixture.Db);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            sut.PrepareAmbientTagging(999999, new CommandTuningOptions.AmbientExpertTuning()));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void SaveAmbientTaggingResult_PersistsStoryTagsAndStoryTagged()
    {
        using var fixture = TestDbFixture.Create();
        var storyId = fixture.Db.InsertSingleStory("prompt", "Prima riga.\nSeconda riga.");

        var sut = new StoryTaggingPipelineService(fixture.Db);
        var prep = sut.PrepareAmbientTagging(storyId, new CommandTuningOptions.AmbientExpertTuning());
        sut.PersistInitialRows(prep);

        var tags = new List<StoryTaggingService.StoryTagEntry>
        {
            new(StoryTaggingService.TagTypeAmbient, 1, "[RUMORI: vento forte]")
        };

        var ok = sut.SaveAmbientTaggingResult(prep, tags, out var error);
        var story = fixture.Db.GetStoryById(storyId);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(story);
        Assert.Contains("RUMORI", story!.StoryTagged ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(StoryTaggingService.TagTypeAmbient, story.StoryTags ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}

public class NextStatusEnqueuerTests
{
    [Fact]
    public void TryAdvanceAndEnqueueAmbient_ReturnsFalse_WhenStoriesServiceIsMissing()
    {
        var logger = new RecordingLogger();
        var sut = new NextStatusEnqueuer(null, logger);
        var story = new StoryRecord { Id = 10 };

        var result = sut.TryAdvanceAndEnqueueAmbient(story, "run-1", 10, autolaunchNextCommand: true);

        Assert.False(result);
        Assert.Contains(logger.Appended, x => x.Contains("stories service missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryAdvanceAndEnqueueAmbient_ReturnsFalse_WhenAutolaunchDisabled()
    {
        var logger = new RecordingLogger();
        var sut = new NextStatusEnqueuer(null, logger);
        var story = new StoryRecord { Id = 11 };

        var result = sut.TryAdvanceAndEnqueueAmbient(story, "run-2", 11, autolaunchNextCommand: false);

        Assert.False(result);
        Assert.Contains(logger.Appended, x => x.Contains("AutolaunchNextCommand disabled", StringComparison.OrdinalIgnoreCase));
    }
}

public class CommandTelemetryTests
{
    [Fact]
    public void ReportProgress_ForwardsToSink()
    {
        var logger = new RecordingLogger();
        CommandProgressEventArgs? received = null;
        var sut = new CommandTelemetry(logger, args => received = args);

        sut.Start("run-telemetry");
        sut.Append("run-telemetry", "hello");
        sut.MarkLatestModelResponseResult("SUCCESS", null);
        sut.ReportProgress(2, 5, "step");
        sut.MarkCompleted("run-telemetry", "ok");

        Assert.Equal("run-telemetry", logger.StartedRunId);
        Assert.Contains("hello", logger.Appended);
        Assert.Equal("ok", logger.CompletedResult);
        Assert.NotNull(received);
        Assert.Equal(2, received!.Current);
        Assert.Equal(5, received.Max);
        Assert.Equal("step", received.Description);
    }
}

public class ChunkProcessingServiceTests
{
    [Fact]
    public async Task ProcessAsync_ReturnsResult_WhenModelResponseIsValid()
    {
        var responseJson = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "001 [RUMORI: vento e pioggia]"
              }
            }
          ]
        }
        """;

        var kernelFactory = new FakeKernelFactory(_ => responseJson);
        var sut = new ChunkProcessingService(kernelFactory, scopeFactory: null);

        var req = BuildRequest(minAmbientTagsRequired: 1);

        var result = await sut.ProcessAsync(req, CancellationToken.None);

        Assert.Equal("model-a", result.ModelName);
        Assert.Equal(1, result.ModelId);
        Assert.Contains("RUMORI", result.MappingText, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public async Task ProcessAsync_Throws_WhenBelowMinTagRequirement()
    {
        var responseJson = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "001 [RUMORI: vento]"
              }
            }
          ]
        }
        """;

        var kernelFactory = new FakeKernelFactory(_ => responseJson);
        var sut = new ChunkProcessingService(kernelFactory, scopeFactory: null);

        var req = BuildRequest(minAmbientTagsRequired: 2, maxAttempts: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProcessAsync(req, CancellationToken.None));
        Assert.Contains("Failed to process chunk", ex.Message);
    }

    private static ChunkProcessRequest BuildRequest(int minAmbientTagsRequired, int maxAttempts = 2)
    {
        var tuning = new CommandTuningOptions.AmbientExpertTuning
        {
            MaxAttemptsPerChunk = maxAttempts,
            MinAmbientTagsPerChunkRequirement = minAmbientTagsRequired,
            RetryDelayBaseSeconds = 0,
            DiagnoseOnFinalFailure = false,
            RequestTimeoutSeconds = 0
        };

        return new ChunkProcessRequest(
            Agent: new Agent { Name = "Ambient", Role = CommandRoleCodes.AmbientExpert, Temperature = 0.1 },
            RoleCode: CommandRoleCodes.AmbientExpert,
            SystemPrompt: "system",
            ChunkText: "001 Testo chunk",
            ChunkIndex: 1,
            ChunkCount: 1,
            RunId: "run-chunk",
            CurrentModelId: 1,
            CurrentModelName: "model-a",
            TriedModelNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "model-a" },
            Tuning: tuning,
            Telemetry: new CommandTelemetry(new RecordingLogger()),
            OperationScope: CommandScopePaths.AddAmbientTagsToStory);
    }
}

internal sealed class TestDbFixture : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _dbPath;

    private TestDbFixture(ServiceProvider provider, string dbPath, DatabaseService db)
    {
        _provider = provider;
        _dbPath = dbPath;
        Db = db;
    }

    public DatabaseService Db { get; }

    public static TestDbFixture Create()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TinyGeneratorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "storage.db");
        var sourceDbPath = FindSeedDbPath();

        if (!File.Exists(sourceDbPath))
        {
            throw new FileNotFoundException($"Database seed non trovato: {sourceDbPath}");
        }

        File.Copy(sourceDbPath, dbPath, overwrite: true);

        var services = new ServiceCollection();
        services.AddDbContext<TinyGeneratorDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        var provider = services.BuildServiceProvider();

        var db = new DatabaseService(dbPath, null, provider);

        return new TestDbFixture((ServiceProvider)provider, dbPath, db);
    }

    private static string FindSeedDbPath()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "data", "storage.db");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Database seed non trovato risalendo le directory (data/storage.db).");
    }

    public int InsertModel(string name)
    {
        Db.UpsertModel(new ModelInfo
        {
            Name = name,
            Provider = "test",
            Enabled = true,
            Endpoint = "http://localhost"
        });

        return Db.ListModels().Single(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).Id!.Value;
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}

internal sealed class FakeKernelFactory : ILangChainKernelFactory
{
    private readonly Func<HttpRequestMessage, string> _responseFactory;

    public FakeKernelFactory(Func<HttpRequestMessage, string> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public HybridLangChainOrchestrator CreateOrchestrator(string? model = null, IEnumerable<string>? allowedPlugins = null, int? agentId = null, string? ttsWorkingFolder = null, string? ttsStoryText = null)
        => throw new NotSupportedException();

    public HybridLangChainOrchestrator? GetOrchestratorForAgent(int agentId) => null;

    public void EnsureOrchestratorForAgent(int agentId, string? modelId = null, IEnumerable<string>? allowedPlugins = null)
    {
    }

    public void ClearCache()
    {
    }

    public int GetCachedCount() => 0;

    public LangChainChatBridge CreateChatBridge(
        string model,
        double? temperature = null,
        double? topP = null,
        double? repeatPenalty = null,
        int? topK = null,
        int? repeatLastN = null,
        int? numPredict = null,
        bool useMaxTokens = false)
    {
        var handler = new FakeHttpMessageHandler(_responseFactory);
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new LangChainChatBridge(
            "http://localhost",
            model,
            apiKey: "test-key",
            httpClient: httpClient,
            logger: null,
            forceOllama: true,
            services: null);
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string> _responseFactory;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, string> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var json = _responseFactory(request);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

internal sealed class RecordingLogger : ICustomLogger
{
    public string? StartedRunId { get; private set; }
    public string? CompletedResult { get; private set; }
    public List<string> Appended { get; } = new();

    public void Log(string level, string category, string message, string? exception = null, string? state = null, string? result = null) { }
    public Task FlushAsync() => Task.CompletedTask;
    public void LogPrompt(string modelName, string prompt, string? agentName = null) { }
    public void LogResponse(string modelName, string response, string? agentName = null) { }
    public void LogRequestJson(string modelName, string requestJson, int? threadId = null, string? agentName = null) { }
    public void LogResponseJson(string modelName, string responseJson, int? threadId = null, string? agentName = null) { }
    public void Start(string runId) => StartedRunId = runId;
    public Task AppendAsync(string runId, string message, string? extraClass = null)
    {
        Append(runId, message, extraClass);
        return Task.CompletedTask;
    }
    public void Append(string runId, string message, string? extraClass = null) => Appended.Add(message);
    public Task MarkCompletedAsync(string runId, string? finalResult = null)
    {
        MarkCompleted(runId, finalResult);
        return Task.CompletedTask;
    }
    public void MarkCompleted(string runId, string? finalResult = null) => CompletedResult = finalResult;
    public List<string> Get(string runId) => new();
    public bool IsCompleted(string runId) => false;
    public string? GetResult(string runId) => null;
    public void Clear(string runId) { }
    public Task ShowAgentActivityAsync(string agentName, string status, string? agentId = null, string testType = "question") => Task.CompletedTask;
    public void ShowAgentActivity(string agentName, string status, string? agentId = null, string testType = "question") { }
    public Task HideAgentActivityAsync(string agentId) => Task.CompletedTask;
    public void HideAgentActivity(string agentId) { }
    public void MarkLatestModelResponseResult(string result, string? failReason = null, bool? examined = null) { }
    public Task BroadcastLogsAsync(IEnumerable<LogEntry> entries) => Task.CompletedTask;
    public Task ModelRequestStartedAsync(string modelName) => Task.CompletedTask;
    public void ModelRequestStarted(string modelName) { }
    public Task ModelRequestFinishedAsync(string modelName) => Task.CompletedTask;
    public void ModelRequestFinished(string modelName) { }
    public IReadOnlyList<string> GetBusyModelsSnapshot() => Array.Empty<string>();
    public Task NotifyAllAsync(string title, string message, string level = "info") => Task.CompletedTask;
    public Task NotifyGroupAsync(string group, string title, string message, string level = "info") => Task.CompletedTask;
    public Task BroadcastStepProgress(Guid generationId, int current, int max, string stepDescription) => Task.CompletedTask;
    public Task BroadcastStepRetry(Guid generationId, int retryCount, string reason) => Task.CompletedTask;
    public Task BroadcastStepComplete(Guid generationId, int stepNumber) => Task.CompletedTask;
    public Task BroadcastTaskComplete(Guid generationId, string status) => Task.CompletedTask;
    public Task PublishEventAsync(string eventType, string title, string message, string level = "information", string? group = null) => Task.CompletedTask;
}
