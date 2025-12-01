using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using System.Collections.Generic;
using System.Linq;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.ChatLog
{
    public class ChatLogModel : PageModel
    {
        private readonly DatabaseService _db;

        public ChatLogModel(DatabaseService db)
        {
            _db = db;
        }

        public List<LogEntry> ChatMessages { get; set; } = new();

        public void OnGet()
        {
            // Get all log entries that are requests, responses, or prompts with chat_text populated
            var allLogs = _db.ListLogs(limit: 5000);
            
            ChatMessages = allLogs
                .Where(log => 
                    (log.Category == "ModelRequest" || 
                     log.Category == "ModelResponse" || 
                     log.Category == "ModelPrompt" || 
                     log.Category == "ModelCompletion") &&
                    !string.IsNullOrWhiteSpace(log.ChatText))
                .OrderByDescending(log => log.Timestamp)
                .ToList();

            // Extract agent names from messages where not explicitly set
            foreach (var log in ChatMessages)
            {
                if (string.IsNullOrWhiteSpace(log.AgentName))
                {
                    // Try to extract model name from message
                    var msg = log.Message ?? string.Empty;
                    if (msg.StartsWith("[") && msg.Contains("]"))
                    {
                        var endIdx = msg.IndexOf("]");
                        log.AgentName = msg.Substring(1, endIdx - 1);
                    }
                }
            }
        }
    }
}
