using Microsoft.SemanticKernel;
using System.ComponentModel;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    [Description("Provides time-related functions such as getting the current date and time, and date arithmetic.")]
    public class TimeSkill : ITinySkill
    {
        private readonly ICustomLogger? _logger;
        private int? _modelId;
        private string? _modelName;
        private DateTime? _lastCalled;
        private string? _lastFunction;

        // ITinySkill implementation
        int? ITinySkill.ModelId { get => _modelId; set => _modelId = value; }
        string? ITinySkill.ModelName { get => _modelName; set => _modelName = value; }
        int? ITinySkill.AgentId => null;
        string? ITinySkill.AgentName => null;
        DateTime? ITinySkill.LastCalled { get => _lastCalled; set => _lastCalled = value; }
        string? ITinySkill.LastFunction { get => _lastFunction; set => _lastFunction = value; }
        ICustomLogger? ITinySkill.Logger { get => _logger; set { } }

        public TimeSkill(ICustomLogger? logger = null)
        {
            _logger = logger;
        }

        [KernelFunction("now"), Description("Gets the current date and time.")]
        public string Now()
        {
            ((ITinySkill)this).LogFunctionCall("now");
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        [KernelFunction("today"), Description("Gets today's date.")]
        public string Today()
        {
            ((ITinySkill)this).LogFunctionCall("today");
            return DateTime.Now.ToString("yyyy-MM-dd");
        }

        [KernelFunction("adddays"), Description("Adds days to the current date.")]
        public string AddDays([Description("The number of days to add.")] int days)
        {
            ((ITinySkill)this).LogFunctionCall("adddays");
            return DateTime.Now.AddDays(days).ToString("yyyy-MM-dd");
        }

        [KernelFunction("addhours"), Description("Adds hours to the current time.")]
        public string AddHours([Description("The number of hours to add.")] int hours)
        {
            ((ITinySkill)this).LogFunctionCall("addhours");
            return DateTime.Now.AddHours(hours).ToString("yyyy-MM-dd HH:mm:ss");
        }

        [KernelFunction("describe"), Description("Describes the available time functions.")]
        public string Describe()
        {
            ((ITinySkill)this).LogFunctionCall("describe");
            return "Available functions: now(), today(), adddays(days), addhours(hours). " +
                "Example: now() returns the current date and time, " +
                "adddays(5) returns a date 5 days in the future.";
        }
    }
}
