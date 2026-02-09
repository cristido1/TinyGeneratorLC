using System;

namespace TinyGenerator.Services;

/// <summary>
/// Exception thrown when a model does not support tool calling.
/// This allows automatic marking of the model's NoTools flag.
/// </summary>
public class ModelNoToolsSupportException : Exception
{
    public string ModelName { get; }

    public ModelNoToolsSupportException(string modelName, string message) 
        : base(message)
    {
        ModelName = modelName;
    }

    public ModelNoToolsSupportException(string modelName, string message, Exception innerException) 
        : base(message, innerException)
    {
        ModelName = modelName;
    }
}
