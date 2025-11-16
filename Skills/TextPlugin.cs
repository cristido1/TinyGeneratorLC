using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace TinyGenerator.Skills
{
    [Description("Provides various text manipulation functions.")]
    public class TextPlugin
    {
        public string? LastCalled { get; set; }

        [KernelFunction("toupper"), Description("Converts a string to uppercase.")]
        public string ToUpper([Description("The string to convert to uppercase.")] string input) { LastCalled = nameof(ToUpper); return input.ToUpperInvariant(); }

        [KernelFunction("tolower"), Description("Converts a string to lowercase.")]
        public string ToLower([Description("The string to convert to lowercase.")] string input) { LastCalled = nameof(ToLower); return input.ToLowerInvariant(); }

        [KernelFunction("trim"), Description("Trims whitespace from the start and end of a string.")]
        public string Trim([Description("The string to trim whitespace from.")] string input) { LastCalled = nameof(Trim); return input.Trim(); }

        [KernelFunction("length"), Description("Gets the length of a string.")]
        public int Length([Description("The string to get the length of.")] string input) { LastCalled = nameof(Length); return input?.Length ?? 0; }

        [KernelFunction("substring"), Description("Extracts a substring from a string.")]
        public string Substring([Description("The string to extract the substring from.")] string input, [Description("The zero-based starting index of the substring.")] int startIndex, [Description("The length of the substring.")] int length)
        {
            LastCalled = nameof(Substring);
            if (string.IsNullOrEmpty(input)) return string.Empty;
            if (startIndex < 0) startIndex = 0;
            if (length <= 0) return string.Empty;
            if (startIndex >= input.Length) return string.Empty;
            // Clamp length to available characters
            if (startIndex + length > input.Length) length = input.Length - startIndex;
            return input.Substring(startIndex, length);
        }

        [KernelFunction("join"), Description("Joins an array of strings into a single string with a separator.")]
        public string Join([Description("The array of strings to join.")] string[] input, [Description("The separator to use.")] string separator) { LastCalled = nameof(Join); return string.Join(separator, input); }

        [KernelFunction("split"), Description("Splits a string into an array of strings using a separator.")]
        public string[] Split([Description("The string to split.")] string input, [Description("The separator to use.")] string separator) { LastCalled = nameof(Split); return input.Split(separator); }
    
        [KernelFunction("describe"), Description("Describes the available text manipulation functions.")]
        public string Describe() =>
            "Available functions: toupper(input), tolower(input), trim(input), length(input), substring(input, startIndex, length), join(input[], separator), split(input, separator). " +
            "Example: text.toupper('hello') returns 'HELLO'.";
    }
}