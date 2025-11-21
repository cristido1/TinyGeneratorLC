namespace TinyGenerator.Services
{
    public interface IKernelFactory
    {
        Microsoft.SemanticKernel.Kernel CreateKernel(string? model = null, System.Collections.Generic.IEnumerable<string>? allowedPlugins = null, int? agentId = null, string? ttsStoryText = null, string? workingFolder = null);
    }
}
