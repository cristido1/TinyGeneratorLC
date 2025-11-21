namespace TinyGenerator.Services
{
    /// <summary>
    /// DEPRECATED: Semantic Kernel factory interface (legacy)
    /// 
    /// This interface creates SK Kernel instances with plugins.
    /// Used by legacy services like TestService, AgentService, etc.
    /// 
    /// Migration Path:
    /// - Migrate to LangChainKernelFactory for new code
    /// - Use HybridLangChainOrchestrator instead of Kernel
    /// - Use LangChainToolFactory for tool management
    /// 
    /// Will be removed once all consumers are migrated to LangChain.
    /// </summary>
    [Obsolete("IKernelFactory uses deprecated Semantic Kernel. Use ILangChainKernelFactory instead.", false)]
    public interface IKernelFactory
    {
        Microsoft.SemanticKernel.Kernel CreateKernel(string? model = null, System.Collections.Generic.IEnumerable<string>? allowedPlugins = null, int? agentId = null, string? ttsStoryText = null, string? workingFolder = null);
    }
}

