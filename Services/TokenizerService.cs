using System;

namespace TinyGenerator.Services;

public interface ITokenizer
{
    int CountTokens(string text);
    bool IsPrecise { get; }
}

/// <summary>
/// Tokenizer service: attempts to load a native tokenizer library (e.g. Tiktoken.Net)
/// via reflection at runtime. If the library is not available, falls back to a
/// conservative heuristic (chars/4).
/// This keeps the project free of hard NuGet tokenizer dependencies while
/// providing precise counting when the user installs a tokenizer package.
/// </summary>
public sealed class TokenizerService : ITokenizer, IDisposable
{
    private readonly Func<string, int> _countFunc;
    public bool IsPrecise { get; }

    public TokenizerService(string model = "cl100k_base")
    {
        // Try to load Tiktoken.Net (or similar) by reflection. This avoids a hard
        // dependency in the csproj while supporting precise token counting if the
        // user chooses to install the package.
        try
        {
            var asm = AppDomain.CurrentDomain.Load("Tiktoken.Net");
            var encType = asm.GetType("Tiktoken.Encoding") ?? asm.GetType("Tiktoken.EncodingFactory");
            if (encType != null)
            {
                var getEnc = encType.GetMethod("GetEncoding", new[] { typeof(string) }) ?? encType.GetMethod("Create", new[] { typeof(string) });
                if (getEnc != null)
                {
                    var encoder = getEnc.Invoke(null, new object[] { model });
                    if (encoder != null)
                    {
                        var encodeMethod = encoder.GetType().GetMethod("Encode", new[] { typeof(string) }) ?? encoder.GetType().GetMethod("Encode", new[] { typeof(ReadOnlySpan<char>) });
                        if (encodeMethod != null)
                        {
                            _countFunc = text =>
                            {
                                var encoded = encodeMethod.Invoke(encoder, new object[] { text });
                                if (encoded == null) return Math.Max(1, text.Length / 4);
                                var countProp = encoded.GetType().GetProperty("Count") ?? encoded.GetType().GetProperty("Length");
                                if (countProp != null)
                                {
                                    return Convert.ToInt32(countProp.GetValue(encoded));
                                }
                                return Math.Max(1, text.Length / 4);
                            };
                            IsPrecise = true;
                            return;
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore - fallthrough to heuristic
        }

        // Fallback heuristic
        _countFunc = text => Math.Max(1, text?.Length / 4 ?? 1);
        IsPrecise = false;
    }

    public int CountTokens(string text) => _countFunc(text);

    public void Dispose()
    {
        // nothing to dispose; if a loaded encoder requires disposal we could
        // implement it via reflection here.
    }
}
