namespace TinyGenerator.Services
{
    /// <summary>
    /// LangChain-based kernel factory interface (new, replaces deprecated IKernelFactory)
    /// 
    /// Creates HybridLangChainOrchestrator instances instead of Semantic Kernel Kernels.
    /// Orchestrators manage tools and handle explicit ReAct loop execution.
    /// 
    /// This is the new standard interface for agent and test infrastructure.
    /// </summary>
    public interface ILangChainKernelFactory
    {
        /// <summary>
        /// Create a HybridLangChainOrchestrator with tools for a given model
        /// </summary>
        /// <param name="model">Model name (e.g., "gpt-3.5-turbo", "phi3:mini")</param>
        /// <param name="allowedPlugins">Filter tools by name (optional)</param>
        /// <param name="agentId">Agent ID for caching (optional)</param>
        /// <returns>Configured orchestrator ready for use</returns>
        HybridLangChainOrchestrator CreateOrchestrator(
            string? model = null,
            System.Collections.Generic.IEnumerable<string>? allowedPlugins = null,
            int? agentId = null);

        /// <summary>
        /// Get a cached orchestrator for an agent
        /// </summary>
        /// <param name="agentId">Agent ID</param>
        /// <returns>Cached orchestrator or null if not found</returns>
        HybridLangChainOrchestrator? GetOrchestratorForAgent(int agentId);

        /// <summary>
        /// Ensure orchestrator is created and cached for an agent
        /// </summary>
        /// <param name="agentId">Agent ID</param>
        /// <param name="modelId">Model ID (optional, will look up from agent if not provided)</param>
        /// <param name="allowedPlugins">Filter tools by name (optional)</param>
        void EnsureOrchestratorForAgent(
            int agentId,
            string? modelId = null,
            System.Collections.Generic.IEnumerable<string>? allowedPlugins = null);

        /// <summary>
        /// Clear the cache of agent orchestrators (useful for testing or reload scenarios).
        /// </summary>
        void ClearCache();

        /// <summary>
        /// Get cached orchestrators count (diagnostic).
        /// </summary>
        int GetCachedCount();
    }
}
