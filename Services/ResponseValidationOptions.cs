using System;
using System.Collections.Generic;

namespace TinyGenerator.Services
{
    public sealed class ResponseValidationOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Maximum number of retries after a failed validation.
        /// Total model calls for the primary model will be MaxRetries + 1.
        /// </summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>
        /// If true, after retries are exhausted the bridge asks the model for a best-effort diagnosis.
        /// Can be overridden per operation via CommandPolicies.
        /// </summary>
        public bool AskFailureReasonOnFinalFailure { get; set; } = true;

        /// <summary>
        /// If true, the bridge will attempt model fallback (via ModelFallbackService)
        /// after retries are exhausted.
        /// </summary>
        public bool EnableFallback { get; set; } = true;

        /// <summary>
        /// If true, the bridge will call the response_checker agent for validation.
        /// This can be overridden per operation via CommandPolicies.
        /// </summary>
        public bool EnableCheckerByDefault { get; set; } = true;

        /// <summary>
        /// Roles that must never be validated (avoid recursion / unwanted validation).
        /// </summary>
        public List<string> SkipRoles { get; set; } = new() { "response_checker", "log_analyzer" };

        /// <summary>
        /// Per operation policy. Key is typically LogScope.Current (thread scope / operation scope).
        /// </summary>
        public Dictionary<string, ResponseValidationCommandPolicy> CommandPolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Rule list fed to response_checker; checker must cite violated REGOLA n.
        /// </summary>
        public List<ResponseValidationRule> Rules { get; set; } = new();
    }

    public sealed class ResponseValidationCommandPolicy
    {
        public bool? EnableChecker { get; set; }

        /// <summary>
        /// Overrides ResponseValidationOptions.MaxRetries for this operation.
        /// </summary>
        public int? MaxRetries { get; set; }

        /// <summary>
        /// Overrides ResponseValidationOptions.AskFailureReasonOnFinalFailure for this operation.
        /// </summary>
        public bool? AskFailureReasonOnFinalFailure { get; set; }

        /// <summary>
        /// If set, only these rule ids will be passed to response_checker for this operation.
        /// If null/empty, all rules are used.
        /// </summary>
        public List<int>? RuleIds { get; set; }
    }

    public sealed class ResponseValidationRule
    {
        public int Id { get; set; }
        public string Text { get; set; } = string.Empty;
    }
}
