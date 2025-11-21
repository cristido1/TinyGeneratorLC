using Microsoft.SemanticKernel;
using System.ComponentModel;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    [Description("Provides math functions such as add, subtract, multiply, and divide.")]
    public class MathSkill : ITinySkill
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

        public MathSkill(ICustomLogger? logger = null)
        {
            _logger = logger;
        }

        [KernelFunction("add"), Description("Adds two numbers.")]
        public double Add([Description("The first number.")] double a, [Description("The second number.")] double b)
        {
            ((ITinySkill)this).LogFunctionCall("add");
            return a + b;
        }

        [KernelFunction("subtract"), Description("Subtracts two numbers.")]
        public double Subtract([Description("The first number.")] double a, [Description("The second number.")] double b)
        {
            ((ITinySkill)this).LogFunctionCall("subtract");
            return a - b;
        }

        [KernelFunction("multiply"), Description("Multiplies two numbers.")]
        public double Multiply([Description("The first number.")] double a, [Description("The second number.")] double b)
        {
            ((ITinySkill)this).LogFunctionCall("multiply");
            return a * b;
        }

        [KernelFunction("divide"), Description("Divides two numbers.")]
        public double Divide([Description("The first number.")] double a, [Description("The second number.")] double b)
        {
            ((ITinySkill)this).LogFunctionCall("divide");
            if (b == 0) throw new ArgumentException("Division by zero is not allowed.");
            return a / b;
        }

        [KernelFunction("describe"), Description("Describes the available math functions.")]
        public string Describe()
        {
            ((ITinySkill)this).LogFunctionCall("describe");
            return "Available functions: add(a, b), subtract(a, b), multiply(a, b), divide(a, b). " +
                "Example: add(2, 3) returns 5.";
        }
    }
}
