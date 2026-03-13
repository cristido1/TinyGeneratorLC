using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed class ValidateAgentJsonInstructionExamplesCommand : ICommand
{
    private readonly DatabaseService _database;
    private readonly ICustomLogger? _logger;
    private readonly bool _includeInactive;
    private readonly int _maxExamplesPerAgent;

    public ValidateAgentJsonInstructionExamplesCommand(
        DatabaseService database,
        ICustomLogger? logger = null,
        bool includeInactive = false,
        int maxExamplesPerAgent = 10)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger;
        _includeInactive = includeInactive;
        _maxExamplesPerAgent = Math.Max(1, maxExamplesPerAgent);
    }

    public string CommandName => "validate_agent_json_instruction_examples";
    public int Priority => 2;
    public bool Batch => true;

    public Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        ct.ThrowIfCancellationRequested();
        var started = DateTime.UtcNow;
        var responseFormatsDir = Path.Combine(Directory.GetCurrentDirectory(), "response_formats");
        var agents = _database.ListAgents()
            .Where(a =>
                !string.IsNullOrWhiteSpace(a.JsonResponseFormat) &&
                (_includeInactive || a.IsActive))
            .OrderBy(a => a.Description)
            .ToList();

        var inspectedAgents = 0;
        var totalExamples = 0;
        var invalidExamples = 0;
        var missingExamplesAgents = 0;
        var missingSchemasAgents = 0;
        var details = new List<string>();

        foreach (var agent in agents)
        {
            ct.ThrowIfCancellationRequested();
            inspectedAgents++;

            var schemaFile = SanitizeSchemaFileName(agent.JsonResponseFormat);
            if (string.IsNullOrWhiteSpace(schemaFile))
            {
                missingSchemasAgents++;
                details.Add($"- [{agent.Id}] {Safe(agent.Description)}: json_response_format vuoto/non valido.");
                continue;
            }

            var schemaPath = Path.Combine(responseFormatsDir, schemaFile);
            if (!File.Exists(schemaPath))
            {
                missingSchemasAgents++;
                details.Add($"- [{agent.Id}] {Safe(agent.Description)}: schema non trovato ({schemaFile}).");
                continue;
            }

            var examples = ExtractJsonExamples(agent.Instructions, _maxExamplesPerAgent);
            if (examples.Count == 0)
            {
                missingExamplesAgents++;
                details.Add($"- [{agent.Id}] {Safe(agent.Description)}: nessun esempio JSON rilevato nelle instructions.");
                continue;
            }

            var schemaJson = File.ReadAllText(schemaPath);
            var checker = new JsonSchemaResponseFormatCheck(schemaJson, schemaFile);
            var localInvalid = 0;

            for (var i = 0; i < examples.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                totalExamples++;
                var result = checker.Execute(examples[i]);
                if (result.Successed)
                {
                    continue;
                }

                localInvalid++;
                invalidExamples++;
                details.Add($"- [{agent.Id}] {Safe(agent.Description)} | esempio #{i + 1}: {Safe(result.Message)}");
            }

            if (localInvalid == 0)
            {
                details.Add($"- [{agent.Id}] {Safe(agent.Description)}: {examples.Count} esempio/i validi.");
            }
        }

        var elapsedMs = Math.Max(0, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        var success = invalidExamples == 0 && missingExamplesAgents == 0 && missingSchemasAgents == 0;

        var title = success
            ? "Verifica examples JSON instructions agenti: OK"
            : "Verifica examples JSON instructions agenti: criticita trovate";

        var summary =
            $"agenti_ispezionati={inspectedAgents}; esempi_valutati={totalExamples}; esempi_invalidi={invalidExamples}; " +
            $"agenti_senza_esempi={missingExamplesAgents}; agenti_senza_schema={missingSchemasAgents}; include_inactive={_includeInactive}";

        var report = new SystemReport
        {
            CreatedAt = DateTime.UtcNow.ToString("o"),
            Severity = success ? "info" : "warning",
            Status = "new",
            Deleted = false,
            Title = title,
            Message = summary,
            FailureReason = BuildFailureReason(summary, details),
            OperationType = CommandName,
            ExecutionTimeMs = elapsedMs,
            RetryCount = 0,
            RawLogRef = string.IsNullOrWhiteSpace(runId) ? null : $"runId={runId}"
        };
        var inserted = _database.InsertSystemReport(report);

        _logger?.Log(
            success ? "Info" : "Warning",
            "JsonExampleValidation",
            $"Report #{inserted.Id} - {summary}");

        var message = $"Report system_reports id={inserted.Id}. {summary}";
        return Task.FromResult(new CommandResult(true, message));
    }

    private static string BuildFailureReason(string summary, List<string> details)
    {
        var sb = new StringBuilder();
        sb.AppendLine(summary);
        if (details.Count == 0)
        {
            return sb.ToString().Trim();
        }

        sb.AppendLine();
        sb.AppendLine("Dettagli:");
        foreach (var line in details.Take(200))
        {
            sb.AppendLine(line);
        }

        if (details.Count > 200)
        {
            sb.AppendLine($"... altri {details.Count - 200} dettagli omessi");
        }

        return sb.ToString().Trim();
    }

    private static string SanitizeSchemaFileName(string? formatName)
    {
        if (string.IsNullOrWhiteSpace(formatName))
        {
            return string.Empty;
        }

        return Path.GetFileName(formatName.Trim());
    }

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static List<string> ExtractJsonExamples(string? instructions, int maxItems)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return new List<string>();
        }

        var text = instructions!;
        var fenced = Regex.Matches(
            text,
            @"```(?:json|JSON)?\s*(?<body>[\s\S]*?)```",
            RegexOptions.CultureInvariant);

        foreach (Match match in fenced)
        {
            if (!match.Success) continue;
            var candidate = match.Groups["body"].Value.Trim();
            TryAddJson(candidate, unique);
            if (unique.Count >= maxItems)
            {
                return unique.Take(maxItems).ToList();
            }
        }

        var i = 0;
        while (i < text.Length && unique.Count < maxItems)
        {
            var ch = text[i];
            if (ch != '{' && ch != '[')
            {
                i++;
                continue;
            }

            if (!TryExtractBalancedJson(text, i, out var json, out var endIndex))
            {
                i++;
                continue;
            }

            TryAddJson(json, unique);
            i = endIndex + 1;
        }

        return unique.Take(maxItems).ToList();
    }

    private static void TryAddJson(string? candidate, ISet<string> target)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var raw = candidate.Trim();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            target.Add(doc.RootElement.GetRawText());
        }
        catch
        {
            // Non JSON: ignora.
        }
    }

    private static bool TryExtractBalancedJson(string input, int start, out string json, out int endIndex)
    {
        json = string.Empty;
        endIndex = -1;
        if (start < 0 || start >= input.Length)
        {
            return false;
        }

        var open = input[start];
        var close = open == '{' ? '}' : (open == '[' ? ']' : '\0');
        if (close == '\0')
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < input.Length; i++)
        {
            var c = input[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == open)
            {
                depth++;
                continue;
            }

            if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    endIndex = i;
                    json = input.Substring(start, endIndex - start + 1);
                    return true;
                }
            }
        }

        return false;
    }
}
