using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Workaround for SK 1.67.1 bug where ToolCallBehavior.AutoInvokeKernelFunctions 
    /// doesn't send tools to the OpenAI API.
    /// 
    /// This service injects tools into OpenAIPromptExecutionSettings using reflection
    /// so they are actually sent to the API.
    /// </summary>
    public class ToolsInjectorService
    {
        /// <summary>
        /// Inject kernel functions as tools into the execution settings.
        /// Uses reflection to set the internal "_tools" field since there's no public property.
        /// </summary>
        public static void InjectToolsFromKernel(
            Kernel kernel,
            OpenAIPromptExecutionSettings settings,
            ICustomLogger? logger = null)
        {
            try
            {
                if (kernel == null || settings == null)
                {
                    logger?.Log("Warning", "ToolsInjectorService", "Kernel or settings is null, skipping tool injection");
                    return;
                }

                var functionsMetadata = kernel.Plugins.GetFunctionsMetadata().ToList();
                if (functionsMetadata.Count == 0)
                {
                    logger?.Log("Information", "ToolsInjectorService", "No kernel functions found to inject");
                    return;
                }

                // Convert KernelFunctionMetadata to ChatTool objects
                var tools = new List<ChatTool>();
                foreach (var funcMetadata in functionsMetadata)
                {
                    try
                    {
                        var tool = ConvertToOpenAIChatTool(funcMetadata);
                        if (tool != null)
                        {
                            tools.Add(tool);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Log("Warning", "ToolsInjectorService", 
                            $"Failed to convert function {funcMetadata.PluginName}/{funcMetadata.Name}: {ex.Message}");
                    }
                }

                if (tools.Count == 0)
                {
                    logger?.Log("Information", "ToolsInjectorService", "No tools could be converted from kernel functions");
                    return;
                }

                // Try to inject into settings using reflection
                InjectToolsViaReflection(settings, tools, logger);
            }
            catch (Exception ex)
            {
                logger?.Log("Error", "ToolsInjectorService", 
                    $"Error injecting tools from kernel: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Convert KernelFunctionMetadata to OpenAI ChatTool format.
        /// </summary>
        private static ChatTool? ConvertToOpenAIChatTool(Microsoft.SemanticKernel.KernelFunctionMetadata funcMetadata)
        {
            try
            {
                // Build function definition JSON following OpenAI format
                var functionDef = new
                {
                    name = $"{funcMetadata.PluginName}_{funcMetadata.Name}".ToLower(),
                    description = funcMetadata.Description ?? "No description",
                    parameters = ConvertParametersToJsonSchema(funcMetadata)
                };

                // Serialize to JSON
                var functionJson = JsonSerializer.Serialize(functionDef);

                // Create ChatTool using the public constructor if available
                // ChatTool represents a function definition that can be sent to OpenAI
                var tool = ChatTool.CreateFunctionTool(funcMetadata.Name, funcMetadata.Description ?? "No description");
                return tool;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to convert function {funcMetadata.Name} to ChatTool", ex);
            }
        }

        /// <summary>
        /// Convert kernel function parameters to JSON Schema format for OpenAI.
        /// </summary>
        private static object ConvertParametersToJsonSchema(
            Microsoft.SemanticKernel.KernelFunctionMetadata funcMetadata)
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            if (funcMetadata.Parameters != null && funcMetadata.Parameters.Count > 0)
            {
                foreach (var param in funcMetadata.Parameters)
                {
                    var paramDef = new
                    {
                        type = "string",
                        description = param.Description ?? $"Parameter: {param.Name}"
                    };
                    properties[param.Name] = paramDef;

                    // IsRequired is a bool?, so check if true
                    if (param.IsRequired)
                    {
                        required.Add(param.Name);
                    }
                }
            }

            return new
            {
                type = "object",
                properties = properties,
                required = required.Count > 0 ? required : null
            };
        }

        /// <summary>
        /// Inject tools into OpenAIPromptExecutionSettings using reflection.
        /// This is a workaround since there's no public property to set tools.
        /// </summary>
        private static void InjectToolsViaReflection(
            OpenAIPromptExecutionSettings settings,
            List<ChatTool> tools,
            ICustomLogger? logger)
        {
            if (tools == null || tools.Count == 0)
                return;

            try
            {
                // Try to find and set the internal _tools field
                var toolsField = typeof(OpenAIPromptExecutionSettings)
                    .GetField("_tools", BindingFlags.NonPublic | BindingFlags.Instance);

                if (toolsField != null && toolsField.FieldType.IsAssignableFrom(typeof(IList<ChatTool>)))
                {
                    toolsField.SetValue(settings, tools);
                    logger?.Log("Information", "ToolsInjectorService", 
                        $"Successfully injected {tools.Count} tools via reflection (field _tools)");
                    return;
                }

                // Try alternative field name
                var toolsField2 = typeof(OpenAIPromptExecutionSettings)
                    .GetField("Tools", BindingFlags.NonPublic | BindingFlags.Instance);

                if (toolsField2 != null && toolsField2.FieldType.IsAssignableFrom(typeof(IList<ChatTool>)))
                {
                    toolsField2.SetValue(settings, tools);
                    logger?.Log("Information", "ToolsInjectorService", 
                        $"Successfully injected {tools.Count} tools via reflection (field Tools)");
                    return;
                }

                // Try to find property
                var toolsProperty = typeof(OpenAIPromptExecutionSettings)
                    .GetProperty("Tools", BindingFlags.Public | BindingFlags.Instance);

                if (toolsProperty != null && toolsProperty.CanWrite)
                {
                    toolsProperty.SetValue(settings, tools);
                    logger?.Log("Information", "ToolsInjectorService", 
                        $"Successfully injected {tools.Count} tools via reflection (property Tools)");
                    return;
                }

                logger?.Log("Warning", "ToolsInjectorService", 
                    "Could not find field or property to inject tools into OpenAIPromptExecutionSettings");
            }
            catch (Exception ex)
            {
                logger?.Log("Warning", "ToolsInjectorService", 
                    $"Failed to inject tools via reflection: {ex.Message}");
            }
        }
    }
}
