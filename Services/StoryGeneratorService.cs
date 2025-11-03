// File: Services/StoryGeneratorService.cs
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Planning;
using System.Diagnostics;
using System.Linq;

namespace TinyGenerator.Services
{
    public class StoryGeneratorService
    {
        private readonly IKernel _kernel;
        private readonly CostController _cost;
        private readonly StoriesService _stories;
    private readonly HandlebarsPlanner _planner;
    private readonly PersistentMemoryService _persistentMemory;
    private readonly PlannerExecutor _plannerExecutor;
        private readonly string _collection = "storie";
        private readonly string _outputPath = "wwwroot/story_output.txt";
        private readonly Dictionary<string, string> _currentStoryMemory = new Dictionary<string, string>();

        public StoryGeneratorService(IKernel kernel, CostController cost, StoriesService stories, PersistentMemoryService persistentMemory, PlannerExecutor plannerExecutor)
        {
            _kernel = kernel;
            _cost = cost;
            _stories = stories;
            _planner = new HandlebarsPlanner(_kernel);
            _persistentMemory = persistentMemory;
            _plannerExecutor = plannerExecutor;
        }

        public class GenerationResult
        {
            public string StoryA { get; set; } = string.Empty;
            public string EvalA { get; set; } = string.Empty;
            public double ScoreA { get; set; }

            public string ModelA { get; set; } = string.Empty;

            public string StoryB { get; set; } = string.Empty;
            public string EvalB { get; set; } = string.Empty;
            public double ScoreB { get; set; }

            public string ModelB { get; set; } = string.Empty;

            public string StoryC { get; set; } = string.Empty;
            public string EvalC { get; set; } = string.Empty;
            public double ScoreC { get; set; }

            public string ModelC { get; set; } = string.Empty;

            public string? Approved { get; set; }
            public string Message { get; set; } = string.Empty;
        }

        public async Task<GenerationResult> GenerateStoryAsync(string theme, Action<string> progress, string selectedWriter = "All")
        {
            // normalize selection
            selectedWriter = (selectedWriter ?? "All").Trim();
            var sel = selectedWriter.ToUpperInvariant(); // "ALL", "A", "B", "C"
            const int MIN_CHARS = 3000;
            const double MIN_SCORE = 7.0;
            progress?.Invoke("Inizializzazione agenti e planner...");
            _cost?.ResetRunCounters();
            _currentStoryMemory.Clear(); // Clear previous story memory
            // qwen2.5:14b-instruct-q4_K_M,mistral-nemo:12b-instruct-2407-q4_K_M
            var writerModelA = "phi3:3.8b-mini-4k-instruct-q4_K_M";
            var writerModelB = "mistral:7b-instruct-q4_K_M";
            // Ensure Ollama model instances are started with a larger context (best-effort).
            try
            {
                var ctxEnv = Environment.GetEnvironmentVariable("OLLAMA_DEFAULT_CONTEXT");
                var desiredContextDefault = 8192;
                if (!string.IsNullOrWhiteSpace(ctxEnv) && int.TryParse(ctxEnv, out var v)) desiredContextDefault = v;

                // prefer per-model configured "context_to_use" if available in DB
                try
                {
                    var miA = _cost?.GetModelInfo(writerModelA);
                    var ctxA = miA != null && miA.ContextToUse > 0 ? miA.ContextToUse : desiredContextDefault;
                    var rA = await OllamaMonitorService.StartModelWithContextAsync(writerModelA, ctxA);
                    Console.WriteLine($"[OllamaMonitor] Start {writerModelA} ctx={ctxA}: {rA.Output}");
                }
                catch { }

                try
                {
                    var miB = _cost?.GetModelInfo(writerModelB);
                    var ctxB = miB != null && miB.ContextToUse > 0 ? miB.ContextToUse : desiredContextDefault;
                    var rB = await OllamaMonitorService.StartModelWithContextAsync(writerModelB, ctxB);
                    Console.WriteLine($"[OllamaMonitor] Start {writerModelB} ctx={ctxB}: {rB.Output}");
                }
                catch { }
            }
            catch { }
            // Diagnostic: print configured models for agents to ensure correct mapping
            try { Console.WriteLine($"[StoryGen] Configured writerModelA={writerModelA}, writerModelB={writerModelB}"); } catch { }
            var writerA = MakeAgent("WriterA", "Scrittore bilanciato", "Sei uno scrittore esperto. Scrivi in italiano, coerente e ben strutturata. Evita ripetizioni.", writerModelA);
            var writerB = MakeAgent("WriterB", "Scrittore emotivo", "Sei un narratore emotivo. Scrivi in italiano coinvolgente, evita ripetizioni.", writerModelB);
            var writerModelC = "phi3:mini-128k";
            var evaluator1 = MakeAgent("Evaluator1", "Coerenza", "Valuta coerenza e struttura. Rispondi JSON: {\"score\":<1-10>}", "qwen2.5:3b");
            var evaluator2 = MakeAgent("Evaluator2", "Stile", "Valuta stile e ritmo. Rispondi JSON: {\"score\":<1-10>}", "llama3.2:3b");

            // Create plan for the theme
            var plan = await _planner.CreatePlanAsync($"Genera una storia completa sul tema: {theme}");

            // Use PlannerExecutor which will run the plan for each writer and report per-step progress
            var memoryKey = Guid.NewGuid().ToString();
            var candidateStories = new Dictionary<string, string>();

            // --- Writer C: single-shot epic writer (writes whole story in one shot: title + story only) ---
            if (sel == "ALL" || sel == "C")
            {
                var writer = MakeAgent("WriterC", "Scrittore epico", "Sei uno scrittore epico: scrivi storie lunghe, avvincenti e dettagliate in italiano.", writerModelC);
                var agentMemoryKey = $"{writer.Name}_{memoryKey}";
                progress?.Invoke($"{writer.Name}: avvio single-shot writer (titolo + storia)...");

                // Instruct the agent to reply ONLY with Title and Story in this exact format
                var promptC = $"Genera una storia completa sul tema: {theme}\n\n" +
                              "Rispondi ESCLUSIVAMENTE nel seguente formato:\nTitolo: <Il titolo qui>\n\n<Corpo della storia qui>\n\n" +
                              "Niente altro: nessuna spiegazione, nessun metadata, solo il titolo e la storia. Scrivi la storia il più lunga e avvincente possibile.";

                var raw = await Ask(writer, promptC);
                // keep raw as-is; ensure coherence/length
                var assembledC = raw ?? string.Empty;
                assembledC = await EnsureCoherent(assembledC, writer, $"Storia completa sul tema: {theme}");
                assembledC = await ExtendUntil(assembledC, MIN_CHARS, writer);
                candidateStories[writer.Name] = assembledC;
            }

            // --- Writer A: use PlannerExecutor (planner-driven steps) ---
            if (sel == "ALL" || sel == "A")
            {
                var writer = writerA;
                var agentMemoryKey = $"{writer.Name}_{memoryKey}";
                progress?.Invoke($"{writer.Name}: avvio esecuzione piano...");
                var assembled = await _plannerExecutor.ExecutePlanForAgentAsync(writer, plan, agentMemoryKey, msg => progress?.Invoke(msg));
                if (string.IsNullOrWhiteSpace(assembled)) assembled = string.Empty;
                assembled = await EnsureCoherent(assembled, writer, $"Storia completa sul tema: {theme}");
                assembled = await ExtendUntil(assembled, MIN_CHARS, writer);
                candidateStories[writer.Name] = assembled;
            }

            // --- Writer B: use the FreeWriterPlanner (autonomous planner) ---
            if (sel == "ALL" || sel == "B")
            {
                var writer = writerB;
                var agentMemoryKey = $"{writer.Name}_{memoryKey}";
                progress?.Invoke($"{writer.Name}: avvio FreeWriterPlanner...");

                var freePlanner = new FreeWriterPlanner(_kernel);
                // prefix progress messages with agent name so UI can distinguish
                var assembledRaw = await freePlanner.RunAsync(theme, agentMemoryKey, writer, msg => progress?.Invoke($"{writer.Name}|freeplanner|{msg}"));

                // Extract MEMORY-JSON blocks from the returned text and save as chapters if present
                var chapParts = new List<string>();
                try
                {
                    var rx = new System.Text.RegularExpressions.Regex("---MEMORY-JSON---\\s*(\\{[\\s\\S]*?\\})\\s*---END-MEMORY---", System.Text.RegularExpressions.RegexOptions.Singleline);
                    var matches = rx.Matches(assembledRaw ?? string.Empty);
                    int chap = 1;
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        var json = m.Groups[1].Value;
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            var content = root.GetProperty("content").GetString() ?? string.Empty;
                            chapParts.Add(content);
                            try { _stories?.SaveChapter(agentMemoryKey, chap, content); } catch { }
                            chap++;
                        }
                        catch { }
                    }
                }
                catch { }

                var assembled = chapParts.Count > 0 ? string.Join("\n\n", chapParts) : assembledRaw ?? string.Empty;
                assembled = await EnsureCoherent(assembled, writer, $"Storia completa sul tema: {theme}");
                assembled = await ExtendUntil(assembled, MIN_CHARS, writer);
                candidateStories[writer.Name] = assembled;
            }

            // pick candidate stories produced by writers
            var storiaA = candidateStories.ContainsKey(writerA.Name) ? candidateStories[writerA.Name] : string.Empty;
            var storiaB = candidateStories.ContainsKey(writerB.Name) ? candidateStories[writerB.Name] : string.Empty;

            // Evaluate candidate stories only if they exist (skip empty ones to avoid evaluator calls)
            double scoreA = 0, scoreB = 0, scoreC = 0;
            string evalACombined = string.Empty, evalBCombined = string.Empty, evalCCombined = string.Empty;
            var storiaC = candidateStories.ContainsKey("WriterC") ? candidateStories["WriterC"] : string.Empty;
            if (!string.IsNullOrWhiteSpace(storiaA)) (scoreA, evalACombined) = await EvaluateAsync(storiaA, evaluator1, evaluator2);
            if (!string.IsNullOrWhiteSpace(storiaB)) (scoreB, evalBCombined) = await EvaluateAsync(storiaB, evaluator1, evaluator2);
            if (!string.IsNullOrWhiteSpace(storiaC)) (scoreC, evalCCombined) = await EvaluateAsync(storiaC, evaluator1, evaluator2);

            var result = new GenerationResult
            {
                StoryA = storiaA,
                EvalA = evalACombined,
                ScoreA = scoreA,
                ModelA = writerModelA,
                StoryB = storiaB,
                EvalB = evalBCombined,
                ScoreB = scoreB,
                ModelB = writerModelB
                ,
                StoryC = storiaC,
                EvalC = evalCCombined,
                ScoreC = scoreC,
                ModelC = !string.IsNullOrWhiteSpace(storiaC) ? writerModelC : string.Empty
            };

            // Choose best approved story among A, B, C
            var scores = new List<(string name, double score, string story)>
            {
                ("A", scoreA, storiaA),
                ("B", scoreB, storiaB),
                ("C", scoreC, storiaC)
            };
            var best = scores.OrderByDescending(s => s.score).First();
            if (best.score >= MIN_SCORE)
            {
                result.Approved = best.story;
                result.Message = $"Story {best.name} approvata.";
            }
            else
            {
                result.Approved = null;
                result.Message = "Nessuna storia ha raggiunto il punteggio minimo.";
            }

            if (!string.IsNullOrEmpty(result.Approved))
            {
                await File.WriteAllTextAsync(_outputPath, result.Approved);
                try { await _kernel.Memory.SaveInformationAsync(_collection, result.Approved, Guid.NewGuid().ToString()); } catch { }
            }

            try { _stories?.SaveGeneration(theme, result, memoryKey); } catch { }

            return result;
        }

        private ChatCompletionAgent MakeAgent(string name, string desc, string sys, string? model = null) =>
            new ChatCompletionAgent(name, _kernel, desc, model) { Instructions = sys };

        private async Task<string> Ask(ChatCompletionAgent agent, string input)
        {
            var result = await agent.InvokeAsync(input);
            var content = string.Join("\n", result.Select(x => x.Content));

            try
            {
                // record call with token estimates if CostController is available
                if (_cost != null)
                {
                    var reqTokens = _cost.EstimateTokensFromText(input);
                    var resTokens = _cost.EstimateTokensFromText(content);
                    var model = agent.Model ?? "ollama";
                    // use per-model cost estimation (input/output)
                    var cost = _cost.EstimateCost(model, reqTokens, resTokens);
                    _cost.RecordCall(model, reqTokens, resTokens, cost, input, content);
                }
            }
            catch { /* don't fail on logging */ }

            return content;
        }

        private async Task<string> ExtendUntil(string text, int minChars, ChatCompletionAgent writer)
        {
            int rounds = 0;
            while (text.Length < minChars && rounds++ < 6)
            {
                var prompt = $"""
Continua la storia da dove si era interrotta.
NO riassunti, NO ripetizioni.
Contesto:
{text[^Math.Min(4000, text.Length)..]}
""";
                var extra = await Ask(writer, prompt);
                // if the continuation looks gibberish/repetitive, stop extending
                if (extra.Length < 500) break;
                if (IsLikelyGibberish(extra)) break;
                text += "\n\n" + extra;
            }
            return text;
        }

        // attempt to detect high repetition / gibberish and retry generation with an explicit hint
        private async Task<string> EnsureCoherent(string initial, ChatCompletionAgent writer, string originalPrompt)
        {
            var text = initial ?? string.Empty;
            int attempts = 0;
            while (attempts++ < 3)
            {
                if (!IsLikelyGibberish(text)) return text;
                // ask to regenerate with explicit instruction avoiding repetition
                var regenHint = originalPrompt + "\n\nRIGENERA la storia evitando ripetizioni, producendo frasi complete e un flusso narrativo coerente. Non ripetere parole o blocchi di testo.\n";
                text = await Ask(writer, regenHint);
            }
            return text;
        }

        // crude heuristic: low ratio of unique words => repetition/gibberish
        private bool IsLikelyGibberish(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            var words = text.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim().ToLowerInvariant()).ToArray();
            if (words.Length < 20) return true; // too short to be a full story
            var unique = words.Distinct().Count();
            double uniqRatio = (double)unique / words.Length;
            if (uniqRatio < 0.45) return true; // too repetitive
            // detect model outputs that are a sequence of short 'commands' like "Scrivi ..." repeated
            var scriviCount = words.Count(w => w == "scrivi");
            if ((double)scriviCount / words.Length > 0.02) return true;
            // also check for repeated single-word tokens (e.g., "mare mare mare")
            var repeats = words.Where((w, i) => i > 0 && w == words[i - 1]).Count();
            if ((double)repeats / words.Length > 0.06) return true;
            return false;
        }

        private async Task<double> GetScore(string story, ChatCompletionAgent eval1, ChatCompletionAgent eval2)
        {
            var j1 = await Ask(eval1, story);
            var j2 = await Ask(eval2, story);
            return (ParseScore(j1) + ParseScore(j2)) / 2;
        }

        private double ParseScore(string json)
        {
            try
            {
                var i = json.IndexOf("\"score\"");
                if (i < 0) return 0;
                var part = json[(i + 7)..];
                var colon = part.IndexOf(':');
                var val = part[(colon + 1)..].Trim();
                var num = new string(val.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
                return double.TryParse(num, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            }
            catch { return 0; }
        }

        // Helper: ask multiple evaluators and return average score and combined responses
        private async Task<(double averageScore, string combinedResponses)> EvaluateAsync(string story, params ChatCompletionAgent[] evaluators)
        {
            if (evaluators == null || evaluators.Length == 0) return (0.0, string.Empty);
            double total = 0.0;
            var parts = new List<string>();
            foreach (var ev in evaluators)
            {
                try
                {
                    var res = await Ask(ev, story);
                    parts.Add(res);
                    var parsed = ParseScore(res);
                    total += parsed;
                    try { Console.WriteLine($"[Evaluate] {ev.Name} -> score={parsed:F2} model={ev.Model ?? "?"} responsePreview={res.Substring(0, Math.Min(200, res.Length)).Replace('\n',' ')}"); } catch { }
                }
                catch (Exception ex)
                {
                    parts.Add($"ERROR from {ev.Name}: {ex.Message}");
                    try { Console.WriteLine($"[Evaluate] ERROR from {ev.Name}: {ex.Message}"); } catch { }
                }
            }
            var avg = evaluators.Length > 0 ? total / evaluators.Length : 0.0;
            return (avg, string.Join("\n", parts));
        }

        private string BuildPromptForStep(PlanStep step, string theme, string memoryKey)
        {
            var basePrompt = $"Tema: {theme}\nPasso: {step.Description}\n";
            if (step.Description.Contains("trama"))
            {
                return basePrompt + "Crea una trama dettagliata suddivisa in 6 capitoli. Descrivi brevemente ciascun capitolo. Nello scrivere la storia mantieniti nei lilmiti delle tue regole di generazione storie in ambito di violenza e volgarità, ma non fare storie noiose.";
            }
            else if (step.Description.Contains("personaggi"))
            {
                return basePrompt + "Definisci i personaggi principali e i loro caratteri.";
            }
            else if (step.Description.Contains("primo capitolo"))
            {
                return basePrompt + "Scrivi il primo capitolo, con narratore e dialoghi.";
            }
            else if (step.Description.Contains("riassunto") && step.Description.Contains("primo"))
            {
                return basePrompt + "Fai un riassunto di quello che è successo nel primo capitolo.";
            }
            else if (step.Description.Contains("secondo capitolo"))
            {
                var context = GetMemoryContext(memoryKey, new[] { "trama", "personaggi", "riassunto primo capitolo" });
                return basePrompt + $"Contesto:\n{context}\nScrivi il secondo capitolo.";
            }
            else if (step.Description.Contains("terzo capitolo"))
            {
                var context = GetMemoryContext(memoryKey, new[] { "trama", "personaggi", "riassunto cumulativo" });
                return basePrompt + $"Contesto:\n{context}\nScrivi il terzo capitolo.";
            }
            else if (step.Description.Contains("quarto capitolo"))
            {
                var context = GetMemoryContext(memoryKey, new[] { "trama", "personaggi", "riassunto cumulativo" });
                return basePrompt + $"Contesto:\n{context}\nScrivi il quarto capitolo.";
            }
            else if (step.Description.Contains("quinto capitolo"))
            {
                var context = GetMemoryContext(memoryKey, new[] { "trama", "personaggi", "riassunto cumulativo" });
                return basePrompt + $"Contesto:\n{context}\nScrivi il quinto capitolo.";
            }
            else if (step.Description.Contains("sesto capitolo"))
            {
                var context = GetMemoryContext(memoryKey, new[] { "trama", "personaggi", "riassunto cumulativo" });
                return basePrompt + $"Contesto:\n{context}\nScrivi il sesto capitolo.";
            }
            else if (step.Description.Contains("aggiornare riassunto") && step.Description.Contains("secondo"))
            {
                var context = GetMemoryContext(memoryKey, new[] { "riassunto cumulativo", "capitolo 2" });
                return basePrompt + $"Contesto:\n{context}\nAggiorna il riassunto cumulativo aggiungendo il riassunto del secondo capitolo.";
            }
            else if (step.Description.Contains("aggiornare riassunto") && step.Description.Contains("terzo"))
            {
                var context = GetMemoryContext(memoryKey, new[] { "riassunto cumulativo", "capitolo 3" });
                return basePrompt + $"Contesto:\n{context}\nAggiorna il riassunto cumulativo aggiungendo il riassunto del terzo capitolo.";
            }
            // Similar for others
            else if (step.Description.Contains("aggiornare riassunto"))
            {
                var chapterNum = ExtractChapterNumber(step.Description);
                var context = GetMemoryContext(memoryKey, new[] { "riassunto cumulativo", $"capitolo {chapterNum}" });
                return basePrompt + $"Contesto:\n{context}\nAggiorna il riassunto cumulativo aggiungendo il riassunto del capitolo {chapterNum}.";
            }
            return basePrompt;
        }

        private int ExtractChapterNumber(string desc)
        {
            var words = desc.Split(' ');
            foreach (var word in words)
            {
                if (int.TryParse(word, out var num)) return num;
            }
            return 1;
        }

        private string GetMemoryContext(string agentMemoryKey, string[] keys)
        {
            var context = new List<string>();
            foreach (var key in keys)
            {
                var composite = $"{agentMemoryKey}_{key}";
                if (_currentStoryMemory.TryGetValue(composite, out var value))
                {
                    context.Add($"{key}: {value}");
                }
            }
            return string.Join("\n", context);
        }

        private async Task SaveToMemory(string agentName, string agentMemoryKey, string key, string content)
        {
            var composite = $"{agentMemoryKey}_{key}";
            _currentStoryMemory[composite] = content;
            try
            {
                // save both agent-scoped and generic entry
                await _kernel.Memory.SaveInformationAsync(_collection, $"{agentName}:{key}: {content}", $"{agentMemoryKey}_{key}");
            }
            catch { }
        }

        private string GetKeyForStep(string description)
        {
            if (description.Contains("trama")) return "trama";
            if (description.Contains("personaggi")) return "personaggi";
            if (description.Contains("primo capitolo")) return "capitolo 1";
            if (description.Contains("riassunto") && description.Contains("primo")) return "riassunto primo capitolo";
            if (description.Contains("secondo capitolo")) return "capitolo 2";
            if (description.Contains("aggiornare riassunto") && description.Contains("secondo")) return "riassunto cumulativo";
            if (description.Contains("terzo capitolo")) return "capitolo 3";
            if (description.Contains("aggiornare riassunto") && description.Contains("terzo")) return "riassunto cumulativo";
            if (description.Contains("quarto capitolo")) return "capitolo 4";
            if (description.Contains("aggiornare riassunto") && description.Contains("quarto")) return "riassunto cumulativo";
            if (description.Contains("quinto capitolo")) return "capitolo 5";
            if (description.Contains("aggiornare riassunto") && description.Contains("quinto")) return "riassunto cumulativo";
            if (description.Contains("sesto capitolo")) return "capitolo 6";
            if (description.Contains("aggiornare riassunto") && description.Contains("sesto")) return "riassunto cumulativo";
            return description;
        }
    }
}