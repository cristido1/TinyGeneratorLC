using System.IO;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    [Description("Provides file system functions such as checking existence, reading, writing, and deleting files.")]
    public class FileSystemSkill : ITinySkill
    {
        private readonly ICustomLogger? _logger;
        private int? _modelId;
        private string? _modelName;
        private DateTime? _lastCalled;
        private string? _lastFunction;
        
        public string? LastCalled { get; set; }

        // ITinySkill implementation
        int? ITinySkill.ModelId { get => _modelId; set => _modelId = value; }
        string? ITinySkill.ModelName { get => _modelName; set => _modelName = value; }
        int? ITinySkill.AgentId => null;
        string? ITinySkill.AgentName => null;
        DateTime? ITinySkill.LastCalled { get => _lastCalled; set => _lastCalled = value; }
        string? ITinySkill.LastFunction { get => _lastFunction; set => _lastFunction = value; }
        ICustomLogger? ITinySkill.Logger { get => _logger; set { } }

        public FileSystemSkill(ICustomLogger? logger = null)
        {
            _logger = logger;
        }

        [KernelFunction("file_exists"), Description("Checks if a file exists.")]
        public bool FileExists([Description("The path of the file to check.")] string path)
        {
            ((ITinySkill)this).LogFunctionCall("file_exists");
            LastCalled = nameof(FileExists);
            return File.Exists(path);
        }

        [KernelFunction("read_all_text"), Description("Reads all text from a file.")]
        public string ReadAllText([Description("The path of the file to read from.")] string path)
        {
            ((ITinySkill)this).LogFunctionCall("read_all_text");
            LastCalled = nameof(ReadAllText);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }

        [KernelFunction("write_all_text"), Description("Writes text to a file.")]
        public void WriteAllText([Description("The path of the file to write to.")] string path, [Description("The content to write to the file.")] string content)
        {
            ((ITinySkill)this).LogFunctionCall("write_all_text");
            LastCalled = nameof(WriteAllText);
            File.WriteAllText(path, content);
        }

        [KernelFunction("delete_file"), Description("Deletes a file.")]
        public void DeleteFile([Description("The path of the file to delete.")] string path)
        {
            ((ITinySkill)this).LogFunctionCall("delete_file");
            LastCalled = nameof(DeleteFile);
            if (File.Exists(path)) File.Delete(path);
        }
    
        [KernelFunction("describe"), Description("Describes the available file system functions.")]
        public string Describe()
        {
            ((ITinySkill)this).LogFunctionCall("describe");
            return "Available functions: file_exists(path), read_all_text(path), write_all_text(path, content), delete_file(path). " +
                "Example: file.read_all_text('/path/to/file.txt') returns the contents of the file.";
        }
    }
}
