using Microsoft.SemanticKernel;

namespace TinyGenerator.Skills
{
    public class MathPlugin
    {
    public string? LastCalled { get; set; }

        [KernelFunction("add")]
        public double Add(double a, double b) { LastCalled = nameof(Add); return a + b; }

        [KernelFunction("subtract")]
        public double Subtract(double a, double b) { LastCalled = nameof(Subtract); return a - b; }

        [KernelFunction("multiply")]
        public double Multiply(double a, double b) { LastCalled = nameof(Multiply); return a * b; }

        [KernelFunction("divide")]
        public double Divide(double a, double b) { LastCalled = nameof(Divide); return a / b; }
    }
}
