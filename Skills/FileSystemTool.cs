using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for file system operations.
    /// Converted from FileSystemSkill (Semantic Kernel).
    /// </summary>
    public class FileSystemTool : BaseLangChainTool, ITinyTool
    {
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public FileSystemTool(ICustomLogger? logger = null) 
            : base("filesystem", "Provides file system functions such as checking existence, reading, writing, and deleting files.", logger)
        {
        }

        public override Dictionary<string, object> GetSchema()
        {
            return CreateFunctionSchema(
                Name,
                Description,
                new Dictionary<string, object>
                {
                    {
                        "operation",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "The file operation: 'file_exists', 'read_all_text', 'write_all_text', 'delete_file', 'describe'" }
                        }
                    },
                    {
                        "path",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "The file path" }
                        }
                    },
                    {
                        "content",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Content to write (for write_all_text operation)" }
                        }
                    }
                },
                new List<string> { "operation", "path" }
            );
        }

        public override async Task<string> ExecuteAsync(string input)
        {
            try
            {
                var request = ParseInput<FileSystemToolRequest>(input);
                if (request == null)
                    return SerializeResult(new { error = "Invalid input format" });

                CustomLogger?.Log("Info", "FileSystemTool", $"Executing operation: {request.Operation} on path: {request.Path}");

                return request.Operation?.ToLowerInvariant() switch
                {
                    "file_exists" => SerializeResult(new { result = File.Exists(request.Path) }),
                    "read_all_text" => SerializeResult(new 
                    { 
                        result = File.Exists(request.Path) ? File.ReadAllText(request.Path) : string.Empty 
                    }),
                    "write_all_text" => ExecuteWriteAllText(request),
                    "delete_file" => ExecuteDeleteFile(request),
                    "describe" => SerializeResult(new { result = "Available operations: file_exists(path), read_all_text(path), write_all_text(path, content), delete_file(path). Example: read_all_text('/path/to/file.txt') returns the contents of the file." }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "FileSystemTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private string ExecuteWriteAllText(FileSystemToolRequest request)
        {
            try
            {
                File.WriteAllText(request.Path, request.Content ?? string.Empty);
                return SerializeResult(new { result = "File written successfully", path = request.Path });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private string ExecuteDeleteFile(FileSystemToolRequest request)
        {
            try
            {
                if (File.Exists(request.Path))
                {
                    File.Delete(request.Path);
                    return SerializeResult(new { result = "File deleted successfully", path = request.Path });
                }
                return SerializeResult(new { result = "File not found", path = request.Path });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private class FileSystemToolRequest
        {
            public string? Operation { get; set; }
            public string? Path { get; set; }
            public string? Content { get; set; }
        }
    }
}
