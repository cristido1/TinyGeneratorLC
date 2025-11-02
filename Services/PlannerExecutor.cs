using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Planning;

namespace TinyGenerator.Services
{
    // Executor that gives an agent a plan and lets it run steps, returning assembled chapters.
    public sealed class PlannerExecutor
    {
        private readonly IKernel _kernel;
        private readonly StoriesService? _stories;
        private readonly string _collection = "storie";

        public PlannerExecutor(IKernel kernel, StoriesService? stories = null)
        {
            _kernel = kernel;
            _stories = stories;
        }

        public async Task<string> ExecutePlanForAgentAsync(ChatCompletionAgent agent, Plan plan, string agentMemoryKey, Action<string>? progress = null)
        {
            var parts = new List<string>();

            foreach (var step in plan.Steps)
            {
                progress?.Invoke($"{agent.Name}: eseguo passo: {step.Description}");
                var stepKey = GetKeyForStep(step.Description);
                var prompt = BuildPromptForStep(step.Description, plan.Goal);

                var askPrompt = prompt + "\n\n" +
                    "AL TERMINE DELLA RISPOSTA, FORNISCI ANCHE UN BLOCCO JSON MARCATO COME:\n" +
                    "---MEMORY-JSON---\n" +
                    "{" + "\"memory_key\":\"" + agentMemoryKey + "\", \"key\":\"" + stepKey + "\", \"content\":\"<il contenuto da salvare>\"}" + "\n" +
                    "---END-MEMORY---\n\n" +
                    "Rispondi prima il testo destinato all'utente (capitolo/descrizione) e poi il blocco MEMORY-JSON esattamente come sopra.\n";

                try { Console.WriteLine($"[PlannerExecutor] Sending prompt to {agent.Name} (model={agent.Model}): {askPrompt.Substring(0, Math.Min(400, askPrompt.Length)).Replace('\n',' ')}"); } catch { }
                try { TinyGenerator.Services.OllamaMonitorService.RecordPrompt(agent.Model ?? string.Empty, askPrompt); } catch { }
                var invokeResult = await agent.InvokeAsync(askPrompt);
                var content = string.Join("\n", invokeResult.Select(r => r.Content));

                // report that step produced output (first a short snippet)
                try
                {
                    var snippet = content.Length > 300 ? content.Substring(0, 300) + "..." : content;
                    progress?.Invoke($"{agent.Name}|{stepKey}|START\n{snippet}");
                }
                catch { }

                var memJson = ExtractMemoryJson(content);
                if (!string.IsNullOrEmpty(memJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(memJson);
                        var root = doc.RootElement;
                        var key = root.GetProperty("key").GetString() ?? stepKey;
                        var saved = root.GetProperty("content").GetString() ?? string.Empty;

                        // if the model provided a memory_key in the JSON, prefer it for saving
                        string modelMemoryKey = agentMemoryKey;
                        if (root.TryGetProperty("memory_key", out var mkProp))
                        {
                            try { var mk = mkProp.GetString(); if (!string.IsNullOrWhiteSpace(mk)) modelMemoryKey = mk; } catch { }
                        }

                        try
                        {
                            await _kernel.Memory.SaveInformationAsync(_collection, $"{agent.Name}:{key}: {saved}", $"{modelMemoryKey}_{key}");
                        }
                        catch { }

                        if (key.StartsWith("capitolo") && int.TryParse(new string(key.Where(char.IsDigit).ToArray()), out var chapNum))
                        {
                            parts.Add(saved);
                            try { _stories?.SaveChapter(modelMemoryKey, chapNum, saved); } catch { }
                        }

                        // publish the full saved content for UI
                        try { progress?.Invoke($"{agent.Name}|{key}|{saved}"); } catch { }
                    }
                    catch { }
                }
                else
                {
                    if (stepKey.StartsWith("capitolo"))
                    {
                        parts.Add(content);
                        try { _stories?.SaveChapter(agentMemoryKey, int.Parse(new string(stepKey.Where(char.IsDigit).ToArray())), content); } catch { }
                    }
                    else
                    {
                        try
                        {
                            await _kernel.Memory.SaveInformationAsync(_collection, $"{agent.Name}:{stepKey}: {content}", $"{agentMemoryKey}_{stepKey}");
                        }
                        catch { }
                    }
                }
            }

            var assembled = string.Join("\n\n", parts);
            return assembled;
        }

        private static string ExtractMemoryJson(string text)
        {
            var m = Regex.Match(text, @"---MEMORY-JSON---\s*(\{[\s\S]*?\})\s*---END-MEMORY---", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        private static string BuildPromptForStep(string stepDescription, string? theme = null)
        {
            var sd = stepDescription.ToLowerInvariant();
            var prefix = string.Empty;
            if (!string.IsNullOrWhiteSpace(theme))
            {
                // Use a clear user-instructions prefix so it's obvious in the final prompt
                var t = theme.Trim();
                // If the theme already contains a leading 'tema:' token, strip it to avoid duplication
                var idx = t.IndexOf("tema:", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    t = t[(idx + 5)..].Trim();
                }
                prefix = "Istruzioni utente: " + t + "\n\n";
            }
            // Prepend the theme/context prefix (if any) so user-provided instructions are included
            if (sd.Contains("trama")) return prefix + "Crea una trama dettagliata suddivisa in 6 capitoli. Per ogni capitolo scrivi una descrizione sintetica ma completa di quello che succede. Non usare contenuti sessali espliciti o violenti, ma fai in modo che la storia non sia noiosa.";
            if (sd.Contains("personaggi")) return prefix + "Definisci i personaggi principali: nome, età approssimativa, tratto caratteriale, ruolo nella storia (max 6 personaggi).";
            if (sd.Contains("primo capitolo")) return prefix + "Scrivi il primo capitolo con narratore e dialoghi. Mantieni tono coerente con la trama e i personaggi.";
            if (sd.Contains("secondo capitolo")) return prefix + "Scrivi il secondo capitolo usando il contesto della trama, personaggi e riassunto precedente.";
            if (sd.Contains("terzo capitolo")) return prefix + "Scrivi il terzo capitolo usando il contesto della trama, personaggi e riassunto cumulativo.";
            if (sd.Contains("quarto capitolo")) return prefix + "Scrivi il quarto capitolo.";
            if (sd.Contains("quinto capitolo")) return prefix + "Scrivi il quinto capitolo.";
            if (sd.Contains("sesto capitolo")) return prefix + "Scrivi il sesto capitolo.";
            if (sd.Contains("riassunto")) return prefix + "Fai un riassunto sintetico (3-5 frasi) di quanto accaduto.";
            return prefix + $"Esegui il passo: {stepDescription}";
        }

        private static string GetKeyForStep(string description)
        {
            var d = description.ToLowerInvariant();
            if (d.Contains("trama")) return "trama";
            if (d.Contains("personaggi")) return "personaggi";
            if (d.Contains("primo")) return "capitolo 1";
            if (d.Contains("secondo")) return "capitolo 2";
            if (d.Contains("terzo")) return "capitolo 3";
            if (d.Contains("quarto")) return "capitolo 4";
            if (d.Contains("quinto")) return "capitolo 5";
            if (d.Contains("sesto")) return "capitolo 6";
            if (d.Contains("riassunto")) return "riassunto cumulativo";
            return description;
        }
    }
    public class FreeWriterPlanner
{
    private readonly IKernel _kernel;
    private readonly string _collection = "storie";

    public FreeWriterPlanner(IKernel kernel)
    {
        _kernel = kernel;
    }

    public async Task<string> RunAsync(string prompt, string storyId, Microsoft.SemanticKernel.Agents.ChatCompletionAgent agent, Action<string>? progress = null)
    {
        // Stubbed kernel memory may not support search in offline mode; keep memoria empty as a best-effort.
        var memoria = string.Empty;

        // Build the meta-prompt without using raw interpolated strings to avoid brace-escaping issues.
        var metaPrompt =
            "Sei uno scrittore autonomo.\n" +
            "Il tuo obiettivo è creare una storia coerente in più capitoli, ma puoi decidere da solo l'ordine delle operazioni.\n" +
            "Hai memoria di ciò che hai già scritto:\n---\n" + memoria + "\n---\n" +
            "Quando ritieni di aver completato una parte, salvala in memoria come JSON:\n" +
            "---MEMORY-JSON---\n" +
            "{\n" +
            "  \"story_id\": \"" + storyId + "\",\n" +
            "  \"content\": \"<testo prodotto>\",\n" +
            "  \"summary\": \"<breve riassunto per memoria futura>\"\n" +
            "}\n" +
            "---END-MEMORY---\n" +
            "Poi decidi il prossimo passo da solo (nuovo capitolo, modifica, riassunto...).\n" +
            "Istruzioni utente: " + prompt + "\n";

        string result = string.Empty;
            for (int i = 0; i < 6; i++)
        {
            progress?.Invoke($"🪶 Step {i + 1}: generazione autonoma…");

            // Primary generation
                try { Console.WriteLine($"[FreeWriterPlanner] Sending metaPrompt to {agent.Name} (model={agent.Model}): {metaPrompt.Substring(0, Math.Min(400, metaPrompt.Length)).Replace('\n',' ')}"); } catch { }
                try { TinyGenerator.Services.OllamaMonitorService.RecordPrompt(agent.Model ?? string.Empty, metaPrompt); } catch { }
                var invoke = await agent.InvokeAsync(metaPrompt);
            var reply = string.Join("\n", invoke.Select(r => r.Content));

            // If the reply looks like gibberish, attempt up to 2 regeneration retries with explicit instructions
            int retries = 0;
            while (IsLikelyGibberishLocal(reply) && retries < 2)
            {
                retries++;
                progress?.Invoke($"🛠️ Step {i + 1}: output probabilmente non valido, tentativo di rigenerazione #{retries}...#\n{reply}");
                var regenHint = metaPrompt + "\n\nRIGENERA il testo precedente evitando ripetizioni, usa frasi complete, mantieni coerenza narrativa e produci paragrafi; non ripetere singole parole o sequenze.\n";
                var reinvoke = await agent.InvokeAsync(regenHint);
                reply = string.Join("\n", reinvoke.Select(r => r.Content));
            }

            result += reply + "\n";
            await SaveMemoryIfFound(reply, storyId);
        }

        return result;
    }

    private async Task SaveMemoryIfFound(string reply, string storyId)
    {
        var match = Regex.Match(reply, @"---MEMORY-JSON---(.*?)---END-MEMORY---", RegexOptions.Singleline);
        if (match.Success)
        {
            await _kernel.Memory.SaveInformationAsync(
                _collection,
                match.Groups[1].Value,
                storyId + "_" + Guid.NewGuid().ToString("N")
            );
        }
    }

    // Simple local heuristic to detect very repetitive/gibberish outputs from free planner.
    private static bool IsLikelyGibberishLocal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var words = text.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim().ToLowerInvariant()).ToArray();
        if (words.Length < 20) return true;
        var unique = words.Distinct().Count();
        double uniqRatio = (double)unique / words.Length;
        if (uniqRatio < 0.35) return true; // stricter for free planner
        var repeats = words.Where((w, i) => i > 0 && w == words[i - 1]).Count();
        if ((double)repeats / words.Length > 0.08) return true;
        return false;
    }
}
}
