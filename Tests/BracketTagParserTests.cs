using TinyGenerator.Services.Text;
using Xunit;

namespace TinyGenerator.Tests;

public sealed class BracketTagParserTests
{
    [Fact]
    public void TryGetTagContent_SupportsClosedTags()
    {
        var text = "[IS_VALID]true[/IS_VALID]\n[REASON]ok[/REASON]";
        Assert.True(BracketTagParser.TryGetTagContent(text, "IS_VALID", out var v));
        Assert.Equal("true", v);

        Assert.True(BracketTagParser.TryGetTagContent(text, "REASON", out var r));
        Assert.Equal("ok", r);
    }

    [Fact]
    public void TryGetTagContent_SupportsOpenOnlyTags_UntilNextTagLine()
    {
        var text = "[IS_VALID]true\n[NEEDS_RETRY]false\n[REASON]All good\n[VIOLATED_RULES]\n";

        Assert.True(BracketTagParser.TryGetTagContent(text, "IS_VALID", out var isValid));
        Assert.Equal("true", isValid);

        Assert.True(BracketTagParser.TryGetTagContent(text, "NEEDS_RETRY", out var needsRetry));
        Assert.Equal("false", needsRetry);

        Assert.True(BracketTagParser.TryGetTagContent(text, "REASON", out var reason));
        Assert.Equal("All good", reason);

        Assert.True(BracketTagParser.TryGetTagContent(text, "VIOLATED_RULES", out var violated));
        Assert.Equal(string.Empty, violated);
    }

    [Fact]
    public void TryGetTagContent_AllowsColonSeparator_AfterTag()
    {
        var text =
            "[IS_VALID]: true\n"
            + "[NEEDS_RETRY]: false\n"
            + "[REASON]: ok\n"
            + "[VIOLATED_RULES]: // vuoto\n";

        Assert.True(BracketTagParser.TryGetTagContent(text, "IS_VALID", out var isValid));
        Assert.Equal("true", isValid);

        Assert.True(BracketTagParser.TryGetTagContent(text, "NEEDS_RETRY", out var needsRetry));
        Assert.Equal("false", needsRetry);

        Assert.True(BracketTagParser.TryGetTagContent(text, "REASON", out var reason));
        Assert.Equal("ok", reason);

        Assert.True(BracketTagParser.TryGetTagContent(text, "VIOLATED_RULES", out var violated));
        Assert.Equal("// vuoto", violated);
    }
}
