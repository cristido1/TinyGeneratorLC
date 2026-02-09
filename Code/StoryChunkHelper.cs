using System;
using System.Collections.Generic;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Utility to split a story into chunks without breaking sentences abruptly.
    /// Tries to cut at sentence boundaries ('.', '!' '?') or newlines.
    /// </summary>
    public static class StoryChunkHelper
    {
        public static List<string> SplitIntoChunks(string storyText, int targetSize = 1000, int boundaryWindow = 150)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(storyText))
                return chunks;

            int len = storyText.Length;
            int start = 0;
            while (start < len)
            {
                int end = Math.Min(start + targetSize, len);
                int maxSeek = Math.Min(len, end + boundaryWindow);
                int minSeek = Math.Max(start, end - boundaryWindow);

                // Prefer newline boundaries, then sentence punctuation, then whitespace to avoid cutting words
                int boundaryIndex = FindLastNewlineBetween(storyText, minSeek, Math.Min(maxSeek, len));
                if (boundaryIndex < 0)
                {
                    boundaryIndex = FindLastPunctuationBetween(storyText, minSeek, Math.Min(maxSeek, len));
                }
                if (boundaryIndex < 0)
                {
                    boundaryIndex = FindLastWhitespaceBetween(storyText, minSeek, Math.Min(maxSeek, len));
                }

                // If we found a reasonable boundary after start, use it; otherwise fall back to the original end
                if (boundaryIndex > start)
                    end = boundaryIndex;

                // Ensure we make progress; if end == start (shouldn't happen), advance by targetSize
                if (end <= start)
                {
                    end = Math.Min(start + targetSize, len);
                }

                var chunk = storyText.Substring(start, end - start);
                chunks.Add(chunk);
                start = end;
            }

            return chunks;
        }

        private static int FindLastNewlineBetween(string text, int startInclusive, int endExclusive)
        {
            for (int i = Math.Min(endExclusive, text.Length) - 1; i >= startInclusive; i--)
            {
                char c = text[i];
                if (c == '\n' || c == '\r') return i + 1; // include newline
            }
            return -1;
        }

        private static int FindLastPunctuationBetween(string text, int startInclusive, int endExclusive)
        {
            for (int i = Math.Min(endExclusive, text.Length) - 1; i >= startInclusive; i--)
            {
                char c = text[i];
                if (c == '.' || c == '!' || c == '?') return i + 1; // include punctuation
            }
            return -1;
        }

        private static int FindLastWhitespaceBetween(string text, int startInclusive, int endExclusive)
        {
            for (int i = Math.Min(endExclusive, text.Length) - 1; i >= startInclusive; i--)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) return i + 1; // include the whitespace
            }
            return -1;
        }
    }
}
