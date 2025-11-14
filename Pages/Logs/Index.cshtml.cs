using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TinyGenerator.Models;

namespace TinyGenerator.Pages.Logs
{
    public class LogsModel : PageModel
    {
        public List<LogEntry> LogEntries { get; set; } = new();

        public void OnGet()
        {
            // TODO: Sostituire con la logica reale di caricamento log
            LogEntries = DummyLogs();
        }

        private List<LogEntry> DummyLogs()
        {
            return new List<LogEntry>
            {
                new LogEntry { Timestamp = DateTime.Now.AddMinutes(-1), Level = "Info", Message = "Avvio applicazione", Source = "System" },
                new LogEntry { Timestamp = DateTime.Now.AddSeconds(-30), Level = "Warning", Message = "Connessione lenta", Source = "Database" },
                new LogEntry { Timestamp = DateTime.Now, Level = "Error", Message = "Errore test", Source = "TestService" }
            };
        }
    }
}
