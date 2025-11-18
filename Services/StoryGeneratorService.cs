// File: Services/StoryGeneratorService.cs
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Memory;
using System.Diagnostics;
using System.Linq;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public class StoryGeneratorService
    {
    private readonly IKernelFactory _kernelFactory;
        private readonly CostController _cost;
        private readonly StoriesService _stories;
    // TODO: Planner disabilitato temporaneamente (HandebarsPlanner non compatibile con SK 1.51.0-preview)
    // private readonly HandlebarsPlanner _planner;
        private readonly PersistentMemoryService _persistentMemory;
        private readonly PlannerExecutor _plannerExecutor;
        private readonly string _collection = "storie";
        private readonly string _outputPath = "wwwroot/story_output.txt";
        private readonly Dictionary<string, string> _currentStoryMemory = new Dictionary<string, string>();

    public StoryGeneratorService(IKernelFactory kernelFactory, CostController cost, StoriesService stories, PersistentMemoryService persistentMemory, PlannerExecutor plannerExecutor)
    {
        _kernelFactory = kernelFactory;
        _cost = cost;
        _stories = stories;
        // _planner = new HandlebarsPlanner(_kernel); // TODO: Planner disabilitato temporaneamente
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
            
            // Create kernels and system prompts
            var writerAKernel = _kernelFactory.CreateKernel(writerModelA);
            var writerAPrompt = "Sei uno scrittore esperto. Scrivi in italiano, coerente e ben strutturata. Evita ripetizioni.";
            
            var writerBKernel = _kernelFactory.CreateKernel(writerModelB);
            var writerBPrompt = "Sei un narratore emotivo. Scrivi in italiano coinvolgente, evita ripetizioni.";
            
            var writerModelC = "phi3:mini-128k";
            var writerCKernel = _kernelFactory.CreateKernel(writerModelC);
            var writerCPrompt = "Sei uno scrittore epico: scrivi storie lunghe, avvincenti e dettagliate in italiano.";
            
            var evaluator1Kernel = _kernelFactory.CreateKernel("qwen2.5:3b");
            var evaluator1Prompt = "Valuta coerenza e struttura. Rispondi JSON: {\"score\":<1-10>}";
            
            var evaluator2Kernel = _kernelFactory.CreateKernel("llama3.2:3b");
            var evaluator2Prompt = "Valuta stile e ritmo. Rispondi JSON: {\"score\":<1-10>}";

            // Planner disabilitato temporaneamente

            // Use PlannerExecutor which will run the plan for each writer and report per-step progress
            var memoryKey = Guid.NewGuid().ToString();
            var candidateStories = new Dictionary<string, string>();

            // --- Writer C: single-shot epic writer (writes whole story in one shot: title + story only) ---
            if (sel == "ALL" || sel == "C")
            {
                var agentMemoryKey = $"WriterC_{memoryKey}";
                progress?.Invoke($"WriterC: avvio single-shot writer (titolo + storia)...");

                // Instruct the agent to reply ONLY with Title and Story in this exact format
                var promptC = $"Genera una storia completa sul tema: {theme}\n\n" +
                              "Rispondi ESCLUSIVAMENTE nel seguente formato:\nTitolo: <Il titolo qui>\n\n<Corpo della storia qui>\n\n" +
                              "Niente altro: nessuna spiegazione, nessun metadata, solo il titolo e la storia. Scrivi la storia il più lunga e avvincente possibile.";

                var raw = await Ask(writerCKernel, writerCPrompt, promptC, writerModelC);
                // keep raw as-is; ensure coherence/length
                var assembledC = raw ?? string.Empty;
                assembledC = await EnsureCoherent(assembledC, writerCKernel, writerCPrompt, $"Storia completa sul tema: {theme}");
                assembledC = await ExtendUntil(assembledC, MIN_CHARS, writerCKernel, writerCPrompt);
                candidateStories["WriterC"] = assembledC;
            }

            // --- Writer A: use PlannerExecutor (planner-driven steps) ---
            if (sel == "ALL" || sel == "A")
            {
                var agentMemoryKey = $"WriterA_{memoryKey}";
                progress?.Invoke($"WriterA: avvio esecuzione piano...");
                var assembled = string.Empty; // TODO: reintegrare planner compatibile
                if (string.IsNullOrWhiteSpace(assembled)) assembled = string.Empty;
                assembled = await EnsureCoherent(assembled, writerAKernel, writerAPrompt, $"Storia completa sul tema: {theme}");
                assembled = await ExtendUntil(assembled, MIN_CHARS, writerAKernel, writerAPrompt);
                candidateStories["WriterA"] = assembled;
            }

            // --- Writer B: FreeWriterPlanner non trovato: sezione disabilitata ---


            // pick candidate stories produced by writers
            var storiaA = candidateStories.ContainsKey("WriterA") ? candidateStories["WriterA"] : string.Empty;
            var storiaB = candidateStories.ContainsKey("WriterB") ? candidateStories["WriterB"] : string.Empty;

            // Evaluate candidate stories only if they exist (skip empty ones to avoid evaluator calls)
            double scoreA = 0, scoreB = 0, scoreC = 0;
            string evalACombined = string.Empty, evalBCombined = string.Empty, evalCCombined = string.Empty;
            var storiaC = candidateStories.ContainsKey("WriterC") ? candidateStories["WriterC"] : string.Empty;
            if (!string.IsNullOrWhiteSpace(storiaA)) (scoreA, evalACombined) = await EvaluateAsync(storiaA, (evaluator1Kernel, evaluator1Prompt), (evaluator2Kernel, evaluator2Prompt));
            if (!string.IsNullOrWhiteSpace(storiaB)) (scoreB, evalBCombined) = await EvaluateAsync(storiaB, (evaluator1Kernel, evaluator1Prompt), (evaluator2Kernel, evaluator2Prompt));
            if (!string.IsNullOrWhiteSpace(storiaC)) (scoreC, evalCCombined) = await EvaluateAsync(storiaC, (evaluator1Kernel, evaluator1Prompt), (evaluator2Kernel, evaluator2Prompt));

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
                try
                {
                    await _persistentMemory.SaveAsync(_collection, result.Approved);
                }
                catch { }
            }

            try { _stories?.SaveGeneration(theme, result, memoryKey); } catch { }

            return result;
        }

        private async Task<string> Ask(Kernel kernel, string systemPrompt, string input, string modelId = "unknown")
        {
            var chatHistory = new ChatHistory();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                chatHistory.AddSystemMessage(systemPrompt);
            chatHistory.AddUserMessage(input);

            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var chatResult = await chatService.GetChatMessageContentAsync(chatHistory, kernel: kernel);
            var content = chatResult?.Content ?? string.Empty;

            try
            {
                // record call with token estimates if CostController is available
                if (_cost != null)
                {
                    var reqTokens = _cost.EstimateTokensFromText(input);
                    var resTokens = _cost.EstimateTokensFromText(content);
                    var cost = _cost.EstimateCost(modelId, reqTokens, resTokens);
                    _cost.RecordCall(modelId, reqTokens, resTokens, cost, input, content);
                }
            }
            catch { /* don't fail on logging */ }

            return content;
        }

        private async Task<string> ExtendUntil(string text, int minChars, Kernel writerKernel, string systemPrompt)
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
                var extra = await Ask(writerKernel, systemPrompt, prompt);
                // if the continuation looks gibberish/repetitive, stop extending
                if (extra.Length < 500) break;
                if (IsLikelyGibberish(extra)) break;
                text += "\n\n" + extra;
            }
            return text;
        }

        // attempt to detect high repetition / gibberish and retry generation with an explicit hint
        private async Task<string> EnsureCoherent(string initial, Kernel writerKernel, string systemPrompt, string originalPrompt)
        {
            var text = initial ?? string.Empty;
            int attempts = 0;
            while (attempts++ < 3)
            {
                if (!IsLikelyGibberish(text)) return text;
                // ask to regenerate with explicit instruction avoiding repetition
                var regenHint = originalPrompt + "\n\nRIGENERA la storia evitando ripetizioni, producendo frasi complete e un flusso narrativo coerente. Non ripetere parole o blocchi di testo.\n";
                text = await Ask(writerKernel, systemPrompt, regenHint);
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

        private async Task<double> GetScore(string story, Kernel evalKernel1, string sysPrompt1, Kernel evalKernel2, string sysPrompt2)
        {
            var j1 = await Ask(evalKernel1, sysPrompt1, story);
            var j2 = await Ask(evalKernel2, sysPrompt2, story);
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
        private async Task<(double averageScore, string combinedResponses)> EvaluateAsync(string story, params (Kernel kernel, string systemPrompt)[] evaluators)
        {
            if (evaluators == null || evaluators.Length == 0) return (0.0, string.Empty);
            double total = 0.0;
            var parts = new List<string>();
            int evalIndex = 0;
            foreach (var (kernel, sysPrompt) in evaluators)
            {
                evalIndex++;
                var evalName = $"Evaluator{evalIndex}";
                try
                {
                    var res = await Ask(kernel, sysPrompt, story);
                    parts.Add(res);
                    var parsed = ParseScore(res);
                    total += parsed;
                    try { Console.WriteLine($"[Evaluate] {evalName} -> score={parsed:F2} responsePreview={res.Substring(0, Math.Min(200, res.Length)).Replace('\n',' ')}"); } catch { }
                }
                catch (Exception ex)
                {
                    parts.Add($"ERROR from {evalName}: {ex.Message}");
                    try { Console.WriteLine($"[Evaluate] ERROR from {evalName}: {ex.Message}"); } catch { }
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
                await _persistentMemory.SaveAsync(_collection, $"{agentName}:{key}: {content}");
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