using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;

namespace TinyGenerator.Services;

public static class BatchStoryCommandWorker
{
    public sealed record Request(string Operation, long StoryId, string RunId, string? Folder);

    public static bool TryParse(string[] args, out Request? request, out string? error)
    {
        request = null;
        error = null;

        if (args == null || args.Length == 0)
        {
            return false;
        }

        if (!args.Any(a => string.Equals(a, "--batch-worker", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var operation = GetArgValue(args, "--operation");
        var storyIdRaw = GetArgValue(args, "--story-id");
        var runId = GetArgValue(args, "--run-id");
        var folder = GetArgValue(args, "--folder");

        if (string.IsNullOrWhiteSpace(operation))
        {
            error = "Parametro --operation mancante.";
            return true;
        }
        if (string.IsNullOrWhiteSpace(storyIdRaw) || !long.TryParse(storyIdRaw, out var storyId) || storyId <= 0)
        {
            error = "Parametro --story-id non valido.";
            return true;
        }
        if (string.IsNullOrWhiteSpace(runId))
        {
            error = "Parametro --run-id mancante.";
            return true;
        }

        request = new Request(operation.Trim(), storyId, runId.Trim(), string.IsNullOrWhiteSpace(folder) ? null : folder.Trim());
        return true;
    }

    public static async Task<int> ExecuteAsync(IServiceProvider services, Request request, ILogger? logger, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = services.CreateScope();
            var storyService = scope.ServiceProvider.GetRequiredService<StoriesService>();

            var folder = request.Folder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                var story = storyService.GetStoryById(request.StoryId);
                if (story == null)
                {
                    Console.Error.WriteLine($"Story {request.StoryId} non trovata.");
                    return 2;
                }

                folder = !string.IsNullOrWhiteSpace(story.Folder)
                    ? story.Folder
                    : ResolveStoryFolderNameFromDisk(request.StoryId);
            }

            if (string.IsNullOrWhiteSpace(folder))
            {
                Console.Error.WriteLine($"Folder non risolta per story {request.StoryId}.");
                return 2;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var normalizedOperation = request.Operation.StartsWith("story_", StringComparison.OrdinalIgnoreCase)
                ? request.Operation["story_".Length..]
                : request.Operation;

            (bool success, string? message) result = normalizedOperation switch
            {
                "generate_ambience_audio" => await storyService.GenerateAmbienceForStoryAsync(request.StoryId, folder!, request.RunId).ConfigureAwait(false),
                "generate_fx_audio" => await storyService.GenerateFxForStoryAsync(request.StoryId, folder!, request.RunId).ConfigureAwait(false),
                "generate_music" => await storyService.GenerateMusicForStoryAsync(request.StoryId, folder!, request.RunId).ConfigureAwait(false),
                _ => (false, $"Operazione batch non supportata: {request.Operation}")
            };

            var finalMessage = string.IsNullOrWhiteSpace(result.message)
                ? $"Batch worker {request.Operation} {(result.success ? "completed" : "failed")}."
                : result.message!;

            if (result.success)
            {
                Console.WriteLine(finalMessage);
                return 0;
            }

            Console.Error.WriteLine(finalMessage);
            return 1;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Batch worker failed for operation {Operation} on story {StoryId}", request.Operation, request.StoryId);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? GetArgValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string? ResolveStoryFolderNameFromDisk(long storyId)
    {
        try
        {
            var storiesRoot = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder");
            if (!Directory.Exists(storiesRoot))
            {
                return null;
            }

            var idRaw = storyId.ToString();
            var idPadded = storyId.ToString("D5");
            var candidates = Directory.EnumerateDirectories(storiesRoot)
                .Select(Path.GetFileName)
                .Where(name =>
                    !string.IsNullOrWhiteSpace(name) &&
                    (name.StartsWith(idPadded + "_", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith(idRaw + "_", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, idRaw, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, idPadded, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return candidates.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
