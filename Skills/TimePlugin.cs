using System;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace TinyGenerator.Skills
{
    [Description("Provides date and time related functions.")]
    public class TimePlugin
    {
    public string? LastCalled { get; set; }

        [KernelFunction("now"),Description("Gets the current date and time.")]
        public string Now() { LastCalled = nameof(Now); return DateTime.UtcNow.ToString("o"); }

        [KernelFunction("today"),Description("Gets the current date.")]
        public string Today() { LastCalled = nameof(Today); return DateTime.UtcNow.ToString("yyyy-MM-dd"); }

        [KernelFunction("adddays"),Description("Adds a specified number of days to a date.")]
        public string AddDays([Description("The date to add days to.")] string date, [Description("The number of days to add.")] int days)
        {
            LastCalled = nameof(AddDays);
            if (DateTime.TryParse(date, out var dt))
                return dt.AddDays(days).ToString("yyyy-MM-dd");
            return "";
        }

        [KernelFunction("addhours"), Description("Adds a specified number of hours to a time.")]
        public string AddHours([Description("The time to add hours to.")] string time, [Description("The number of hours to add.")] int hours)
        {
            LastCalled = nameof(AddHours);
            if (TimeSpan.TryParse(time, out var ts))
                return ts.Add(TimeSpan.FromHours(hours)).ToString(@"hh\:mm");
            if (DateTime.TryParse(time, out var dt))
                return dt.AddHours(hours).ToString("HH:mm");
            return "";
        }
        [KernelFunction("describe"), Description("Describes the available time functions.")]
        public string Describe() =>
            "Available functions: now(), today(), adddays(date, days), addhours(time, hours). " +
            "Example: time.now() returns the current date and time in ISO 8601 format.";
    }
}
