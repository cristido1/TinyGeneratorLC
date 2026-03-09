using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed partial class SoundSearchService
{
    private readonly DatabaseService _database;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<SoundSearchOptions> _optionsMonitor;
    private readonly ICallCenter? _callCenter;
    private readonly ICustomLogger? _customLogger;
    private readonly ILogger<SoundSearchService>? _logger;

    public SoundSearchService(
        DatabaseService database,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<SoundSearchOptions> optionsMonitor,
        ICallCenter? callCenter = null,
        ICustomLogger? customLogger = null,
        ILogger<SoundSearchService>? logger = null)
    {
        _database = database;
        _httpClientFactory = httpClientFactory;
        _optionsMonitor = optionsMonitor;
        _callCenter = callCenter;
        _customLogger = customLogger;
        _logger = logger;
    }

    public IReadOnlyList<SoundMissing> ListOpenMissingSearchable(int? limit = null)
    {
        var list = _database.ListMissingSounds(status: "open")
            .Where(m => NormalizeSoundType(m.Type) is "fx" or "amb")
            .OrderByDescending(m => m.Occurrences)
            .ThenByDescending(m => m.LastSeenAt ?? string.Empty)
            .ThenBy(m => m.Id)
            .ToList();
        return limit.HasValue && limit.Value > 0 ? list.Take(limit.Value).ToList() : list;
    }

    public async Task<SoundSearchProcessResult> ProcessOneMissingSoundAsync(
        long missingId,
        CancellationToken ct = default,
        Action<int, int, string>? progress = null,
        string? runId = null)
    {
        var result = new SoundSearchProcessResult { MissingId = missingId };
        var opts = _optionsMonitor.CurrentValue ?? new SoundSearchOptions();
        void LogInfo(string msg)
        {
            LogRun(runId, msg);
        }
        void LogWarn(string msg)
        {
            LogRun(runId, msg, "warn");
        }

        if (!opts.Enabled)
        {
            result.Message = "SoundSearch disabilitato";
            LogWarn("Servizio disabilitato da appsettings.");
            return result;
        }

        var missing = _database.GetMissingSoundById(missingId);
        result.Missing = missing;
        if (missing == null)
        {
            result.Message = "sounds_missing non trovato";
            LogWarn("Record sounds_missing non trovato.");
            return result;
        }
        LogInfo($"Start missingId={missing.Id} type={missing.Type} status={missing.Status} occ={missing.Occurrences} prompt='{missing.Prompt}' tags='{missing.Tags}'");
        LogInfo($"Config fonti: freesound.enabled={opts.Freesound.Enabled} hasKey={!string.IsNullOrWhiteSpace(opts.Freesound.ApiKey)} | pixabay.enabled={opts.Pixabay.Enabled} hasKey={!string.IsNullOrWhiteSpace(opts.Pixabay.ApiKey)} | mixkit.enabled={opts.Mixkit.Enabled}");

        var type = NormalizeSoundType(missing.Type);
        if (type == "music")
        {
            result.Status = "skipped";
            result.Message = "type=music";
            LogInfo("Skip type=music (DB status invariato; usiamo solo open/resolved)");
            return result;
        }
        if (type is not ("fx" or "amb"))
        {
            _database.UpdateMissingSoundStatus(missing.Id, "open", notes: $"Tipo non supportato: {missing.Type}");
            result.Message = "tipo non supportato";
            LogWarn($"Tipo non supportato '{missing.Type}'.");
            return result;
        }

        var deterministicQueries = BuildQueries(missing, opts);
        LogInfo($"Query deterministiche: {string.Join(" || ", deterministicQueries)}");
        // FX/AMB ora usano tag ordinati dagli expert: niente query-planner agent, per evitare complessita' e incoerenze.
        var queries = deterministicQueries;
        LogInfo("Query planner agent (Fletcher sound search / FXFetcherAgent) disattivato per fx/amb: uso solo query derivate dai tag.");
        LogInfo($"Query finali: {string.Join(" || ", queries)}");
        var allCandidates = new List<SoundSearchCandidate>();
        var sourceActions = new List<(string Name, Func<CancellationToken, Task<List<SoundSearchCandidate>>> Fn)>();
        if (opts.Freesound.Enabled) sourceActions.Add(("freesound", c => SearchFreesoundAsync(type, queries, missing, opts, c, result.Errors, runId)));
        if (opts.Pixabay.Enabled) sourceActions.Add(("pixabay", c => SearchPixabayAsync(type, queries, missing, opts, c, result.Errors, runId)));
        if (opts.Mixkit.Enabled) sourceActions.Add(("mixkit", c => SearchMixkitAsync(type, queries, missing, opts, c, result.Errors, runId)));
        if (opts.OrangeFreeSounds.Enabled) sourceActions.Add(("orange", c => SearchOrangeAsync(type, queries, missing, opts, c, result.Errors, runId)));
        if (opts.SoundBible.Enabled) sourceActions.Add(("soundbible", c => SearchSoundBibleAsync(type, queries, missing, opts, c, result.Errors, runId)));
        if (opts.OpenGameArt.Enabled) sourceActions.Add(("opengameart", c => SearchOpenGameArtAsync(type, queries, missing, opts, c, result.Errors, runId)));
        LogInfo($"Fonte pipeline attiva: {(sourceActions.Count == 0 ? "<nessuna>" : string.Join(", ", sourceActions.Select(x => x.Name)))}");

        for (var i = 0; i < sourceActions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = sourceActions[i];
            result.TriedSources.Add(item.Name);
            result.SourcesTried++;
            progress?.Invoke(i + 1, sourceActions.Count + 1, $"Ricerca {item.Name} ({i + 1}/{sourceActions.Count})");
            LogInfo($"Ricerca fonte '{item.Name}' avviata con {queries.Count} query.");
            var errorsBefore = result.Errors.Count;
            try
            {
                var found = await item.Fn(ct).ConfigureAwait(false);
                result.CandidatesSeen += found.Count;
                allCandidates.AddRange(found);
                LogInfo($"Fonte '{item.Name}': candidati raccolti={found.Count}");
                foreach (var err in result.Errors.Skip(errorsBefore))
                {
                    LogWarn($"Fonte '{err.Source}' warning/error: {err.Error}{(string.IsNullOrWhiteSpace(err.Detail) ? string.Empty : " | " + err.Detail)}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SoundSearch source failed {Source}", item.Name);
                result.Errors.Add(new SoundSearchSourceError(item.Name, "SourceSearchFailed", ex.Message));
                LogWarn($"Fonte '{item.Name}' errore: {ex.Message}");
            }
        }

        progress?.Invoke(sourceActions.Count + 1, sourceActions.Count + 1, "Download + insert...");

        var existing = _database.ListSounds(type: type);
        var existingPaths = new HashSet<string>(
            existing.Where(s => !string.IsNullOrWhiteSpace(s.SoundPath)).Select(s => Path.GetFullPath(s.SoundPath)),
            StringComparer.OrdinalIgnoreCase);
        var existingKeys = new HashSet<string>(
            existing.Select(s => $"{NormalizeText(s.Library)}|{NormalizeText(s.SoundName)}"),
            StringComparer.OrdinalIgnoreCase);

        var maxInsertPerSearch = Math.Max(1, opts.MaxInsertPerSearch > 0 ? opts.MaxInsertPerSearch : 100);
        foreach (var group in allCandidates.OrderByDescending(c => c.Score).GroupBy(c => c.Source, StringComparer.OrdinalIgnoreCase))
        {
            LogInfo($"Fonte '{group.Key}': candidati ordinati={group.Count()}, limite insert={Math.Max(1, opts.MaxInsertPerSource)}");
            foreach (var candidate in group.Take(Math.Max(1, opts.MaxInsertPerSource)))
            {
                if (result.InsertedCount >= maxInsertPerSearch)
                {
                    LogInfo($"Raggiunto limite inserimenti per ricerca ({maxInsertPerSearch}). Stop insert.");
                    break;
                }
                ct.ThrowIfCancellationRequested();
                LogInfo($"Tentativo download/insert source={candidate.Source} extId={candidate.ExternalId} score={candidate.Score:0.##} title='{candidate.Title}' url='{candidate.DownloadUrl}'");
                var inserted = await TryDownloadAndInsertAsync(candidate, missing, type, opts, existingPaths, existingKeys, ct, runId).ConfigureAwait(false);
                if (inserted != null)
                {
                    result.Inserted.Add(inserted);
                    result.InsertedCount++;
                    LogInfo($"Inserito soundId={inserted.SoundId} file='{inserted.SoundName}' source={inserted.Source} score={inserted.Score:0.##}");
                }
                else
                {
                    LogInfo($"Candidato non inserito (skip/duplicate/download fail) source={candidate.Source} extId={candidate.ExternalId}");
                }
            }
            if (result.InsertedCount >= maxInsertPerSearch)
            {
                break;
            }
        }

        LogInfo($"Totale candidati raccolti={allCandidates.Count}; inseriti={result.InsertedCount}; errori={result.Errors.Count}");

        if (result.InsertedCount > 0)
        {
            var srcs = string.Join(",", result.Inserted.Select(x => x.Source).Distinct(StringComparer.OrdinalIgnoreCase));
            _database.UpdateMissingSoundStatus(missing.Id, "resolved", notes: $"Trovati {result.InsertedCount} suoni ({srcs})", source: srcs);
            result.Status = "found";
            result.Message = $"Trovati {result.InsertedCount} suoni";
            LogInfo($"Completato status=found inserted={result.InsertedCount} sources={srcs}");
        }
        else
        {
            _database.UpdateMissingSoundStatus(missing.Id, "open", notes: $"Nessun suono trovato. Fonti: {string.Join(",", result.TriedSources)}");
            result.Status = "not_found";
            result.Message = "Nessun suono trovato";
            LogWarn($"Completato status=not_found. Fonti={string.Join(",", result.TriedSources)} errori={result.Errors.Count}");
        }

        return result;
    }

    private async Task<SoundSearchInsertedSoundInfo?> TryDownloadAndInsertAsync(
        SoundSearchCandidate candidate,
        SoundMissing missing,
        string type,
        SoundSearchOptions opts,
        HashSet<string> existingPaths,
        HashSet<string> existingKeys,
        CancellationToken ct,
        string? runId)
    {
        var sourceFolder = NormalizeSourceFolder(candidate.Source);
        var root = string.IsNullOrWhiteSpace(opts.DownloadFolder)
            ? @"C:\Users\User\Documents\ai\sounds_library"
            : opts.DownloadFolder;
        var slug = BuildSlug(string.Join("_", ParseTagTokens(missing.Tags).Take(4).DefaultIfEmpty(type).Concat(new[] { candidate.Title })), 40);
        var targetFolder = Path.Combine(root, "audio_library", NormalizeSoundType(type), slug);
        Directory.CreateDirectory(targetFolder);

        var initialExt = NormalizeExtension(candidate.FileExtension, candidate.DownloadUrl);
        var fileExtForName = string.IsNullOrWhiteSpace(initialExt) ? ".bin" : initialExt;
        var fileName = $"{sourceFolder}_{BuildSlug(candidate.ExternalId, 48)}{fileExtForName}";
        var fullPath = Path.GetFullPath(Path.Combine(targetFolder, fileName));
        var dedupeKey = $"{NormalizeText(sourceFolder)}|{NormalizeText(fileName)}";
        if (opts.SkipIfAlreadyInSounds && (existingPaths.Contains(fullPath) || existingKeys.Contains(dedupeKey)))
            return null;

        if (!File.Exists(fullPath))
        {
            LogRun(runId, $"Download avvio source={candidate.Source} extId={candidate.ExternalId} url={candidate.DownloadUrl}");
            var bytes = await DownloadBytesAsync(candidate.DownloadUrl, opts, ct, runId).ConfigureAwait(false);
            if (bytes == null || bytes.Length < 5 * 1024)
            {
                LogRun(runId, $"Download fallito o file troppo piccolo source={candidate.Source} extId={candidate.ExternalId}", "warn");
                return null;
            }
            var finalPath = await SaveDownloadedCandidateAsync(bytes, candidate, opts, targetFolder, sourceFolder, ct, runId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(finalPath))
            {
                LogRun(runId, $"Download/scarico candidato non utilizzabile source={candidate.Source} extId={candidate.ExternalId}", "warn");
                return null;
            }
            fullPath = finalPath;
            fileName = Path.GetFileName(fullPath);
            dedupeKey = $"{NormalizeText(sourceFolder)}|{NormalizeText(fileName)}";
            LogRun(runId, $"Download OK file='{fullPath}' bytes={bytes.Length}");
        }
        else
        {
            fileName = Path.GetFileName(fullPath);
        }

        var finalExt = NormalizeExtension(Path.GetExtension(fullPath), fullPath);
        if (string.IsNullOrWhiteSpace(finalExt) || !IsAllowedExtension(finalExt, opts))
            return null;

        var sound = new Sound
        {
            Type = type,
            Library = sourceFolder,
            SoundPath = fullPath,
            SoundName = fileName,
            Description = BuildDescription(candidate, missing),
            License = candidate.License,
            Tags = MergeTags(missing.Tags, candidate.Tags),
            DurationSeconds = candidate.DurationSeconds,
            Enabled = true
        };

        int soundId;
        try
        {
            soundId = _database.InsertSound(sound);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "InsertSound duplicate/failed for {File}", fileName);
            return null;
        }

        existingPaths.Add(fullPath);
        existingKeys.Add(dedupeKey);
        return new SoundSearchInsertedSoundInfo(soundId, sourceFolder, fullPath, fileName, candidate.Score);
    }

    private async Task<byte[]?> DownloadBytesAsync(string url, SoundSearchOptions opts, CancellationToken ct, string? runId = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var client = CreateClient(opts);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        LogApiResponse(runId, "download", "file", url, (int)res.StatusCode, null);
        _logger?.LogInformation("SoundSearch download url={Url} status={Status}", url, (int)res.StatusCode);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> SaveDownloadedCandidateAsync(
        byte[] bytes,
        SoundSearchCandidate candidate,
        SoundSearchOptions opts,
        string targetFolder,
        string sourceFolder,
        CancellationToken ct,
        string? runId)
    {
        var ext = NormalizeExtension(candidate.FileExtension, candidate.DownloadUrl);
        if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return await ExtractFirstAudioFromZipAsync(bytes, candidate, opts, targetFolder, sourceFolder, ct, runId).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(ext) || !IsAllowedExtension(ext, opts))
        {
            return null;
        }

        var safeId = BuildSlug(candidate.ExternalId, 48);
        var fileName = $"{sourceFolder}_{safeId}{ext}";
        var fullPath = Path.GetFullPath(Path.Combine(targetFolder, fileName));
        await File.WriteAllBytesAsync(fullPath, bytes, ct).ConfigureAwait(false);
        return fullPath;
    }

    private async Task<string?> ExtractFirstAudioFromZipAsync(
        byte[] zipBytes,
        SoundSearchCandidate candidate,
        SoundSearchOptions opts,
        string targetFolder,
        string sourceFolder,
        CancellationToken ct,
        string? runId)
    {
        try
        {
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
            var entry = zip.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault(e =>
                {
                    var ext = NormalizeExtension(Path.GetExtension(e.Name), e.Name);
                    return !string.IsNullOrWhiteSpace(ext) && IsAllowedExtension(ext, opts);
                });

            if (entry == null)
            {
                LogRun(runId, $"ZIP senza file audio validi source={candidate.Source} extId={candidate.ExternalId}", "warn");
                return null;
            }

            var ext = NormalizeExtension(Path.GetExtension(entry.Name), entry.Name);
            if (string.IsNullOrWhiteSpace(ext)) return null;

            var fileName = $"{sourceFolder}_{BuildSlug(candidate.ExternalId, 48)}{ext}";
            var fullPath = Path.GetFullPath(Path.Combine(targetFolder, fileName));
            await using var src = entry.Open();
            await using var dst = File.Create(fullPath);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            LogRun(runId, $"ZIP estratto: entry='{entry.FullName}' => '{fullPath}'");
            return fullPath;
        }
        catch (Exception ex)
        {
            LogRun(runId, $"Errore estrazione ZIP source={candidate.Source} extId={candidate.ExternalId}: {ex.Message}", "warn");
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> BuildQueriesViaAgentAndMergeAsync(
        SoundMissing missing,
        IReadOnlyList<string> fallbackQueries,
        CancellationToken ct,
        string? runId)
    {
        try
        {
            var agent =
                _database.GetAgentByName("Fletcher sound search")
                ?? _database.GetAgentByName("FXFetcherAgent");
            if (agent == null || !agent.IsActive)
            {
                LogRun(runId, "Agente query sound search non trovato/attivo (attesi: 'Fletcher sound search' o alias legacy 'FXFetcherAgent'). Uso query deterministiche.", "warn");
                return fallbackQueries;
            }

            if (_callCenter == null)
            {
                LogRun(runId, "CallCenter non disponibile. Uso query deterministiche.", "warn");
                return fallbackQueries;
            }

            LogRun(runId, $"Chiamata agente '{agent.Name}' via CallCenter per query di ricerca...");
            LogRun(runId,
                $"INTERNET REQUEST | service={agent.Name} | stage=query_planner | method=CALLCENTER | url=internal://callcenter/fxfetcheragent\n" +
                $"REQUEST_BODY: type={NormalizeSoundType(missing.Type)} | prompt={missing.Prompt} | tags={missing.Tags} | fallback={string.Join(" || ", fallbackQueries)}");

            var history = new ChatHistory();
            history.AddSystem(
                "Sei un utility agent per la ricerca di suoni FX/ambience. " +
                "Ordina i tag per importanza per la ricerca web. " +
                "Rispondi SOLO testo puro. Formato preferito: 'TAGS: tag1, tag2, tag3' (in inglese, dal piu importante al meno importante). " +
                "Formato legacy ammesso solo se necessario: una query per riga, massimo 6 righe. " +
                "Nessun commento, nessuna spiegazione, nessuna numerazione, nessun markdown.");
            history.AddUser(
                $"Tipo: {NormalizeSoundType(missing.Type)}\n" +
                $"Prompt: {missing.Prompt}\n" +
                $"Tags: {missing.Tags}\n" +
                $"Query fallback (deterministiche): {string.Join(" | ", fallbackQueries)}\n" +
                "Restituisci preferibilmente una riga 'TAGS:' con tag in inglese ordinati per importanza. " +
                "Se non riesci, restituisci query brevi in inglese (puoi includere una fallback italiana).");

            var options = new CallOptions
            {
                Operation = "sound_search_query_planner",
                Timeout = TimeSpan.FromSeconds(25),
                MaxRetries = 1,
                UseResponseChecker = false,
                AskFailExplanation = false,
                AllowFallback = true
            };

            var call = await _callCenter.CallAgentAsync(
                storyId: missing.StoryId ?? 0,
                threadId: ($"fx_fetcher_agent:sound_search:{missing.Id}").GetHashCode(StringComparison.Ordinal),
                agent: agent,
                history: history,
                options: options,
                cancellationToken: ct).ConfigureAwait(false);

            if (!call.Success || string.IsNullOrWhiteSpace(call.ResponseText))
            {
                LogRun(runId, $"Agente '{agent.Name}' fallito: {call.FailureReason ?? "empty response"}", "warn");
                return fallbackQueries;
            }

            var orderedTags = TryParseOrderedTagsFromAgentResponse(call.ResponseText);
            var agentQueries = orderedTags.Count > 0
                ? BuildProgressiveQueriesFromOrderedTags(orderedTags)
                : ParseLegacyAgentQueries(call.ResponseText);

            if (orderedTags.Count > 0)
            {
                LogRun(runId, $"Agente '{agent.Name}' tags ordinati: {string.Join(", ", orderedTags)}");
                LogRun(runId, $"Query progressive da tag (tutti->1): {string.Join(" || ", agentQueries)}");
            }
            else
            {
                LogRun(runId, $"Agente '{agent.Name}' query response (legacy): {string.Join(" || ", agentQueries)}");
            }

            LogRun(runId,
                $"INTERNET RESPONSE | service={agent.Name} | stage=query_planner | status=200 | url=internal://callcenter/fxfetcheragent\n" +
                $"RESPONSE_BODY: {TruncateForLog(call.ResponseText, 1200)}");

            if (agentQueries.Count == 0)
                return fallbackQueries;

            return agentQueries
                .Concat(fallbackQueries)
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }
        catch (Exception ex)
        {
            LogRun(runId, $"Eccezione agente sound search: {ex.Message}", "warn");
            return fallbackQueries;
        }
    }

    private static List<string> TryParseOrderedTagsFromAgentResponse(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText)) return new();

        var normalized = responseText.Replace("\r\n", "\n").Replace('\r', '\n');
        var line = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("TAGS:", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(line)) return new();

        var raw = line[(line.IndexOf(':') + 1)..];
        return raw
            .Split(new[] { ',', ';', '|', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTagToken)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static List<string> BuildProgressiveQueriesFromOrderedTags(IReadOnlyList<string> orderedTags)
    {
        var queries = new List<string>();
        if (orderedTags == null || orderedTags.Count == 0) return queries;

        for (var len = orderedTags.Count; len >= 1; len--)
        {
            var q = string.Join(" ", orderedTags.Take(len).Select(t => t.Replace("_", " "))).Trim();
            if (!string.IsNullOrWhiteSpace(q))
                queries.Add(q);
        }

        return queries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static List<string> ParseLegacyAgentQueries(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText)) return new();

        return responseText
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(q => Regex.Replace(q, @"^\s*(?:[-*?]|\d+[.)]?)\s*", "").Trim())
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Take(6)
            .ToList();
    }

    private HttpClient CreateClient(SoundSearchOptions opts)
    {
        var client = _httpClientFactory.CreateClient("default");
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.TimeoutSeconds));
        if (!string.IsNullOrWhiteSpace(opts.UserAgent))
        {
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opts.UserAgent);
        }
        return client;
    }

    private static IReadOnlyList<string> BuildQueries(SoundMissing missing, SoundSearchOptions opts)
    {
        var tags = ParseTagTokens(missing.Tags).ToList();
        var type = NormalizeSoundType(missing.Type);
        var maxWords = Math.Max(2, opts.Query.MaxWordsPerQuery);

        var queries = type switch
        {
            "fx" => BuildFxQueries(tags, maxWords, opts.Query.IncludeFallbackQuery),
            "amb" => BuildAmbientQueries(tags, maxWords, opts.Query.IncludeFallbackQuery),
            "music" => BuildMusicQueries(tags, maxWords, opts.Query.IncludeFallbackQuery),
            _ => BuildGenericQueries(tags, maxWords, opts.Query.IncludeFallbackQuery)
        };

        return queries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BuildFxQueries(IReadOnlyList<string> tags, int maxWords, bool includeFallback)
    {
        var s1 = tags.ElementAtOrDefault(0);
        var s2 = tags.ElementAtOrDefault(1);
        var objectTag = tags.ElementAtOrDefault(2);
        var contextTag = tags.ElementAtOrDefault(4);
        var materialTag = tags.ElementAtOrDefault(6);

        var q = new List<string>
        {
            JoinQuery(maxWords, s1, objectTag),
            JoinQuery(maxWords, s2, objectTag),
            JoinQuery(maxWords, s1, materialTag, objectTag),
            JoinQuery(maxWords, objectTag, s1),
            JoinQuery(maxWords, objectTag, contextTag)
        };

        if (includeFallback)
        {
            q.Add(JoinQuery(maxWords, objectTag, s1));
        }

        return q;
    }

    private static IReadOnlyList<string> BuildAmbientQueries(IReadOnlyList<string> tags, int maxWords, bool includeFallback)
    {
        var category = tags.ElementAtOrDefault(0);
        var categorySyn = tags.ElementAtOrDefault(1);
        var s1 = tags.ElementAtOrDefault(2);
        var s2 = tags.ElementAtOrDefault(4);

        var q = new List<string>
        {
            JoinQuery(maxWords, category, "ambience"),
            JoinQuery(maxWords, categorySyn, "ambience"),
            JoinQuery(maxWords, s1, "ambience"),
            JoinQuery(maxWords, s1, category),
            JoinQuery(maxWords, s2, category)
        };

        if (includeFallback)
        {
            q.Add(JoinQuery(maxWords, category, "ambience"));
        }

        return q;
    }

    private static IReadOnlyList<string> BuildMusicQueries(IReadOnlyList<string> tags, int maxWords, bool includeFallback)
    {
        var type = tags.ElementAtOrDefault(0);
        var typeSyn = tags.ElementAtOrDefault(1);
        var mood = tags.ElementAtOrDefault(2);
        var energy = tags.ElementAtOrDefault(4);
        var context = tags.ElementAtOrDefault(6);

        var q = new List<string>
        {
            JoinQuery(maxWords, type, "music"),
            JoinQuery(maxWords, typeSyn, "music"),
            JoinQuery(maxWords, mood, type),
            JoinQuery(maxWords, energy, type),
            JoinQuery(maxWords, context, type)
        };
        if (includeFallback)
        {
            q.Add(JoinQuery(maxWords, type, "music"));
        }
        return q;
    }

    private static IReadOnlyList<string> BuildGenericQueries(IReadOnlyList<string> tags, int maxWords, bool includeFallback)
    {
        var ordered = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var q = new List<string>
        {
            JoinQuery(maxWords, ordered.Take(maxWords).ToArray()),
            JoinQuery(maxWords, ordered.Skip(1).Take(maxWords).ToArray()),
            JoinQuery(maxWords, ordered.Take(2).ToArray())
        };
        if (includeFallback && ordered.Count > 0) q.Add(JoinQuery(maxWords, ordered[0]));
        return q;
    }

    private static string JoinQuery(int maxWords, params string?[] words)
    {
        return string.Join(" ",
            (words ?? Array.Empty<string?>())
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(w => (w ?? string.Empty).Replace("_", " ").Trim())
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Take(Math.Max(1, maxWords)));
    }

    private static double ScoreCandidate(string source, string type, string title, SoundMissing missing, IReadOnlyList<string> candidateTags, double? duration, string? ext, SoundSearchOptions opts)
    {
        var wantedOrdered = ParseTagTokens(missing.Tags).ToList();
        var wanted = new HashSet<string>(wantedOrdered, StringComparer.OrdinalIgnoreCase);
        var have = new HashSet<string>((candidateTags ?? Array.Empty<string>()).Select(NormalizeTagToken).Concat(ExtractKeywordTokens(title)), StringComparer.OrdinalIgnoreCase);
        var sc = opts.Scoring ?? new SoundSearchScoringOptions();
        var score = 0d;

        var primary = wantedOrdered.ElementAtOrDefault(0);
        var primarySyn = wantedOrdered.ElementAtOrDefault(1);
        if (!string.IsNullOrWhiteSpace(primary) && have.Contains(primary)) score += sc.MatchPrimaryWeight;
        else if (!string.IsNullOrWhiteSpace(primarySyn) && have.Contains(primarySyn)) score += sc.MatchPrimaryWeight;

        var support2 = wantedOrdered.ElementAtOrDefault(2);
        var support4 = wantedOrdered.ElementAtOrDefault(4);
        var support6 = wantedOrdered.ElementAtOrDefault(6);
        if (!string.IsNullOrWhiteSpace(support2) && have.Contains(support2)) score += sc.MatchSecondaryWeight;
        if (!string.IsNullOrWhiteSpace(support4) && have.Contains(support4)) score += sc.MatchContextWeight;
        if (!string.IsNullOrWhiteSpace(support6) && have.Contains(support6)) score += sc.MatchMaterialOrEnergyWeight;

        // Bonus leggero per match extra oltre ai ruoli principali.
        var matches = wanted.Count(t => have.Contains(t));
        score += Math.Max(0, matches - 3) * 0.25d;

        if (duration.HasValue)
        {
            var d = duration.Value;
            if (type == "fx")
                score += (d >= 0.3d && d <= 8d) ? sc.DurationCompatibleWeight : (d <= opts.MaxDurationSecondsFx ? 0.25d : -1d);
            else
                score += (d >= Math.Max(1d, opts.MinDurationSeconds) && d <= opts.MaxDurationSecondsAmb) ? sc.DurationCompatibleWeight : -1d;
        }

        var format = (ext ?? string.Empty).ToLowerInvariant() switch { ".wav" or ".flac" => sc.FormatWavOrFlacWeight, ".mp3" => 0.5d, _ => 0d };
        var bias = source.ToLowerInvariant() switch
        {
            "freesound" => sc.SourceBonusFreesound,
            "pixabay" => sc.SourceBonusPixabay,
            "mixkit" => sc.SourceBonusMixkit,
            "orange" => sc.SourceBonusOrange,
            "soundbible" => sc.SourceBonusSoundBible,
            "opengameart" => sc.SourceBonusOpenGameArt,
            _ => 0d
        };
        return score + format + bias;
    }

    private static string BuildDescription(SoundSearchCandidate c, SoundMissing missing)
    {
        var parts = new List<string> { c.Title };
        if (!string.IsNullOrWhiteSpace(c.License)) parts.Add($"lic:{c.License}");
        if (!string.IsNullOrWhiteSpace(c.SourceUrl)) parts.Add($"src:{c.SourceUrl}");
        if (!string.IsNullOrWhiteSpace(missing.Prompt)) parts.Add($"missing:{missing.Prompt}");
        return string.Join(" | ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string MergeTags(string? missingTags, IReadOnlyList<string> sourceTags)
        => string.Join(", ", ParseTagTokens(missingTags).Concat((sourceTags ?? Array.Empty<string>()).Select(NormalizeTagToken)).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static bool IsAllowedExtension(string ext, SoundSearchOptions opts)
    {
        var allowed = (opts.PreferredSampleFormats ?? Array.Empty<string>())
            .Select(s => s.StartsWith('.') ? s.ToLowerInvariant() : "." + s.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowed.Count == 0 || allowed.Contains(ext.ToLowerInvariant());
    }

    private static string NormalizeExtension(string? ext, string? url)
    {
        var e = (ext ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(e))
        {
            var path = (url ?? string.Empty).Split('?')[0];
            e = Path.GetExtension(path);
        }
        if (string.IsNullOrWhiteSpace(e)) return string.Empty;
        if (!e.StartsWith('.')) e = "." + e;
        return e.ToLowerInvariant();
    }

    private static string NormalizeSourceFolder(string? source)
    {
        var s = Regex.Replace((source ?? "source").Trim(), @"[^a-zA-Z0-9_\- ]+", "").Trim();
        return string.IsNullOrWhiteSpace(s) ? "source" : s;
    }

    private static string BuildSlug(string? text, int max)
    {
        var slug = NormalizeTagToken(text);
        if (string.IsNullOrWhiteSpace(slug)) slug = "sound";
        return slug.Length <= max ? slug : slug[..max];
    }

    private static string NormalizeSoundType(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant();
    private static string NormalizeText(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant();

    private static IEnumerable<string> ParseTagTokens(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var part in Regex.Split(raw, @"[,;|\r\n]+"))
        {
            var t = NormalizeTagToken(part);
            if (!string.IsNullOrWhiteSpace(t)) yield return t;
        }
    }

    private static IEnumerable<string> ExtractKeywordTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var stop = new HashSet<string>(new[] { "il", "la", "lo", "i", "gli", "le", "un", "una", "di", "a", "da", "in", "con", "su", "per", "e", "o", "che", "si" }, StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-zàèéìòù0-9_]+"))
        {
            var t = NormalizeTagToken(m.Value);
            if (string.IsNullOrWhiteSpace(t) || stop.Contains(t)) continue;
            yield return t;
        }
    }

    private static IReadOnlyList<string> NormalizeProviderCandidateTags(
        IEnumerable<string>? providerTags,
        string? title,
        string? description,
        SoundSearchOptions opts)
    {
        var norm = opts.ProviderTagNormalization ?? new SoundSearchProviderTagNormalizationOptions();
        var stop = new HashSet<string>(
            (norm.StopWords ?? Array.Empty<string>())
                .Select(NormalizeTagToken)
                .Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);

        var result = new List<string>();

        void AddTag(string? raw)
        {
            var t = NormalizeTagToken(raw);
            if (string.IsNullOrWhiteSpace(t)) return;
            if (t.Length < Math.Max(1, norm.MinTokenLength)) return;
            if (stop.Contains(t)) return;
            result.Add(t);
        }

        var providerTagParts = (providerTags ?? Array.Empty<string>())
            .SelectMany(x => Regex.Split(x ?? string.Empty, @"[,;|]+"))
            .ToList();

        if (providerTagParts.Count > 0)
        {
            foreach (var ptag in providerTagParts) AddTag(ptag);
        }
        else
        {
            foreach (var t in ExtractKeywordTokens(title)) AddTag(t);
            foreach (var t in ExtractKeywordTokens(TrimToFirstWords(description, Math.Max(1, norm.DescriptionFallbackMaxWords)))) AddTag(t);
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string TrimToFirstWords(string? text, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWords <= 0) return string.Empty;
        return string.Join(" ",
            Regex.Split(text.Trim(), @"\s+")
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Take(maxWords));
    }

    private static string NormalizeTagToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;
        var t = token.Trim().Trim('"', '\'', '[', ']').ToLowerInvariant();
        t = Regex.Replace(t, @"\s+", "_");
        t = Regex.Replace(t, @"[^a-z0-9_]+", string.Empty);
        t = Regex.Replace(t, @"_+", "_").Trim('_');
        return t;
    }

    private static string CleanText(string? htmlText)
    {
        if (string.IsNullOrWhiteSpace(htmlText)) return string.Empty;
        var t = Regex.Replace(htmlText, "<.*?>", " ");
        t = WebUtility.HtmlDecode(t);
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static string? TryGetString(JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(property, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            _ => null
        };
    }

    private static string? ExtractFirstUrl(string html, params string[] regexes)
    {
        foreach (var rx in regexes)
        {
            var m = Regex.Match(html, rx, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success) return WebUtility.HtmlDecode(m.Value);
        }
        return null;
    }

    private static string? ExtractFirstGroup(string html, string pattern, string groupName)
    {
        var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[groupName].Value : null;
    }

    private static async Task<string?> SafeReadStringAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try { return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { return null; }
    }

    private static string ShortHash(string value)
    {
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant()[..12];
    }

    private void LogRun(string? runId, string message, string level = "info")
    {
        var custom = _customLogger ?? (ServiceLocator.Services?.GetService(typeof(ICustomLogger)) as ICustomLogger);
        if (!string.IsNullOrWhiteSpace(runId))
        {
            custom?.Append(runId!, $"[SoundSearch] {message}", level);
        }

        if (level.Equals("warn", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning("SoundSearch {Message}", message);
        }
        else if (level.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogError("SoundSearch {Message}", message);
        }
        else
        {
            _logger?.LogInformation("SoundSearch {Message}", message);
        }
    }

    private void LogApiResponse(string? runId, string source, string stage, string url, int? statusCode, string? body)
    {
        var code = statusCode.HasValue ? statusCode.Value.ToString() : "n/a";
        var snippet = TruncateForLog(body, 1200);
        LogRun(runId,
            $"INTERNET RESPONSE | service={source} | stage={stage} | status={code} | url={url}\n" +
            $"RESPONSE_BODY: {snippet}");
    }

    private void LogApiRequest(string? runId, string source, string stage, string url, string? requestBody = null)
    {
        var snippet = TruncateForLog(requestBody, 1200);
        LogRun(runId,
            $"INTERNET REQUEST | service={source} | stage={stage} | method=GET | url={url}\n" +
            $"REQUEST_BODY: {snippet}");
    }

    private static string TruncateForLog(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "<empty>";
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        if (normalized.Length <= maxChars) return normalized;
        return normalized.Substring(0, maxChars) + "...";
    }
}
