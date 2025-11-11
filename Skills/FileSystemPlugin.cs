using System.IO;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace TinyGenerator.Skills
{
    [Description("Provides file system functions such as checking existence, reading, writing, and deleting files.")]
    public class FileSystemPlugin
    {
        public string? LastCalled { get; set; }

        [KernelFunction("file_exists"),Description("Checks if a file exists.")]
        public bool FileExists([Description("The path of the file to check.")] string path) { LastCalled = nameof(FileExists); return File.Exists(path); }

        [KernelFunction("read_all_text"),Description("Reads all text from a file.")]
        public string ReadAllText([Description("The path of the file to read from.")] string path) { LastCalled = nameof(ReadAllText); return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }

        [KernelFunction("write_all_text"),Description("Writes text to a file.")]
        public void WriteAllText([Description("The path of the file to write to.")] string path, [Description("The content to write to the file.")] string content) { LastCalled = nameof(WriteAllText); File.WriteAllText(path, content); }

        [KernelFunction("delete_file"), Description("Deletes a file.")]
        public void DeleteFile([Description("The path of the file to delete.")] string path) { LastCalled = nameof(DeleteFile); if (File.Exists(path)) File.Delete(path); }
    
        [KernelFunction("describe"), Description("Describes the available file system functions.")]
        public string Describe() =>
            "Available functions: file_exists(path), read_all_text(path), write_all_text(path, content), delete_file(path). " +
            "Example: file.read_all_text('/path/to/file.txt') returns the contents of the file.";
    }
}
