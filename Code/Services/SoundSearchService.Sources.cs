using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed partial class SoundSearchService
{
    private async Task<List<SoundSearchCandidate>> SearchFreesoundAsync(
        string type,
        IReadOnlyList<string> queries,
        SoundMissing missing,
        SoundSearchOptions opts,
        CancellationToken ct,
        List<SoundSearchSourceError> errors,
        string? runId)
    {
        var apiKey = opts.Freesound.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            errors.Add(new SoundSearchSourceError("freesound", "SkippedNoApiKey"));
            return new List<SoundSearchCandidate>();
        }

        var client = CreateClient(opts);
        var maxDur = type == "fx" ? opts.MaxDurationSecondsFx : opts.MaxDurationSecondsAmb;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<SoundSearchCandidate>();

        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query)) continue;
            var url = $"{opts.Freesound.BaseUrl.TrimEnd('/')}/search/text/" +
                      $"?query={Uri.EscapeDataString(query)}" +
                      "&fields=id,name,previews,duration,tags,license,username,url" +
                      $"&filter={Uri.EscapeDataString($"duration:[0 TO {maxDur.ToString(CultureInfo.InvariantCulture)}]")}" +
                      $"&page_size={Math.Max(1, Math.Min(50, opts.Freesound.PageSize))}";
            LogApiRequest(runId, "freesound", "search", url, $"query={query}");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey}");
            using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
            var body = await SafeReadStringAsync(res, ct).ConfigureAwait(false);
            LogApiResponse(runId, "freesound", "search", url, (int)res.StatusCode, body);
            if (!res.IsSuccessStatusCode)
            {
                errors.Add(new SoundSearchSourceError("freesound", $"HTTP {(int)res.StatusCode}", body));
                continue;
            }

            using var doc = JsonDocument.Parse(body ?? string.Empty);
            if (!doc.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in items.EnumerateArray())
            {
                var extId = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : string.Empty;
                if (string.IsNullOrWhiteSpace(extId) || !seen.Add($"freesound:{extId}")) continue;
                var title = TryGetString(item, "name");
                var sourceUrl = TryGetString(item, "url");
                var duration = item.TryGetProperty("duration", out var dEl) && dEl.TryGetDouble(out var dur) ? dur : (double?)null;
                var providerTags = item.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array
                    ? tagsEl.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                    : new List<string>();
                var tags = NormalizeProviderCandidateTags(providerTags, title, TryGetString(item, "name"), opts);

                string? dl = null;
                if (item.TryGetProperty("previews", out var pEl) && pEl.ValueKind == JsonValueKind.Object)
                {
                    dl = TryGetString(pEl, "preview-hq-mp3")
                         ?? TryGetString(pEl, "preview_lq_mp3")
                         ?? TryGetString(pEl, "preview-hq-ogg")
                         ?? TryGetString(pEl, "preview-lq-mp3");
                }

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(sourceUrl) || string.IsNullOrWhiteSpace(dl))
                    continue;

                var score = ScoreCandidate("freesound", type, title!, missing, tags, duration, Path.GetExtension(dl), opts);
                list.Add(new SoundSearchCandidate(
                    "freesound", extId, title!, title, sourceUrl!, dl!, TryGetString(item, "license"),
                    TryGetString(item, "username"), duration, tags, Path.GetExtension(dl), score));
            }
        }

        return list.OrderByDescending(x => x.Score).Take(Math.Max(1, opts.MaxCandidatesPerSource)).ToList();
    }

    private async Task<List<SoundSearchCandidate>> SearchMixkitAsync(
        string type,
        IReadOnlyList<string> queries,
        SoundMissing missing,
        SoundSearchOptions opts,
        CancellationToken ct,
        List<SoundSearchSourceError> errors,
        string? runId)
    {
        var client = CreateClient(opts);
        var baseUrl = opts.Mixkit.BaseUrl.TrimEnd('/');
        var list = new List<SoundSearchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query)) continue;
            var urls = new[]
            {
                $"{baseUrl}{opts.Mixkit.SearchPath.TrimEnd('/')}?q={Uri.EscapeDataString(query)}",
                $"{baseUrl}{opts.Mixkit.CategoryPath}"
            }.Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var url in urls)
            {
                string html;
                try
                {
                    LogApiRequest(runId, "mixkit", "search", url, $"query={query}");
                    html = await client.GetStringAsync(url, ct).ConfigureAwait(false);
                    LogApiResponse(runId, "mixkit", "search", url, 200, html);
                }
                catch (Exception ex)
                {
                    errors.Add(new SoundSearchSourceError("mixkit", "SearchFailed", ex.Message));
                    LogRun(runId, $"mixkit search exception url={url}: {ex.Message}", "warn");
                    continue;
                }

                foreach (Match m in Regex.Matches(html, @"href=""(?<href>/free-sound-effects/[^""]+/)""[^>]*>(?<inner>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
                    var title = CleanText(m.Groups["inner"].Value);
                    if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title)) continue;

                    var detailUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{baseUrl}{href}";
                    var extId = Regex.Match(detailUrl, @"-(\d+)/?$").Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(extId)) extId = ShortHash(detailUrl);
                    if (!seen.Add($"mixkit:{extId}")) continue;

                    if (!LooksRelevantForMissing(title, query, missing)) continue;

                    string detailHtml;
                    try
                    {
                        LogApiRequest(runId, "mixkit", "detail", detailUrl, null);
                        detailHtml = await client.GetStringAsync(detailUrl, ct).ConfigureAwait(false);
                        LogApiResponse(runId, "mixkit", "detail", detailUrl, 200, detailHtml);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new SoundSearchSourceError("mixkit", "DetailFailed", ex.Message));
                        LogRun(runId, $"mixkit detail exception url={detailUrl}: {ex.Message}", "warn");
                        continue;
                    }

                    var dl = ExtractFirstUrl(detailHtml,
                        @"https://assets\.mixkit\.co[^""'\s>]+\.(?:mp3|wav)",
                        @"https://download\.mixkit\.co[^""'\s>]+\.(?:mp3|wav)");
                    if (string.IsNullOrWhiteSpace(dl)) continue;

                    var tags = NormalizeProviderCandidateTags(null, title, null, opts);
                    var score = ScoreCandidate("mixkit", type, title, missing, tags, null, Path.GetExtension(dl), opts);
                    list.Add(new SoundSearchCandidate("mixkit", extId, title, title, detailUrl, dl!, "mixkit", null, null, tags, Path.GetExtension(dl), score));
                }
            }
        }

        return list.OrderByDescending(x => x.Score).Take(Math.Max(1, opts.MaxCandidatesPerSource)).ToList();
    }

    private async Task<List<SoundSearchCandidate>> SearchPixabayAsync(
        string type,
        IReadOnlyList<string> queries,
        SoundMissing missing,
        SoundSearchOptions opts,
        CancellationToken ct,
        List<SoundSearchSourceError> errors,
        string? runId)
    {
        if (string.IsNullOrWhiteSpace(opts.Pixabay.ApiKey))
        {
            errors.Add(new SoundSearchSourceError("pixabay", "SkippedNoApiKey"));
            return new List<SoundSearchCandidate>();
        }

        var client = CreateClient(opts);
        var list = new List<SoundSearchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (opts.Pixabay.UseOfficialApiIfPossible)
        {
            foreach (var query in queries)
            {
                try
                {
                    var apiUrl = $"{opts.Pixabay.OfficialSoundsApiUrl.TrimEnd('/')}/?key={Uri.EscapeDataString(opts.Pixabay.ApiKey!)}&q={Uri.EscapeDataString(query)}&per_page={Math.Max(3, opts.MaxCandidatesPerSource)}";
                    LogApiRequest(runId, "pixabay", "api", apiUrl, $"query={query}");
                    using var res = await client.GetAsync(apiUrl, ct).ConfigureAwait(false);
                    var body = await SafeReadStringAsync(res, ct).ConfigureAwait(false);
                    LogApiResponse(runId, "pixabay", "api", apiUrl, (int)res.StatusCode, body);
                    if (!res.IsSuccessStatusCode)
                    {
                        errors.Add(new SoundSearchSourceError("pixabay", $"HTTP {(int)res.StatusCode}", body));
                        continue;
                    }
                    using var doc = JsonDocument.Parse(body ?? string.Empty);
                    if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) continue;

                    foreach (var hit in hits.EnumerateArray())
                    {
                        var dl = TryGetString(hit, "audio") ?? TryGetString(hit, "audioURL") ?? TryGetString(hit, "previewURL");
                        var page = TryGetString(hit, "pageURL");
                        if (string.IsNullOrWhiteSpace(dl) || string.IsNullOrWhiteSpace(page)) continue;
                        var extId = hit.TryGetProperty("id", out var idEl) ? idEl.ToString() : ShortHash(page);
                        if (!seen.Add($"pixabay:{extId}")) continue;
                        var title = TryGetString(hit, "tags") ?? $"pixabay_{extId}";
                        var providerTags = (TryGetString(hit, "tags") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
                        var tags = NormalizeProviderCandidateTags(providerTags, title, null, opts);
                        var score = ScoreCandidate("pixabay", type, title, missing, tags, null, Path.GetExtension(dl), opts);
                        list.Add(new SoundSearchCandidate("pixabay", extId, title, title, page!, dl!, "pixabay", TryGetString(hit, "user"), null, tags, Path.GetExtension(dl), score));
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new SoundSearchSourceError("pixabay", "ApiFailed", ex.Message));
                    LogRun(runId, $"pixabay api exception query='{query}': {ex.Message}", "warn");
                }
            }
        }

        if (list.Count == 0)
        {
            var baseUrl = opts.Pixabay.BaseUrl.TrimEnd('/');
            foreach (var query in queries)
            {
                string html;
                try
                {
                    var slugQuery = Uri.EscapeDataString(query).Replace("%20", "-").ToLowerInvariant();
                    var searchUrl = $"{baseUrl}/sound-effects/search/{slugQuery}/";
                    LogApiRequest(runId, "pixabay", "search_html", searchUrl, $"query={query}");
                    html = await client.GetStringAsync(searchUrl, ct).ConfigureAwait(false);
                    LogApiResponse(runId, "pixabay", "search_html", searchUrl, 200, html);
                }
                catch (Exception ex)
                {
                    errors.Add(new SoundSearchSourceError("pixabay", "SearchFailed", ex.Message));
                    LogRun(runId, $"pixabay search exception query='{query}': {ex.Message}", "warn");
                    continue;
                }

                foreach (Match m in Regex.Matches(html, @"href=""(?<href>/sound-effects/[^""]+/)""", RegexOptions.IgnoreCase))
                {
                    var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
                    if (string.IsNullOrWhiteSpace(href)) continue;
                    var detailUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{baseUrl}{href}";
                    var extId = Regex.Match(detailUrl, @"-(\d+)/?$").Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(extId)) extId = ShortHash(detailUrl);
                    if (!seen.Add($"pixabay:{extId}")) continue;

                    try
                    {
                        LogApiRequest(runId, "pixabay", "detail_html", detailUrl, null);
                        var detailHtml = await client.GetStringAsync(detailUrl, ct).ConfigureAwait(false);
                        LogApiResponse(runId, "pixabay", "detail_html", detailUrl, 200, detailHtml);
                        var dl = ExtractFirstUrl(detailHtml, @"https://cdn\.pixabay\.com/download/audio/[^""'\s>]+\.(?:mp3|wav)");
                        if (string.IsNullOrWhiteSpace(dl)) continue;
                        var title = CleanText(ExtractFirstGroup(detailHtml, @"<h1[^>]*>(?<v>.*?)</h1>", "v") ?? $"pixabay_{extId}");
                        if (!LooksRelevantForMissing(title, query, missing)) continue;
                        var tags = NormalizeProviderCandidateTags(null, title, null, opts);
                        var score = ScoreCandidate("pixabay", type, title, missing, tags, null, Path.GetExtension(dl), opts);
                        list.Add(new SoundSearchCandidate("pixabay", extId, title, title, detailUrl, dl!, "pixabay", null, null, tags, Path.GetExtension(dl), score));
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new SoundSearchSourceError("pixabay", "ParseFailed", ex.Message));
                        LogRun(runId, $"pixabay detail/parse exception url={detailUrl}: {ex.Message}", "warn");
                    }
                }
            }
        }

        return list.OrderByDescending(x => x.Score).Take(Math.Max(1, opts.MaxCandidatesPerSource)).ToList();
    }

    private async Task<List<SoundSearchCandidate>> SearchOrangeAsync(
        string type,
        IReadOnlyList<string> queries,
        SoundMissing missing,
        SoundSearchOptions opts,
        CancellationToken ct,
        List<SoundSearchSourceError> errors,
        string? runId)
    {
        var client = CreateClient(opts);
        var baseUrl = opts.OrangeFreeSounds.BaseUrl.TrimEnd('/');
        var list = new List<SoundSearchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query)) continue;
            var searchUrl = $"{baseUrl}/?s={Uri.EscapeDataString(query)}";
            try
            {
                LogApiRequest(runId, "orange", "search", searchUrl, $"query={query}");
                var html = await client.GetStringAsync(searchUrl, ct).ConfigureAwait(false);
                LogApiResponse(runId, "orange", "search", searchUrl, 200, html);

                foreach (Match m in Regex.Matches(html,
                             @"<a[^>]+href=""(?<href>https?://orangefreesounds\.com/[^""]+)""[^>]*>(?<title>.*?)</a>",
                             RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    var detailUrl = WebUtility.HtmlDecode(m.Groups["href"].Value);
                    var title = CleanText(m.Groups["title"].Value);
                    if (string.IsNullOrWhiteSpace(detailUrl) || string.IsNullOrWhiteSpace(title)) continue;
                    if (detailUrl.Contains("/?s=", StringComparison.OrdinalIgnoreCase)) continue;

                    var extId = BuildSlug(detailUrl.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? ShortHash(detailUrl), 64);
                    if (!seen.Add($"orange:{extId}")) continue;
                    if (!LooksRelevantForMissing(title, query, missing)) continue;

                    LogApiRequest(runId, "orange", "detail", detailUrl, null);
                    var detailHtml = await client.GetStringAsync(detailUrl, ct).ConfigureAwait(false);
                    LogApiResponse(runId, "orange", "detail", detailUrl, 200, detailHtml);

                    var dl = ExtractFirstUrl(detailHtml,
                        @"https?://[^""'\s>]+orangefreesounds[^""'\s>]+\.(?:mp3|wav)",
                        @"https?://[^""'\s>]+\.(?:mp3|wav)");
                    if (string.IsNullOrWhiteSpace(dl)) continue;

                    var author = CleanText(ExtractFirstGroup(detailHtml, @"(?:By|Author)\s*:?\s*</?(?:strong|b|span)?>?\s*(?<v>[A-Za-z0-9 _\-.]+)", "v"));
                    if (string.IsNullOrWhiteSpace(author)) author = null;
                    var tags = NormalizeProviderCandidateTags(null, title, query, opts);
                    var cand = new SoundSearchCandidate(
                        "orange",
                        extId,
                        title,
                        title,
                        detailUrl,
                        dl!,
                        "orangefreesounds-free",
                        author,
                        null,
                        tags,
                        Path.GetExtension(dl),
                        ScoreCandidate("orange", type, title, missing, tags, null, Path.GetExtension(dl), opts));
                    if (!PassesCandidateFilters(cand, type, opts)) continue;
                    list.Add(cand);
                }
            }
            catch (Exception ex)
            {
                errors.Add(new SoundSearchSourceError("orange", "SearchFailed", ex.Message));
                LogRun(runId, $"orange search/detail exception query='{query}': {ex.Message}", "warn");
            }
        }

        return list.OrderByDescending(x => x.Score).Take(Math.Max(1, opts.MaxCandidatesPerSource)).ToList();
    }

    private async Task<List<SoundSearchCandidate>> SearchSoundBibleAsync(
        string type,
        IReadOnlyList<string> queries,
        SoundMissing missing,
        SoundSearchOptions opts,
        CancellationToken ct,
        List<SoundSearchSourceError> errors,
        string? runId)
    {
        var client = CreateClient(opts);
        var baseUrl = opts.SoundBible.BaseUrl.TrimEnd('/');
        var list = new List<SoundSearchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query)) continue;
            var searchUrl = $"{baseUrl}/search.php?q={Uri.EscapeDataString(query)}";
            try
            {
                LogApiRequest(runId, "soundbible", "search", searchUrl, $"query={query}");
                var html = await client.GetStringAsync(searchUrl, ct).ConfigureAwait(false);
                LogApiResponse(runId, "soundbible", "search", searchUrl, 200, html);

                foreach (Match m in Regex.Matches(html, @"href=""(?<href>(?:https?://soundbible\.com/)?\d+-(?<slug>[^""]+)\.html)""", RegexOptions.IgnoreCase))
                {
                    var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
                    var detailUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{baseUrl}/{href.TrimStart('/')}";
                    var extId = Regex.Match(detailUrl, @"/(?<id>\d+)-").Groups["id"].Value;
                    if (string.IsNullOrWhiteSpace(extId)) extId = ShortHash(detailUrl);
                    if (!seen.Add($"soundbible:{extId}")) continue;

                    LogApiRequest(runId, "soundbible", "detail", detailUrl, null);
                    var detailHtml = await client.GetStringAsync(detailUrl, ct).ConfigureAwait(false);
                    LogApiResponse(runId, "soundbible", "detail", detailUrl, 200, detailHtml);

                    var title = CleanText(
                        ExtractFirstGroup(detailHtml, @"<h1[^>]*>(?<v>.*?)</h1>", "v")
                        ?? ExtractFirstGroup(detailHtml, @"<title[^>]*>(?<v>.*?)</title>", "v")
                        ?? $"soundbible_{extId}");
                    if (!LooksRelevantForMissing(title, query, missing)) continue;

                    var dl = ExtractFirstUrl(detailHtml,
                        @"https?://[^""'\s>]*soundbible\.com[^""'\s>]+\.(?:wav|mp3)",
                        @"(?:https?://[^""'\s>]+|/[^""'\s>]+)\.(?:wav|mp3)");
                    if (string.IsNullOrWhiteSpace(dl)) continue;
                    if (dl.StartsWith("/")) dl = $"{baseUrl}{dl}";

                    var license = CleanText(ExtractFirstGroup(detailHtml, @"(?:License|licen[cs]e)[^<:]*[: ]</?(?:a|span|b|strong)?[^>]*>(?<v>.*?)</", "v"));
                    if (string.IsNullOrWhiteSpace(license))
                    {
                        license = CleanText(ExtractFirstGroup(detailHtml, @"(?<v>Public Domain|Attribution\s*[0-9.]+)", "v"));
                    }
                    var author = CleanText(ExtractFirstGroup(detailHtml, @"(?:Author|Submitted by)[^<:]*[: ]</?(?:a|span|b|strong)?[^>]*>(?<v>.*?)</", "v"));
                    if (string.IsNullOrWhiteSpace(author)) author = null;

                    var tags = NormalizeProviderCandidateTags(null, title, query, opts);
                    var cand = new SoundSearchCandidate(
                        "soundbible",
                        extId,
                        title,
                        title,
                        detailUrl,
                        dl!,
                        string.IsNullOrWhiteSpace(license) ? null : license,
                        author,
                        null,
                        tags,
                        Path.GetExtension(dl),
                        ScoreCandidate("soundbible", type, title, missing, tags, null, Path.GetExtension(dl), opts));
                    if (!PassesCandidateFilters(cand, type, opts)) continue;
                    list.Add(cand);
                }
            }
            catch (Exception ex)
            {
                errors.Add(new SoundSearchSourceError("soundbible", "SearchFailed", ex.Message));
                LogRun(runId, $"soundbible search/detail exception query='{query}': {ex.Message}", "warn");
            }
        }

        return list.OrderByDescending(x => x.Score).Take(Math.Max(1, opts.MaxCandidatesPerSource)).ToList();
    }

    private async Task<List<SoundSearchCandidate>> SearchOpenGameArtAsync(
        string type,
        IReadOnlyList<string> queries,
        SoundMissing missing,
        SoundSearchOptions opts,
        CancellationToken ct,
        List<SoundSearchSourceError> errors,
        string? runId)
    {
        var client = CreateClient(opts);
        var baseUrl = opts.OpenGameArt.BaseUrl.TrimEnd('/');
        var list = new List<SoundSearchCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query)) continue;
            var searchUrl = $"{baseUrl}/art-search-advanced?keys={Uri.EscapeDataString(query)}&field_art_type_tid={opts.OpenGameArt.FieldArtTypeTid}";
            try
            {
                LogApiRequest(runId, "opengameart", "search", searchUrl, $"query={query}");
                var html = await client.GetStringAsync(searchUrl, ct).ConfigureAwait(false);
                LogApiResponse(runId, "opengameart", "search", searchUrl, 200, html);

                foreach (Match m in Regex.Matches(html, @"href=""(?<href>/content/[^""]+)""", RegexOptions.IgnoreCase))
                {
                    var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
                    if (string.IsNullOrWhiteSpace(href)) continue;
                    var detailUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{baseUrl}{href}";
                    var extId = Regex.Match(detailUrl, @"(?:/node/|nid=)(?<id>\d+)").Groups["id"].Value;
                    if (string.IsNullOrWhiteSpace(extId)) extId = ShortHash(detailUrl);
                    if (!seen.Add($"opengameart:{extId}")) continue;

                    LogApiRequest(runId, "opengameart", "detail", detailUrl, null);
                    var detailHtml = await client.GetStringAsync(detailUrl, ct).ConfigureAwait(false);
                    LogApiResponse(runId, "opengameart", "detail", detailUrl, 200, detailHtml);

                    var title = CleanText(
                        ExtractFirstGroup(detailHtml, @"<h1[^>]*>(?<v>.*?)</h1>", "v")
                        ?? ExtractFirstGroup(detailHtml, @"<title[^>]*>(?<v>.*?)</title>", "v")
                        ?? $"opengameart_{extId}");
                    if (!LooksRelevantForMissing(title, query, missing)) continue;

                    var dl = ExtractFirstUrl(detailHtml,
                        @"https?://[^""'\s>]+\.(?:wav|mp3|ogg|zip)",
                        @"/sites/default/files/[^""'\s>]+\.(?:wav|mp3|ogg|zip)");
                    if (string.IsNullOrWhiteSpace(dl))
                        continue;
                    if (dl.StartsWith("/")) dl = $"{baseUrl}{dl}";

                    var author = CleanText(ExtractFirstGroup(detailHtml, @"rel=""author""[^>]*>(?<v>.*?)</a>", "v"));
                    if (string.IsNullOrWhiteSpace(author)) author = null;
                    var license = CleanText(ExtractFirstGroup(detailHtml, @"(?:License|Licenses?)</[^>]+>\s*<[^>]+>(?<v>.*?)</", "v"));
                    if (string.IsNullOrWhiteSpace(license))
                    {
                        license = CleanText(ExtractFirstGroup(detailHtml, @"(?<v>CC0|CC-BY(?:-[A-Z]+)?|GPL|OGA-BY)", "v"));
                    }
                    var pageTags = Regex.Matches(detailHtml, @"rel=""tag""[^>]*>(?<v>.*?)</a>", RegexOptions.IgnoreCase)
                        .Cast<Match>()
                        .Select(x => CleanText(x.Groups["v"].Value))
                        .Where(x => !string.IsNullOrWhiteSpace(x));
                    var tags = NormalizeProviderCandidateTags(pageTags, title, query, opts);
                    var ext = Path.GetExtension(dl);
                    var cand = new SoundSearchCandidate(
                        "opengameart",
                        extId,
                        title,
                        title,
                        detailUrl,
                        dl!,
                        string.IsNullOrWhiteSpace(license) ? null : license,
                        author,
                        null,
                        tags,
                        string.IsNullOrWhiteSpace(ext) ? ".zip" : ext,
                        ScoreCandidate("opengameart", type, title, missing, tags, null, ext, opts));
                    if (!PassesCandidateFilters(cand, type, opts, allowZip: true)) continue;
                    list.Add(cand);
                }
            }
            catch (Exception ex)
            {
                errors.Add(new SoundSearchSourceError("opengameart", "SearchFailed", ex.Message));
                LogRun(runId, $"opengameart search/detail exception query='{query}': {ex.Message}", "warn");
            }
        }

        return list.OrderByDescending(x => x.Score).Take(Math.Max(1, opts.MaxCandidatesPerSource)).ToList();
    }

    private static bool LooksRelevantForMissing(string title, string query, SoundMissing missing)
    {
        var hay = new HashSet<string>(ExtractKeywordTokens(title), StringComparer.OrdinalIgnoreCase);
        var q = ExtractKeywordTokens(query).ToList();
        if (q.Count > 0 && q.Any(x => hay.Contains(x))) return true;
        var tags = ParseTagTokens(missing.Tags).ToList();
        return tags.Count == 0 || tags.Any(x => hay.Contains(x));
    }

    private static bool PassesCandidateFilters(SoundSearchCandidate c, string type, SoundSearchOptions opts, bool allowZip = false)
    {
        var ext = (c.FileExtension ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(c.DownloadUrl)) return false;
        if (!(ext is ".mp3" or ".wav" or ".ogg" || (allowZip && ext == ".zip"))) return false;

        if (c.DurationSeconds.HasValue)
        {
            var d = c.DurationSeconds.Value;
            if (d < opts.MinDurationSeconds) return false;
            var max = string.Equals(type, "fx", StringComparison.OrdinalIgnoreCase) ? opts.MaxDurationSecondsFx : opts.MaxDurationSecondsAmb;
            if (d > max) return false;
        }

        return true;
    }
}
