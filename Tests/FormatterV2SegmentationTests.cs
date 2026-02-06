using TinyGenerator.Services;
using Xunit;

namespace TinyGenerator.Tests;

public sealed class FormatterV2SegmentationTests
{
    [Fact]
    public void BuildNumberedLines_DoesNotEmitStandaloneDoubleQuoteLine()
    {
        var input = "Intro.\n\"\nTest.";

        var build = FormatterV2.BuildNumberedLines(input);

        var lines = build.NumberedLines.Replace("\r\n", "\n").Split('\n');
        Assert.DoesNotContain(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^\\d{3}\\s+\"\\s*$"));
    }

    [Fact]
    public void BuildNumberedLines_DoesNotEmitStandaloneCurlyQuoteLine()
    {
        var input = "Non preoccuparti,\n”\n disse in tono placido.";

        var build = FormatterV2.BuildNumberedLines(input);
        var lines = build.NumberedLines.Replace("\r\n", "\n").Split('\n');

        Assert.DoesNotContain(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^\\d{3}\\s+”\\s*$"));
    }

    [Fact]
    public void BuildNumberedLines_DoesNotStartNumberedLineWithClosingCurlyQuote()
    {
        var input = "“Non preoccuparti,\n” disse in tono placido.";

        var build = FormatterV2.BuildNumberedLines(input);
        var lines = build.NumberedLines.Replace("\r\n", "\n").Split('\n');

        Assert.DoesNotContain(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^\\d{3}\\s+”"));
    }

    [Fact]
    public void BuildNumberedLines_MovesOpeningStraightQuoteAfterColonToNextLine()
    {
        // This is the common bad OCR/layout split: opening quote is on the previous line after ':'
        // but the dialogue text starts on the next line.
        var input = "Luca parlò con voce neutra: \"\nNon lo so.\"";

        var build = FormatterV2.BuildNumberedLines(input);
        var lines = build.NumberedLines.Replace("\r\n", "\n").Split('\n');

        // The narration line should not end with a stray opening quote.
        Assert.DoesNotContain(lines, l => l.Contains("voce neutra: \""));

        // The dialogue line must include the opening quote and the dialogue text.
        Assert.Contains(lines, l => l.Contains("\"Non lo so."));
    }

    [Fact]
    public void BuildNumberedLines_IsolatesCurlyQuoteDialogueSpansFromNarration()
    {
        var input =
            "La figura si avvicinò, affondando le dita nella carne della guancia di Luca e trascinandolo verso un angolo buio. “Sei l’unico che può risolvere questo puzzle,” ansimò l’uomo. “Se non hai la chiave, dovrai crearne una nuova.”\n" +
            "Luca cercò di reagire, ma la mano dell’altro lo tratteneva come una morsa.\n" +
            "“Non puoi farlo da solo,” disse l’uomo tra i denti. “Ma con me come guida, potresti farcela.”";

        var build = FormatterV2.BuildNumberedLines(input);
        var lines = build.NumberedLines.Replace("\r\n", "\n").Split('\n');

        Assert.Contains(lines, l => l.Contains("“Sei l’unico che può risolvere questo puzzle,”") && l.TrimEnd().EndsWith("”"));
        Assert.Contains(lines, l => l.Contains("“Se non hai la chiave, dovrai crearne una nuova.”") && l.TrimEnd().EndsWith("”"));
        Assert.Contains(lines, l => l.Contains("“Non puoi farlo da solo,”") && l.TrimEnd().EndsWith("”"));
        Assert.Contains(lines, l => l.Contains("“Ma con me come guida, potresti farcela.”") && l.TrimEnd().EndsWith("”"));

        // Dialogue spans must not be merged with the following narration.
        Assert.DoesNotContain(lines, l => l.Contains("puzzle,”") && l.Contains("ansimò"));
        Assert.DoesNotContain(lines, l => l.Contains("da solo,”") && l.Contains("disse"));
    }

    [Fact]
    public void BuildNumberedLines_MovesLeadingStraightClosingQuoteOffNarrationLine()
    {
        var input = "\"Sei proprio tu?\"\n\" sibilò una voce roca.";

        var build = FormatterV2.BuildNumberedLines(input);
        var lines = build.NumberedLines.Replace("\r\n", "\n").Split('\n');

        // No numbered line should start with a straight quote followed by whitespace and lowercase narration.
        Assert.DoesNotContain(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, "^\\d{3}\\s+\\\"\\s+[a-zàèéìòù]"));
    }

    [Fact]
    public void BuildNumberedLines_KeepsQuoteDotQuoteOnSameLine()
    {
        var input = "\"Ciao.\"";

        var build = FormatterV2.BuildNumberedLines(input);

        Assert.Equal(1, build.TaggableLineCount);
        Assert.Equal("001 \"Ciao.\"", build.NumberedLines);
    }

    [Fact]
    public void BuildNumberedLines_DoesNotSplitInsideDoubleQuotes()
    {
        var input = "Mario disse \"Ciao, come va? Bene.\" e uscì.";

        var build = FormatterV2.BuildNumberedLines(input);

        Assert.True(build.TaggableLineCount >= 1);

        // The quoted span must appear as a single numbered line (not split by comma/period inside quotes).
        var lines = build.NumberedLines.Replace("\r\n", "\n").Split('\n');
        var linesWithQuoteSpan = lines.Count(l => l.Contains("\"Ciao, come va? Bene.\""));
        Assert.Equal(1, linesWithQuoteSpan);

        // Additionally, there should be exactly one numbered line containing any double quote.
        var numberedLinesWithAnyQuote = lines.Count(l => l.Length >= 5 && char.IsDigit(l[0]) && char.IsDigit(l[1]) && char.IsDigit(l[2]) && l[3] == ' ' && l.Contains('"'));
        Assert.Equal(1, numberedLinesWithAnyQuote);
    }

    [Fact]
    public void ParseIdToTagsMapping_IgnoresTrailingTextAfterTags()
    {
        var mapping =
            "004 [PERSONAGGIO: Luca] [EMOZIONE: paura] (nota extra)\n";

        var parsed = FormatterV2.ParseIdToTagsMapping(mapping);

        Assert.Equal("[PERSONAGGIO: Luca] [EMOZIONE: paura]", parsed[4]);
    }

    [Fact]
    public void ExpandSparseMapping_PropagatesTagsForward()
    {
        var sparse = new Dictionary<int, string>
        {
            [3] = "[PERSONAGGIO: Luca] [EMOZIONE: paura]"
        };

        var full = FormatterV2.ExpandSparseMapping(5, sparse);

        Assert.Equal("[NARRATORE]", full[1]);
        Assert.Equal("[NARRATORE]", full[2]);
        Assert.Equal("[PERSONAGGIO: Luca] [EMOZIONE: paura]", full[3]);
        Assert.Equal("[NARRATORE]", full[4]);
        Assert.Equal("[NARRATORE]", full[5]);
    }

    [Fact]
    public void ExpandSparseMapping_EmptyMappingDefaultsToNarrator()
    {
        var sparse = new Dictionary<int, string>();

        var full = FormatterV2.ExpandSparseMapping(3, sparse);

        Assert.Equal("[NARRATORE]", full[1]);
        Assert.Equal("[NARRATORE]", full[2]);
        Assert.Equal("[NARRATORE]", full[3]);
    }

    [Fact]
    public void ApplySectionTags_InsertsTagsOnlyOnSpeakerChanges()
    {
        var input = "A. \"Ciao.\" B.";

        var build = FormatterV2.BuildNumberedLines(input);

        // We tag only the dialogue line; all other lines are implicit narrator.
        var sparse = new Dictionary<int, string>();
        foreach (var line in build.NumberedLines.Split('\n'))
        {
            if (line.Contains("\"Ciao."))
            {
                var id = int.Parse(line.Substring(0, 3));
                sparse[id] = "[PERSONAGGIO: Luca] [EMOZIONE: sereno]";
            }
        }

        var full = FormatterV2.ExpandSparseMapping(build.TaggableLineCount, sparse);
        var tagged = FormatterV2.ApplySectionTags(input, build.Pieces, full);

        // Should start with narrator (implicit) and then switch to character once, then back to narrator.
        Assert.Contains("[NARRATORE]", tagged);
        Assert.Contains("[PERSONAGGIO: Luca] [EMOZIONE: sereno]", tagged);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(tagged, "\\[NARRATORE\\]").Count);
    }
}
