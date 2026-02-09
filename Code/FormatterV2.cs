using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TinyGenerator.Services
{
    public static class FormatterV2
    {
        public sealed record Piece(string Text, bool IsTaggable, int? LineId);

        public sealed record BuildResult(string NumberedLines, IReadOnlyList<Piece> Pieces, int TaggableLineCount);

        // NOTE: double quotes (\") are handled with dedicated logic to keep quoted spans together.
        private static readonly HashSet<char> SplitAfterChars = new(new[] { ',', ';', ':' });

        // We keep this strict: the goal is *not* to rewrite text, only to classify.
        // Removes ONLY the tags we insert/expect (plus legacy aliases). Used for integrity checks.
        private static readonly Regex StripTagsRegex = new(
            @"\[(?:NARRATORE|PERSONAGGIO:[^\]]+|EMOZIONE:[^\]]+|SENTIMENTO:[^\]]+)\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static BuildResult BuildNumberedLines(string input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var pieces = SegmentIntoPieces(input);
            pieces = NormalizeStandaloneDoubleQuotePieces(pieces);

            int lineId = 1;
            var numberedLines = new List<(int Id, StringBuilder Text)>();

            for (int i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                if (!piece.IsTaggable)
                {
                    pieces[i] = piece with { LineId = null };
                    continue;
                }

                // Do not send purely-whitespace lines to the agent.
                if (!piece.Text.Any(c => !char.IsWhiteSpace(c)))
                {
                    pieces[i] = piece with { IsTaggable = false, LineId = null };
                    continue;
                }

                // Never send lines that are only a standalone quote (", “, ”).
                // If the quote is on its own line, append it to the previous numbered line for the agent's view.
                if (IsStandaloneQuote(piece.Text))
                {
                    if (numberedLines.Count > 0 && i - 1 >= 0 && pieces[i - 1].IsTaggable == false && IsNewlinePiece(pieces[i - 1]))
                    {
                        numberedLines[^1].Text.Append(piece.Text);
                    }
                    pieces[i] = piece with { IsTaggable = false, LineId = null };
                    continue;
                }

                var id = lineId++;
                pieces[i] = piece with { LineId = id };
                numberedLines.Add((id, new StringBuilder(piece.Text)));
            }

            var sb = new StringBuilder();
            for (int i = 0; i < numberedLines.Count; i++)
            {
                var (id, text) = numberedLines[i];
                sb.Append(id.ToString("000")).Append(' ').Append(text).Append('\n');
            }

            return new BuildResult(sb.ToString().TrimEnd('\n'), pieces, lineId - 1);
        }

        private static List<Piece> NormalizeStandaloneDoubleQuotePieces(List<Piece> pieces)
        {
            // Goal: avoid producing a numbered/taggable piece that is just a single quote (", “, ”).
            // We only merge when the quote is adjacent to another taggable piece (no newline/non-taggable in between),
            // so we never change the underlying text order.
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (!p.IsTaggable) continue;

                if (!IsStandaloneQuote(p.Text))
                {
                    continue;
                }

                // If the quote is at end-of-line (next piece is newline) and a taggable piece follows after the newline,
                // it's usually an opening quote misplaced on the previous line. Move it to the next taggable piece.
                if (i + 2 < pieces.Count && IsNewlinePiece(pieces[i + 1]) && pieces[i + 2].IsTaggable)
                {
                    var next = pieces[i + 2];
                    pieces[i + 2] = next with { Text = p.Text + next.Text };
                    pieces.RemoveAt(i);
                    i--;
                    continue;
                }

                // Prefer merging into previous taggable piece.
                if (i - 1 >= 0 && pieces[i - 1].IsTaggable)
                {
                    var prev = pieces[i - 1];
                    pieces[i - 1] = prev with { Text = prev.Text + p.Text };
                    pieces.RemoveAt(i);
                    i--;
                    continue;
                }

                // If the quote is at start-of-line (previous piece is newline), keep it separate.
                // BuildNumberedLines will append it to the previous numbered line for the agent's view.
                if (i - 1 >= 0 && IsNewlinePiece(pieces[i - 1]))
                {
                    continue;
                }

                // Otherwise, merge into next taggable piece when safe.
                if (i + 1 < pieces.Count && pieces[i + 1].IsTaggable)
                {
                    var next = pieces[i + 1];
                    pieces[i + 1] = next with { Text = p.Text + next.Text };
                    pieces.RemoveAt(i);
                    i--;
                    continue;
                }

                // No adjacent taggable pieces: leave it as-is, but it will be marked non-taggable during numbering.
            }

            return pieces;
        }

        private static bool IsStandaloneQuote(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();
            return t.Length == 1 && (t[0] == '"' || t[0] == '“' || t[0] == '”' || t[0] == '«' || t[0] == '»');
        }

        private static bool IsDialogueQuoteChar(char c)
        {
            return c == '"' || c == '“' || c == '”' || c == '«' || c == '»';
        }

        private static bool IsLineStart(string input, int index)
        {
            if (index <= 0) return true;
            var prev = input[index - 1];
            return prev == '\n' || prev == '\r';
        }

        private static bool IsNewlinePiece(Piece piece)
        {
            return piece.Text == "\n" || piece.Text == "\r" || piece.Text == "\r\n";
        }

        public static Dictionary<int, string> ParseIdToTagsMapping(string? mappingText)
        {
            var map = new Dictionary<int, string>();
            if (string.IsNullOrWhiteSpace(mappingText)) return map;

            var lines = mappingText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var raw in lines)
            {
                var line = (raw ?? string.Empty).Trim();
                if (line.Length == 0) continue;

                // Accept optional bullet prefix: "- 054: [PERSONAGGIO: ...] [EMOZIONE: ...]"
                if (line.StartsWith("-", StringComparison.Ordinal))
                {
                    line = line.Substring(1).TrimStart();
                    if (line.Length == 0) continue;
                }

                // Accept lines like:
                //   004 [PERSONAGGIO: Luca] [EMOZIONE: paura] (testo extra da ignorare)
                //   004: [PERSONAGGIO: Luca] [EMOZIONE: paura]
                // Also allow IDs with more than 3 digits.
                int i = 0;
                while (i < line.Length && char.IsDigit(line[i])) i++;
                if (i == 0) continue;
                if (!int.TryParse(line.Substring(0, i), out var id)) continue;

                var rest = line.Substring(i).TrimStart();
                // Accept optional delimiter between ID and tag payload.
                if (rest.StartsWith(":", StringComparison.Ordinal))
                {
                    rest = rest.Substring(1).TrimStart();
                }
                if (rest.Length == 0) continue;

                var tagParts = new List<string>(capacity: 2);
                int pos = 0;
                while (pos < rest.Length)
                {
                    while (pos < rest.Length && char.IsWhiteSpace(rest[pos])) pos++;
                    if (pos >= rest.Length || rest[pos] != '[') break;

                    int close = rest.IndexOf(']', pos + 1);
                    if (close < 0) break;

                    tagParts.Add(rest.Substring(pos, close - pos + 1));
                    pos = close + 1;
                }

                if (tagParts.Count == 0) continue;
                var tags = string.Join(" ", tagParts).Trim();

                // Basic validation: must be narrator OR have a character tag.
                if (!IsAllowedTagSet(tags))
                {
                    continue;
                }

                map[id] = NormalizeTags(tags);
            }

            return map;
        }

        /// <summary>
        /// Expands a sparse mapping (only IDs where a character speaks) into a full per-line mapping (1..N)
        /// by assigning implicit narrator tags to all non-specified lines.
        /// </summary>
        public static Dictionary<int, string> ExpandSparseMapping(int taggableLineCount, IReadOnlyDictionary<int, string> idToTagsStarts)
        {
            if (taggableLineCount < 0) throw new ArgumentOutOfRangeException(nameof(taggableLineCount));
            if (idToTagsStarts == null) throw new ArgumentNullException(nameof(idToTagsStarts));

            var full = new Dictionary<int, string>(capacity: taggableLineCount);
            if (taggableLineCount == 0) return full;

            for (int id = 1; id <= taggableLineCount; id++)
            {
                if (idToTagsStarts.TryGetValue(id, out var updated) && !string.IsNullOrWhiteSpace(updated))
                {
                    full[id] = updated;
                }
                else
                {
                    full[id] = "[NARRATORE]";
                }
            }

            return full;
        }

        /// <summary>
        /// Inserts tags only when the speaker changes (section-based tagging).
        /// The original text is preserved exactly; only tags are added.
        /// </summary>
        public static string ApplySectionTags(string originalText, IReadOnlyList<Piece> pieces, IReadOnlyDictionary<int, string> idToTags)
        {
            if (originalText == null) throw new ArgumentNullException(nameof(originalText));
            if (pieces == null) throw new ArgumentNullException(nameof(pieces));
            if (idToTags == null) throw new ArgumentNullException(nameof(idToTags));

            var sb = new StringBuilder(originalText.Length + idToTags.Count * 12);

            string? currentTag = null;

            foreach (var piece in pieces)
            {
                if (piece.IsTaggable && piece.LineId.HasValue)
                {
                    if (!idToTags.TryGetValue(piece.LineId.Value, out var desired) || string.IsNullOrWhiteSpace(desired))
                    {
                        throw new InvalidOperationException($"Missing tags for line {piece.LineId.Value:000}.");
                    }

                    if (!string.Equals(currentTag, desired, StringComparison.Ordinal))
                    {
                        sb.Append(desired);
                        currentTag = desired;
                    }
                }

                sb.Append(piece.Text);
            }

            return sb.ToString();
        }

        public static string ApplyTags(string originalText, IReadOnlyList<Piece> pieces, IReadOnlyDictionary<int, string> idToTags)
        {
            if (originalText == null) throw new ArgumentNullException(nameof(originalText));
            if (pieces == null) throw new ArgumentNullException(nameof(pieces));
            if (idToTags == null) throw new ArgumentNullException(nameof(idToTags));

            var sb = new StringBuilder(originalText.Length + idToTags.Count * 20);

            foreach (var piece in pieces)
            {
                if (piece.IsTaggable && piece.LineId.HasValue)
                {
                    if (!idToTags.TryGetValue(piece.LineId.Value, out var tags) || string.IsNullOrWhiteSpace(tags))
                    {
                        throw new InvalidOperationException($"Missing tags for line {piece.LineId.Value:000}.");
                    }

                    // IMPORTANT: do not add any extra whitespace/newlines. Only prepend tags.
                    sb.Append(tags);
                }

                sb.Append(piece.Text);
            }

            return sb.ToString();
        }

        public static string StripInsertedTags(string taggedText)
        {
            if (taggedText == null) return string.Empty;
            return StripTagsRegex.Replace(taggedText, string.Empty);
        }

        private static List<Piece> SegmentIntoPieces(string input)
        {
            var pieces = new List<Piece>();
            var current = new StringBuilder();

            char? quoteCloser = null;

            void FlushTaggable()
            {
                if (current.Length == 0) return;
                pieces.Add(new Piece(current.ToString(), IsTaggable: true, LineId: null));
                current.Clear();
            }

            for (int i = 0; i < input.Length; i++)
            {
                var c = input[i];

                // Heuristic: a leading straight quote followed by whitespace and a lowercase letter
                // (e.g., " sibilò...") is very likely a closing quote that should belong to the previous dialogue line.
                // Emit it as a standalone quote piece so BuildNumberedLines can append it to the previous numbered line.
                if (quoteCloser == null && IsLineStart(input, i) && c == '"')
                {
                    int j = i + 1;
                    while (j < input.Length && (input[j] == ' ' || input[j] == '\t')) j++;
                    if (j < input.Length && char.IsLetter(input[j]) && char.IsLower(input[j]))
                    {
                        FlushTaggable();
                        pieces.Add(new Piece("\"", IsTaggable: true, LineId: null));
                        continue;
                    }
                }

                // If a closing quote appears at the start of a line (e.g., "\n” disse...")
                // isolate it into its own piece so we never produce a numbered line that is just a quote.
                // BuildNumberedLines will append this quote to the previous numbered line for the agent view.
                if (quoteCloser == null && IsLineStart(input, i) && (c == '”' || c == '»'))
                {
                    FlushTaggable();
                    pieces.Add(new Piece(c.ToString(), IsTaggable: true, LineId: null));
                    continue;
                }

                // Quote spans: keep everything between quotes in a single taggable piece.
                // Always split (flush) immediately after the closing quote to separate speech from narration.
                if (quoteCloser == null)
                {
                    if (c == '"')
                    {
                        FlushTaggable();
                        quoteCloser = '"';
                        current.Append(c);
                        continue;
                    }
                    if (c == '“')
                    {
                        FlushTaggable();
                        quoteCloser = '”';
                        current.Append(c);
                        continue;
                    }
                    if (c == '«')
                    {
                        FlushTaggable();
                        quoteCloser = '»';
                        current.Append(c);
                        continue;
                    }
                }
                else
                {
                    if (c == quoteCloser.Value)
                    {
                        current.Append(c);
                        FlushTaggable();
                        quoteCloser = null;
                        continue;
                    }
                }

                // Preserve newlines exactly as non-taggable pieces.
                if (c == '\r')
                {
                    FlushTaggable();
                    if (i + 1 < input.Length && input[i + 1] == '\n')
                    {
                        pieces.Add(new Piece("\r\n", IsTaggable: false, LineId: null));
                        i++;
                    }
                    else
                    {
                        pieces.Add(new Piece("\r", IsTaggable: false, LineId: null));
                    }
                    continue;
                }
                if (c == '\n')
                {
                    FlushTaggable();
                    pieces.Add(new Piece("\n", IsTaggable: false, LineId: null));
                    continue;
                }

                // While inside quotes, do not split on punctuation/markers.
                if (quoteCloser != null)
                {
                    current.Append(c);
                    continue;
                }

                // Dialogue apostrophe: if it looks like a leading speech marker, start a new piece.
                if (c == '\'' && IsDialogueApostrophe(input, i))
                {
                    FlushTaggable();
                    current.Append(c);
                    continue;
                }

                current.Append(c);

                // Split after punctuation (keeping punctuation in the current piece).
                if (c == '.')
                {
                    var next = i + 1 < input.Length ? input[i + 1] : '\0';
                    if (next != '\n' && next != '\r' && next != '\0')
                    {
                        FlushTaggable();
                    }
                    continue;
                }

                if (SplitAfterChars.Contains(c))
                {
                    FlushTaggable();
                    continue;
                }
            }

            FlushTaggable();
            return pieces;
        }

        private static bool IsDialogueApostrophe(string text, int index)
        {
            // Heuristic: apostrophe used as dialogue marker often appears at start of a line/after newline,
            // and is followed by a letter.
            var prev = index > 0 ? text[index - 1] : '\n';
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            bool startsLine = prev == '\n' || prev == '\r';
            bool followedByLetter = next != '\0' && char.IsLetter(next);
            return startsLine && followedByLetter;
        }

        private static bool IsAllowedTagSet(string tags)
        {
            if (string.IsNullOrWhiteSpace(tags)) return false;

            // Exact narrator-only.
            if (tags.Trim().Equals("[NARRATORE]", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Allow:
            // - [PERSONAGGIO: X] [EMOZIONE: y]
            // - [PERSONAGGIO: X]
            // (legacy aliases can be normalized later)
            if (!tags.Contains("[PERSONAGGIO:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static string NormalizeTags(string tags)
        {
            // Normalize legacy aliases (SENTIMENTO -> EMOZIONE).
            return Regex.Replace(
                tags,
                @"\[\s*SENTIMENTO\s*:\s*(?<v>[^\]]+?)\s*\]",
                m => $"[EMOZIONE: {m.Groups["v"].Value.Trim()}]",
                RegexOptions.IgnoreCase);
        }
    }
}
