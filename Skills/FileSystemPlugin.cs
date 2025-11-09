using System.IO;
using Microsoft.SemanticKernel;

namespace TinyGenerator.Skills
{
    public class FileSystemPlugin
    {
    public string? LastCalled { get; set; }

        [KernelFunction("file_exists")]
        public bool FileExists(string path) { LastCalled = nameof(FileExists); return File.Exists(path); }

        [KernelFunction("read_all_text")]
        public string ReadAllText(string path) { LastCalled = nameof(ReadAllText); return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }

        [KernelFunction("write_all_text")]
        public void WriteAllText(string path, string content) { LastCalled = nameof(WriteAllText); File.WriteAllText(path, content); }

        [KernelFunction("delete_file")]
        public void DeleteFile(string path) { LastCalled = nameof(DeleteFile); if (File.Exists(path)) File.Delete(path); }
    }
}
