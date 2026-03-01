using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Logs;

public sealed class LogChatModel : PageModel
{
    private readonly DatabaseService _db;

    public LogChatModel(DatabaseService db)
    {
        _db = db;
    }

    [BindProperty(SupportsGet = true)]
    public string? CorrelationId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? ThreadId { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? StoryId { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool OnlyErrors { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool OnlyWarnings { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool OnlyAgent { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool OnlyCommand { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool OnlyValidator { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool OnlyApi { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool OnlyDb { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? MinDurationSecs { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Model { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Actor { get; set; }

    [BindProperty(SupportsGet = true)]
    public string ViewMode { get; set; } = "conversation";

    [BindProperty(SupportsGet = true)]
    public new int Page { get; set; } = 1;

    public List<ConversationGroupVm> Groups { get; private set; } = new();
    public List<ConversationEntryVm> FilteredEntries { get; private set; } = new();
    public List<string> AvailableCorrelations { get; private set; } = new();
    public int PageSize { get; } = 50;
    public int TotalFilteredRecords { get; private set; }
    public int TotalPages { get; private set; }
    public int TotalEvents { get; private set; }
    public int TotalErrors { get; private set; }
    public int TotalWarnings { get; private set; }
    public int TotalDurationSeconds { get; private set; }
    public string? SlowestActor { get; private set; }
    public string? MostFailingActor { get; private set; }

    public void OnGet()
    {
        ViewMode = string.Equals(ViewMode, "table", StringComparison.OrdinalIgnoreCase)
            ? "table"
            : "conversation";

        var logs = _db.GetRecentLogs(limit: 3000);
        var mapped = logs
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id ?? 0)
            .Select(MapEntry)
            .Where(e => e.IsAgentRequestOrResponse)
            .ToList();

        AvailableCorrelations = mapped
            .Select(m => m.CorrelationId)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(c => c, StringComparer.OrdinalIgnoreCase)
            .Take(250)
            .ToList();

        var filtered = mapped.Where(PassesFilter).ToList();
        TotalFilteredRecords = filtered.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalFilteredRecords / (double)PageSize));
        Page = Math.Max(1, Math.Min(Page <= 0 ? 1 : Page, TotalPages));
        var paged = filtered.Skip((Page - 1) * PageSize).Take(PageSize).ToList();
        FilteredEntries = paged;

        Groups = paged
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Operation) ? "unknown" : e.Operation.Trim())
            .Select(g =>
            {
                var events = g.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id).ToList();
                var newest = events.FirstOrDefault();
                var oldest = events.LastOrDefault();
                var correlations = events
                    .Select(e => e.CorrelationId)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new ConversationGroupVm
                {
                    CorrelationId = correlations.FirstOrDefault() ?? "system:global",
                    CorrelationLabel = g.Key,
                    GroupType = "operation",
                    CorrelationCount = correlations.Count,
                    Events = events,
                    EventCount = events.Count,
                    ErrorCount = events.Count(e => e.Result == "FAILED"),
                    WarningCount = events.Count(e => e.Result == "WARNING"),
                    TotalDurationSeconds = events.Sum(e => e.DurationSeconds ?? 0),
                    FirstTimestamp = oldest?.Timestamp ?? DateTime.MinValue,
                    LastTimestamp = newest?.Timestamp ?? DateTime.MinValue
                };
            })
            .OrderByDescending(g => g.LastTimestamp)
            .ToList();

        TotalEvents = TotalFilteredRecords;
        TotalErrors = filtered.Count(e => e.Result == "FAILED");
        TotalWarnings = filtered.Count(e => e.Result == "WARNING");
        TotalDurationSeconds = filtered.Sum(e => e.DurationSeconds ?? 0);

        SlowestActor = filtered
            .GroupBy(e => e.ActorDisplayName)
            .Select(g => new { Name = g.Key, Duration = g.Sum(x => x.DurationSeconds ?? 0) })
            .OrderByDescending(x => x.Duration)
            .FirstOrDefault()?.Name;

        MostFailingActor = filtered
            .Where(e => e.Result == "FAILED")
            .GroupBy(e => e.ActorDisplayName)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .FirstOrDefault()?.Name;
    }

    private bool PassesFilter(ConversationEntryVm e)
    {
        if (!string.IsNullOrWhiteSpace(CorrelationId) &&
            !string.Equals((CorrelationId ?? string.Empty).Trim(), e.CorrelationId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ThreadId.HasValue && ThreadId.Value > 0 && e.ThreadId != ThreadId.Value)
        {
            return false;
        }

        if (StoryId.HasValue && StoryId.Value > 0 && e.StoryId != StoryId.Value)
        {
            return false;
        }

        if (OnlyErrors && e.Result != "FAILED")
        {
            return false;
        }

        if (OnlyWarnings && e.Result != "WARNING")
        {
            return false;
        }

        if (OnlyAgent && e.ActorType != ActorType.AgentAi)
        {
            return false;
        }

        if (OnlyCommand && e.ActorType != ActorType.Command)
        {
            return false;
        }

        if (OnlyValidator && e.ActorType != ActorType.Validator)
        {
            return false;
        }

        if (OnlyApi && e.ActorType != ActorType.ExternalApi)
        {
            return false;
        }

        if (OnlyDb && e.ActorType != ActorType.Database)
        {
            return false;
        }

        if (MinDurationSecs.HasValue && MinDurationSecs.Value > 0)
        {
            var duration = e.DurationSeconds ?? 0;
            if (duration < MinDurationSecs.Value)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(Model) &&
            (e.ModelName?.Contains(Model.Trim(), StringComparison.OrdinalIgnoreCase) ?? false) == false)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Actor) &&
            (e.ActorDisplayName.Contains(Actor.Trim(), StringComparison.OrdinalIgnoreCase) == false))
        {
            return false;
        }

        return true;
    }

    private static ConversationEntryVm MapEntry(LogEntry log)
    {
        var actorType = ResolveActorType(log);
        var operationRaw = (log.ThreadScope ?? string.Empty).Trim();
        var operationDisplay = DatabaseService.NormalizeOperationForDisplay(operationRaw);
        var result = ResolveResult(log);
        var actorName = ResolveActorName(log, actorType, operationDisplay);
        var actorSubtitle = ResolveActorSubtitle(log, operationDisplay);
        var isRequest = IsAgentRequest(log);
        var isResponse = IsAgentResponse(log);
        var isChecker = IsResponseChecker(log);
        var isCheckerRequest = isChecker && isRequest;
        var message = ResolveMessage(log, actorType, result, operationDisplay, isChecker);
        var correlation = ResolveCorrelationId(log);
        var resourceStates = IsResourceManager(log)
            ? TryExtractResourceStates(log, message)
            : new List<ResourceStateVm>();

        return new ConversationEntryVm
        {
            Id = log.Id ?? 0,
            Timestamp = log.Timestamp,
            Source = log.Source ?? string.Empty,
            Operation = operationDisplay,
            OperationRaw = operationRaw,
            ActorDisplayName = actorName,
            ActorType = actorType,
            ActorSubtitle = actorSubtitle,
            ModelName = log.ModelName,
            Result = result,
            DurationSeconds = log.DurationSecs,
            Message = message,
            RawMessage = log.Message ?? string.Empty,
            FailReason = log.ResultFailReason,
            CorrelationId = correlation,
            ThreadId = log.ThreadId,
            StoryId = log.StoryId,
            PayloadJson = string.IsNullOrWhiteSpace(log.State) ? null : log.State,
            Context = log.Context,
            Exception = log.Exception,
            ChatText = log.ChatText,
            Icon = GetActorIcon(actorType),
            AccentClass = GetActorAccentClass(actorType),
            IsRequest = isRequest,
            IsResponse = isResponse,
            IsAgentRequestOrResponse = (isRequest || isResponse || isChecker) && !isCheckerRequest,
            IsResourceManager = IsResourceManager(log),
            ResourceStates = resourceStates
        };
    }

    private static string ResolveCorrelationId(LogEntry log)
    {
        if (!string.IsNullOrWhiteSpace(log.ThreadScope) &&
            !string.Equals(log.ThreadScope.Trim(), "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return $"scope:{log.ThreadScope.Trim()}";
        }

        if (log.ThreadId.HasValue && log.ThreadId.Value > 0)
        {
            return $"thread:{log.ThreadId.Value}";
        }

        if (log.StoryId.HasValue && log.StoryId.Value > 0)
        {
            return $"story:{log.StoryId.Value}";
        }

        return "system:global";
    }

    private static string FormatCorrelationLabel(string correlationId)
    {
        if (correlationId.StartsWith("scope:", StringComparison.OrdinalIgnoreCase))
        {
            return "Scope " + correlationId["scope:".Length..];
        }

        if (correlationId.StartsWith("thread:", StringComparison.OrdinalIgnoreCase))
        {
            return "Thread " + correlationId["thread:".Length..];
        }

        if (correlationId.StartsWith("story:", StringComparison.OrdinalIgnoreCase))
        {
            return "Story " + correlationId["story:".Length..];
        }

        return correlationId;
    }

    private static ActorType ResolveActorType(LogEntry log)
    {
        var source = (log.Source ?? string.Empty).ToLowerInvariant();
        var operation = (log.ThreadScope ?? string.Empty).ToLowerInvariant();
        var agent = (log.AgentName ?? string.Empty).ToLowerInvariant();
        var message = (log.Message ?? string.Empty).ToLowerInvariant();

        if (ContainsAny(source, "sql", "db", "repository") ||
            ContainsAny(message, "select ", "insert ", "update ", "delete from ", "sqlite"))
        {
            return ActorType.Database;
        }

        if (ContainsAny(source, "api", "http", "freesound", "audiocraft", "tool"))
        {
            return ActorType.ExternalApi;
        }

        if (ContainsAny(operation, "check", "validator") || ContainsAny(agent, "checker", "validator"))
        {
            return ActorType.Validator;
        }

        if (ContainsAny(operation, "callcenter", "dispatcher", "orchestr", "batch"))
        {
            return ActorType.Orchestrator;
        }

        if (!string.IsNullOrWhiteSpace(log.AgentName) ||
            ContainsAny(source, "modelrequest", "modelresponse", "modelprompt", "modelcompletion", "ollama", "openai", "llm"))
        {
            return ActorType.AgentAi;
        }

        if (ContainsAny(operation, "command", "generate_", "delete_", "enqueue_", "run_"))
        {
            return ActorType.Command;
        }

        if (ContainsAny(source, "request", "response"))
        {
            return ActorType.Command;
        }

        return ActorType.System;
    }

    private static string ResolveActorName(LogEntry log, ActorType actorType, string operationDisplay)
    {
        if (!string.IsNullOrWhiteSpace(log.AgentName))
        {
            return log.AgentName!.Trim();
        }

        return actorType switch
        {
            ActorType.Command => $"Command {operationDisplay}",
            ActorType.Orchestrator => "Orchestrator",
            ActorType.Validator => "Validator",
            ActorType.Database => "Database",
            ActorType.ExternalApi => "External API",
            _ => "Sistema"
        };
    }

    private static string? ResolveActorSubtitle(LogEntry log, string operationDisplay)
    {
        if (!string.IsNullOrWhiteSpace(log.ModelName))
        {
            return log.ModelName;
        }

        if (!string.IsNullOrWhiteSpace(operationDisplay) &&
            !string.Equals(operationDisplay, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return operationDisplay;
        }

        return null;
    }

    private static string ResolveResult(LogEntry log)
    {
        if (!string.IsNullOrWhiteSpace(log.Result))
        {
            return log.Result.Trim().ToUpperInvariant();
        }

        var level = (log.Level ?? string.Empty).Trim().ToLowerInvariant();
        if (level.Contains("error"))
        {
            return "FAILED";
        }

        if (level.Contains("warn"))
        {
            return "WARNING";
        }

        return "INFO";
    }

    private static string ResolveMessage(LogEntry log, ActorType actorType, string result, string operationDisplay, bool isResponseChecker)
    {
        if (isResponseChecker)
        {
            if (string.Equals(result, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                return "check passes";
            }

            var reason = ExtractFailureReason(log);
            return string.IsNullOrWhiteSpace(reason)
                ? "FAILED: check non passato"
                : $"FAILED: {reason}";
        }

        var raw = ExtractLastConversationMessage(log);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return DecodeEscapedText(raw.Trim());
        }

        if (!string.IsNullOrWhiteSpace(log.ResultFailReason))
        {
            return DecodeEscapedText(log.ResultFailReason!.Trim());
        }

        return actorType switch
        {
            ActorType.Command when result == "SUCCESS" => $"Fine comando {operationDisplay}",
            ActorType.Command => $"Avvio comando {operationDisplay}",
            ActorType.AgentAi when result == "SUCCESS" => "Output generato",
            ActorType.AgentAi => "Richiesta inviata",
            ActorType.Database when result == "SUCCESS" => "Scrittura dati completata",
            ActorType.Database => "Lettura dati",
            ActorType.ExternalApi when result == "SUCCESS" => "Risposta API ricevuta",
            ActorType.ExternalApi => "Chiamata API inviata",
            _ when result == "FAILED" => "Operazione fallita",
            _ when result == "WARNING" => "Attenzione: condizione non ottimale",
            _ => "Operazione completata"
        };
    }

    private static bool IsAgentRequest(LogEntry log)
    {
        var source = (log.Source ?? string.Empty).ToLowerInvariant();
        var msg = (log.Message ?? string.Empty).ToLowerInvariant();
        return ContainsAny(source, "modelrequest", "modelprompt", "prompt", "request")
               || (ContainsAny(source, "ollama", "openai", "llm", "model") && ContainsAny(msg, "request", "prompt", "invio"));
    }

    private static bool IsAgentResponse(LogEntry log)
    {
        var source = (log.Source ?? string.Empty).ToLowerInvariant();
        var msg = (log.Message ?? string.Empty).ToLowerInvariant();
        return ContainsAny(source, "modelresponse", "modelcompletion", "completion", "response")
               || (ContainsAny(source, "ollama", "openai", "llm", "model") && ContainsAny(msg, "response", "output", "result"));
    }

    private static bool IsResponseChecker(LogEntry log)
    {
        var source = (log.Source ?? string.Empty).ToLowerInvariant();
        var operation = (log.ThreadScope ?? string.Empty).ToLowerInvariant();
        var actor = (log.AgentName ?? string.Empty).ToLowerInvariant();
        return ContainsAny(source, "responsechecker", "response_checker")
               || ContainsAny(operation, "response_checker")
               || ContainsAny(actor, "response_checker", "responsechecker", "response checker");
    }

    private static bool IsResourceManager(LogEntry log)
    {
        var operation = (log.ThreadScope ?? string.Empty).ToLowerInvariant();
        var actor = (log.AgentName ?? string.Empty).ToLowerInvariant();
        return ContainsAny(operation, "resource_manager")
               || ContainsAny(actor, "resource_manager");
    }

    private static List<ResourceStateVm> TryExtractResourceStates(LogEntry log, string resolvedMessage)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(log.ChatText)) candidates.Add(log.ChatText!);
        if (!string.IsNullOrWhiteSpace(log.Message)) candidates.Add(log.Message!);
        if (!string.IsNullOrWhiteSpace(resolvedMessage)) candidates.Add(resolvedMessage);

        foreach (var candidate in candidates)
        {
            var rows = TryParseResourceStateRows(candidate);
            if (rows.Count > 0)
            {
                return rows;
            }
        }

        return new List<ResourceStateVm>();
    }

    private static List<ResourceStateVm> TryParseResourceStateRows(string rawText)
    {
        var normalized = DecodeEscapedText(rawText ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new List<ResourceStateVm>();
        }

        var jsonPayload = ExtractJsonObject(normalized);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return new List<ResourceStateVm>();
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonPayload);
            var resources = FindResourcesArray(doc.RootElement);
            if (resources is null || resources.Value.ValueKind != JsonValueKind.Array)
            {
                return new List<ResourceStateVm>();
            }

            var result = new List<ResourceStateVm>();
            foreach (var item in resources.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name =
                    ReadString(item, "name")
                    ?? ReadString(item, "resource_id")
                    ?? ReadString(item, "id")
                    ?? ReadString(item, "title")
                    ?? "-";

                var status =
                    ReadString(item, "status_flag")
                    ?? ReadString(item, "status")
                    ?? ReadString(item, "state")
                    ?? ReadString(item, "availability")
                    ?? ReadString(item, "mental_state_flag")
                    ?? "-";

                result.Add(new ResourceStateVm
                {
                    Resource = name,
                    Status = status
                });
            }

            return result.Take(200).ToList();
        }
        catch
        {
            return new List<ResourceStateVm>();
        }
    }

    private static JsonElement? FindResourcesArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("resources", out var directResources) && directResources.ValueKind == JsonValueKind.Array)
        {
            return directResources;
        }

        if (root.TryGetProperty("canon_state", out var canonState) && canonState.ValueKind == JsonValueKind.Object)
        {
            if (canonState.TryGetProperty("resources", out var canonResources) && canonResources.ValueKind == JsonValueKind.Array)
            {
                return canonResources;
            }

            if (canonState.TryGetProperty("current_canon_state", out var currentCanon)
                && currentCanon.ValueKind == JsonValueKind.Object
                && currentCanon.TryGetProperty("resources", out var nestedResources)
                && nestedResources.ValueKind == JsonValueKind.Array)
            {
                return nestedResources;
            }
        }

        return null;
    }

    private static string? ExtractJsonObject(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
        {
            return trimmed;
        }

        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');
        if (first >= 0 && last > first)
        {
            return trimmed.Substring(first, last - first + 1);
        }

        return null;
    }

    private static string? ReadString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string ExtractFailureReason(LogEntry log)
    {
        if (!string.IsNullOrWhiteSpace(log.ResultFailReason))
        {
            return DecodeEscapedText(log.ResultFailReason!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(log.Message))
        {
            return DecodeEscapedText(log.Message!.Trim());
        }

        return string.Empty;
    }

    private static string ExtractLastConversationMessage(LogEntry log)
    {
        var text = !string.IsNullOrWhiteSpace(log.ChatText)
            ? log.ChatText!
            : (!string.IsNullOrWhiteSpace(log.Message) ? log.Message! : string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decoded = DecodeEscapedText(text);

        var matches = Regex.Matches(decoded, @"\[(system|user|assistant|tool)\]", RegexOptions.IgnoreCase);
        if (matches.Count > 0)
        {
            var last = matches[matches.Count - 1];
            var start = last.Index + last.Length;
            if (start < decoded.Length)
            {
                return decoded[start..].Trim();
            }
        }

        var blocks = decoded
            .Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (blocks.Length > 0)
        {
            return blocks[^1].Trim();
        }

        return decoded.Trim();
    }

    private static string DecodeEscapedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var raw = text.Trim();
        try
        {
            var regexDecoded = Regex.Unescape(raw);
            if (!string.IsNullOrWhiteSpace(regexDecoded))
            {
                return regexDecoded;
            }
        }
        catch { }

        try
        {
            var wrapped = $"\"{raw.Replace("\"", "\\\"")}\"";
            var parsed = JsonSerializer.Deserialize<string>(wrapped);
            if (!string.IsNullOrWhiteSpace(parsed))
            {
                return parsed;
            }
        }
        catch { }

        return raw
            .Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
    }

    private static bool ContainsAny(string text, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (text.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetActorIcon(ActorType actorType)
    {
        return actorType switch
        {
            ActorType.AgentAi => "🤖",
            ActorType.Command => "🧩",
            ActorType.Orchestrator => "🧭",
            ActorType.Validator => "⚙",
            ActorType.Database => "🗄",
            ActorType.ExternalApi => "🌐",
            _ => "💻"
        };
    }

    private static string GetActorAccentClass(ActorType actorType)
    {
        return actorType switch
        {
            ActorType.AgentAi => "actor-agent",
            ActorType.Command => "actor-command",
            ActorType.Orchestrator => "actor-orchestrator",
            ActorType.Validator => "actor-validator",
            ActorType.Database => "actor-db",
            ActorType.ExternalApi => "actor-api",
            _ => "actor-system"
        };
    }

    public Dictionary<string, string?> BuildPageRouteValues(int targetPage)
    {
        return new Dictionary<string, string?>
        {
            ["CorrelationId"] = CorrelationId,
            ["ThreadId"] = ThreadId?.ToString(),
            ["StoryId"] = StoryId?.ToString(),
            ["OnlyErrors"] = OnlyErrors ? "true" : null,
            ["OnlyWarnings"] = OnlyWarnings ? "true" : null,
            ["OnlyAgent"] = OnlyAgent ? "true" : null,
            ["OnlyCommand"] = OnlyCommand ? "true" : null,
            ["OnlyValidator"] = OnlyValidator ? "true" : null,
            ["OnlyApi"] = OnlyApi ? "true" : null,
            ["OnlyDb"] = OnlyDb ? "true" : null,
            ["MinDurationSecs"] = MinDurationSecs?.ToString(),
            ["Model"] = Model,
            ["Actor"] = Actor,
            ["ViewMode"] = ViewMode,
            ["Page"] = targetPage.ToString()
        };
    }

    public enum ActorType
    {
        AgentAi,
        Command,
        Orchestrator,
        Validator,
        Database,
        ExternalApi,
        System
    }

    public sealed class ConversationGroupVm
    {
        public string CorrelationId { get; set; } = string.Empty;
        public string CorrelationLabel { get; set; } = string.Empty;
        public string GroupType { get; set; } = "operation";
        public int CorrelationCount { get; set; }
        public List<ConversationEntryVm> Events { get; set; } = new();
        public int EventCount { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public int TotalDurationSeconds { get; set; }
        public DateTime FirstTimestamp { get; set; }
        public DateTime LastTimestamp { get; set; }
    }

    public sealed class ConversationEntryVm
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string OperationRaw { get; set; } = string.Empty;
        public string ActorDisplayName { get; set; } = string.Empty;
        public ActorType ActorType { get; set; }
        public string? ActorSubtitle { get; set; }
        public string? ModelName { get; set; }
        public string Result { get; set; } = "INFO";
        public int? DurationSeconds { get; set; }
        public string Message { get; set; } = string.Empty;
        public string RawMessage { get; set; } = string.Empty;
        public string? FailReason { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
        public int? ThreadId { get; set; }
        public long? StoryId { get; set; }
        public string? PayloadJson { get; set; }
        public string? Context { get; set; }
        public string? Exception { get; set; }
        public string? ChatText { get; set; }
        public string Icon { get; set; } = "💻";
        public string AccentClass { get; set; } = "actor-system";
        public bool IsRequest { get; set; }
        public bool IsResponse { get; set; }
        public bool IsAgentRequestOrResponse { get; set; }
        public bool IsResourceManager { get; set; }
        public List<ResourceStateVm> ResourceStates { get; set; } = new();
    }

    public sealed class ResourceStateVm
    {
        public string Resource { get; set; } = "-";
        public string Status { get; set; } = "-";
    }
}
