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
        public static List<string> SplitIntoChunks(string storyText, int targetSize = 1800, int boundaryWindow = 200)
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

                int boundaryIndex = FindBoundary(storyText, minSeek, end);
                if (boundaryIndex < 0 && end < len)
                {
                    // Try looking forward a bit if no boundary behind
                    boundaryIndex = FindBoundary(storyText, end, maxSeek);
                }

                if (boundaryIndex > start)
                    end = boundaryIndex;

                var chunk = storyText.Substring(start, end - start);
                chunks.Add(chunk);
                start = end;
            }

            return chunks;
        }

        // Find last boundary ('.', '!', '?', newline) between startInclusive and endExclusive
        private static int FindBoundary(string text, int startInclusive, int endExclusive)
        {
            int boundary = -1;
            for (int i = startInclusive; i < endExclusive && i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n' || c == '\r' || c == '.' || c == '!' || c == '?')
                {
                    boundary = i + 1; // include the boundary char
                }
            }
            return boundary;
        }
    }
}
