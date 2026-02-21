using Microsoft.Extensions.Options;

namespace TinyGenerator.Services;

public abstract class CheckBase : IDeterministicCheck
{
    public abstract string Rule { get; }
    public virtual string GenericErrorDescription => Rule;
    public IOptions<object>? Options { get; set; }

    public abstract IDeterministicResult Execute(string textToCheck);

    protected T GetOption<T>(string key, T defaultValue = default!)
    {
        var raw = GetOptionRaw(key);
        if (raw == null)
        {
            return defaultValue;
        }

        if (raw is T typed)
        {
            return typed;
        }

        try
        {
            if (typeof(T) == typeof(string))
            {
                return (T)(object)(raw.ToString() ?? string.Empty);
            }

            if (raw is IConvertible)
            {
                return (T)Convert.ChangeType(raw, typeof(T));
            }
        }
        catch
        {
            // ignore conversion errors, fallback below
        }

        return defaultValue;
    }

    protected object? GetOptionRaw(string key)
    {
        var value = Options?.Value;
        if (value is IDictionary<string, object> dict && dict.TryGetValue(key, out var v))
        {
            return v;
        }

        if (value is IReadOnlyDictionary<string, object> readOnly && readOnly.TryGetValue(key, out var v2))
        {
            return v2;
        }

        return null;
    }
}
