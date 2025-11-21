using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Filter that tracks function invocations and model responses for logging to database.
    /// Logs both the function calls and their results to the Live Monitor.
    /// </summary>
    public class FunctionInvocationTrackerFilter : IFunctionInvocationFilter
    {
        private readonly ICustomLogger _customLogger;
        private readonly string _modelName;

        public FunctionInvocationTrackerFilter(ICustomLogger customLogger, string modelName)
        {
            _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
            _modelName = modelName ?? "unknown";
        }

        public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            try
            {
                var functionName = context.Function.Name;
                var pluginName = context.Function.PluginName ?? "core";
                
                // Extract function parameters if available
                var parameterInfo = "";
                try
                {
                    if (context.Arguments != null && context.Arguments.Count > 0)
                    {
                        var paramParts = new List<string>();
                        foreach (var kvp in context.Arguments)
                        {
                            var key = kvp.Key;
                            var value = kvp.Value?.ToString() ?? "(null)";
                            // Truncate very long parameter values
                            if (value.Length > 100)
                                value = value.Substring(0, 100) + "...";
                            paramParts.Add($"{key}={value}");
                        }
                        if (paramParts.Count > 0)
                        {
                            parameterInfo = $" [{string.Join(", ", paramParts)}]";
                        }
                    }
                }
                catch { }
                
                var message = $"Called function: {pluginName}/{functionName}{parameterInfo}";
                
                // Log with model context if available
                if (!string.IsNullOrWhiteSpace(_modelName))
                {
                    message = $"[{_modelName}] {message}";
                }
                
                _customLogger?.Log("Information", "FunctionInvocation", message);
            }
            catch
            {
                // Best-effort: don't fail if logging fails
            }

            // Call the next filter or function with error handling
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                // Log the error that occurred during function execution
                try
                {
                    var functionName = context.Function.Name;
                    var message = $"[{_modelName}] ERROR in {functionName}: {ex.Message}";
                    _customLogger?.Log("Error", "FunctionInvocation", message, ex.ToString());
                }
                catch
                {
                    // Best-effort: don't fail if logging fails
                }
                
                // Re-throw the exception so it propagates properly
                throw;
            }
            
            // After invocation, log the result
            try
            {
                if (context.Result != null)
                {
                    var resultContent = ExtractResultContent(context.Result);
                    if (!string.IsNullOrWhiteSpace(resultContent))
                    {
                        var functionName = context.Function.Name;
                        var message = $"[{_modelName}] RESPONSE from {functionName}: {resultContent}";
                        _customLogger?.Log("Information", "ModelCompletion", message);
                    }
                }
            }
            catch
            {
                // Best-effort: don't fail if logging fails
            }
        }

        /// <summary>
        /// Extracts the string content from various result types returned by SK functions
        /// </summary>
        private string ExtractResultContent(object? result)
        {
            if (result == null) return string.Empty;

            try
            {
                // Handle string results directly
                if (result is string str)
                {
                    return TruncateForDisplay(str);
                }

                // Handle IList<TextContent> or similar collections
                if (result is System.Collections.IEnumerable enumerable && !(result is string))
                {
                    var items = new List<string>();
                    foreach (var item in enumerable)
                    {
                        if (item != null)
                        {
                            var itemStr = item.ToString();
                            if (!string.IsNullOrWhiteSpace(itemStr))
                            {
                                items.Add(itemStr);
                            }
                        }
                    }
                    if (items.Count > 0)
                    {
                        return TruncateForDisplay(string.Join(" | ", items));
                    }
                }

                // Fallback to ToString
                var resultStr = result.ToString();
                return TruncateForDisplay(resultStr);
            }
            catch
            {
                return string.Empty;
            }
        }

        private string TruncateForDisplay(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "(empty response)";

            // Truncate very long responses for readability in logs (keep first 500 chars)
            return text.Length > 500 
                ? text.Substring(0, 500) + "..." 
                : text;
        }
    }
}


