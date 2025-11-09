using Microsoft.SemanticKernel;

namespace TinyGenerator.Skills
{
    public class TextPlugin
    {
    public string? LastCalled { get; set; }

        [KernelFunction("toupper")]
        public string ToUpper(string input) { LastCalled = nameof(ToUpper); return input.ToUpperInvariant(); }

        [KernelFunction("tolower")]
        public string ToLower(string input) { LastCalled = nameof(ToLower); return input.ToLowerInvariant(); }

        [KernelFunction("trim")]
        public string Trim(string input) { LastCalled = nameof(Trim); return input.Trim(); }

        [KernelFunction("length")]
        public int Length(string input) { LastCalled = nameof(Length); return input?.Length ?? 0; }

        [KernelFunction("substring")]
        public string Substring(string input, int startIndex, int length) { LastCalled = nameof(Substring); return input.Substring(startIndex, length); }

        [KernelFunction("join")]
        public string Join(string[] input, string separator) { LastCalled = nameof(Join); return string.Join(separator, input); }

        [KernelFunction("split")]
        public string[] Split(string input, string separator) { LastCalled = nameof(Split); return input.Split(separator); }
    }
}
