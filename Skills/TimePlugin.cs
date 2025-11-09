using System;
using Microsoft.SemanticKernel;

namespace TinyGenerator.Skills
{
    public class TimePlugin
    {
    public string? LastCalled { get; set; }

        [KernelFunction("now")]
        public string Now() { LastCalled = nameof(Now); return DateTime.UtcNow.ToString("o"); }

        [KernelFunction("today")]
        public string Today() { LastCalled = nameof(Today); return DateTime.UtcNow.ToString("yyyy-MM-dd"); }

        [KernelFunction("adddays")]
        public string AddDays(string date, int days)
        {
            LastCalled = nameof(AddDays);
            if (DateTime.TryParse(date, out var dt))
                return dt.AddDays(days).ToString("yyyy-MM-dd");
            return "";
        }

        [KernelFunction("addhours")]
        public string AddHours(string time, int hours)
        {
            LastCalled = nameof(AddHours);
            if (TimeSpan.TryParse(time, out var ts))
                return ts.Add(TimeSpan.FromHours(hours)).ToString(@"hh\:mm");
            if (DateTime.TryParse(time, out var dt))
                return dt.AddHours(hours).ToString("HH:mm");
            return "";
        }
    }
}
