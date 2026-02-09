using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace TinyGenerator.Services
{
    // POCOs to represent provider/chat content objects
    public class ProviderWrapper
    {
        [JsonPropertyName("Content")]
        public string? Content { get; set; }

        // If some providers return object instead of string
        [JsonPropertyName("content")]
        public JsonElement? ContentElement { get; set; }
    }

    public class ChatMessageContent
    {
        [JsonPropertyName("$type")] public string? Type { get; set; }
        public Role? Role { get; set; }
        public List<TextContent>? Items { get; set; }
        public string? ModelId { get; set; }
        public Metadata? Metadata { get; set; }
    }

    public class Role { public string? Label { get; set; } }

    public class TextContent
    {
        [JsonPropertyName("$type")] public string? Type { get; set; }
        public string? Text { get; set; }
        public string? ModelId { get; set; }
    }

    public class Metadata
    {
        public Usage? Usage { get; set; }
    }

    public class Usage
    {
        public int? InputTokenCount { get; set; }
        public int? OutputTokenCount { get; set; }
        public int? TotalTokenCount { get; set; }
        public JsonElement? AdditionalCounts { get; set; }
    }

    public static class ProviderResponseParser
    {
        private static readonly JsonSerializerOptions _opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        /// <summary>
        /// Parse a raw provider response JSON and try to extract the clean textual content.
        /// It handles cases where the top-level `Content` is a JSON string containing a ChatMessageContent
        /// with `Items[].Text`, or where Content is already an object.
        /// Returns null if no textual content could be extracted.
        /// </summary>
        public static string? ExtractText(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;

                // If top-level object contains Content as string
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Content", out var contentEl))
                {
                    if (contentEl.ValueKind == JsonValueKind.String)
                    {
                        var inner = contentEl.GetString();
                        if (string.IsNullOrWhiteSpace(inner)) return null;
                        // inner is a JSON string representing ChatMessageContent -> parse it
                        try
                        {
                            var chat = JsonSerializer.Deserialize<ChatMessageContent>(inner, _opts);
                            var txt = ExtractFromChat(chat);
                            if (!string.IsNullOrWhiteSpace(txt)) return txt;
                        }
                        catch { /* fallthrough to attempt generic parse */ }

                        // fallback: try parse inner as generic JSON and look for Items[].Text
                        try
                        {
                            using var innerDoc = JsonDocument.Parse(inner);
                            var maybe = TryExtractItemsText(innerDoc.RootElement);
                            if (!string.IsNullOrWhiteSpace(maybe)) return maybe;
                        }
                        catch { return inner; }
                    }
                    else if (contentEl.ValueKind == JsonValueKind.Object)
                    {
                        // Content is already an object
                        var chat = JsonSerializer.Deserialize<ChatMessageContent>(contentEl.GetRawText(), _opts);
                        var txt = ExtractFromChat(chat);
                        if (!string.IsNullOrWhiteSpace(txt)) return txt;
                    }
                }

                // Try other common shapes: top-level Items[].Text
                var topItems = TryExtractItemsText(root);
                if (!string.IsNullOrWhiteSpace(topItems)) return topItems;

                // Try direct response/response.text fields
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String) return resp.GetString();
                    if (root.TryGetProperty("output", out var outp) && outp.ValueKind == JsonValueKind.Object && outp.TryGetProperty("response", out var r2) && r2.ValueKind == JsonValueKind.String) return r2.GetString();
                }

                // Last resort: return compacted root as string
                return root.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string? ExtractFromChat(ChatMessageContent? chat)
        {
            if (chat == null) return null;
            if (chat.Items != null && chat.Items.Count > 0)
            {
                return string.Join("\n", chat.Items.Where(i => !string.IsNullOrWhiteSpace(i.Text)).Select(i => i.Text!.Trim()));
            }
            return null;
        }

        private static string? TryExtractItemsText(JsonElement el)
        {
            try
            {
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Text", out var t) && t.ValueKind == JsonValueKind.String)
                        {
                            parts.Add(t.GetString() ?? string.Empty);
                        }
                    }
                    if (parts.Count > 0) return string.Join("\n", parts.Select(p => p.Trim()));
                }
            }
            catch { }
            return null;
        }
    }
}
