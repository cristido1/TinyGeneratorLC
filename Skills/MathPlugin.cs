using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace TinyGenerator.Skills
{
    [Description("Provides basic arithmetic operations such as add, subtract, multiply, and divide.")]
    public class MathPlugin
    {
        public string? LastCalled { get; set; }

        [KernelFunction("add"),Description("Adds two numbers and returns the result.")]
        public double Add([Description("The first number.")] double a, [Description("The second number.")] double b) { LastCalled = nameof(Add); return a + b; }

        [KernelFunction("subtract"),Description("Subtracts the second number from the first and returns the result.")]
        public double Subtract([Description("The first number.")] double a, [Description("The second number.")] double b) { LastCalled = nameof(Subtract); return a - b; }

        [KernelFunction("multiply"),Description("Multiplies two numbers and returns the result.")]
        public double Multiply([Description("The first number.")] double a, [Description("The second number.")] double b) { LastCalled = nameof(Multiply); return a * b; }

        [KernelFunction("divide"), Description("Divides the first number by the second and returns the result.")]
        public double Divide([Description("The first number.")] double a, [Description("The second number.")] double b) { LastCalled = nameof(Divide); return a / b; }
        [KernelFunction("describe"), Description("Describes the available math operations.")]
        public string Describe() =>
            "Available functions: add(a,b), subtract(a,b), multiply(a,b), divide(a,b). " +
            "Example: math.add(5,3) returns 8.";
    }
}
