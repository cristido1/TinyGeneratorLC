// File: Services/StoryGeneratorService.cs
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Memory;
using System.Diagnostics;

public class StoryGeneratorService
{
    private readonly IKernel _kernel;
    private readonly string _collection = "storie";
    private readonly string _outputPath = "wwwroot/story_output.txt";

    public StoryGeneratorService(IKernel kernel)
    {
        _kernel = kernel;
    }

    public async Task<string> GenerateAsync(string theme)
    {
        const int MIN_CHARS = 20000;
        const double MIN_SCORE = 7.0;
        progress("Inizializzazione agenti, 2 scrittori e 2 valutatori...");
        var writerA = MakeAgent("WriterA", "Scrittore bilanciato", "Scrivi storie coerenti senza premesse.");
        var writerB = MakeAgent("WriterB", "Scrittore emotivo", "Stile intenso, narrativo e diretto.");
        var evaluator1 = MakeAgent("Evaluator1", "Coerenza", "Valuta coerenza e struttura. Rispondi JSON: {\"score\":<1-10>}");
        var evaluator2 = MakeAgent("Evaluator2", "Stile", "Valuta stile e ritmo. Rispondi JSON: {\"score\":<1-10>}");

        var prompt = $"""
Scrivi una storia lunga in italiano sul tema: {theme}
Requisiti:
- Almeno {MIN_CHARS} caratteri
- Finale chiuso
- NO premesse o riassunti
""";

        var storiaA = await Ask(writerA, prompt);
        var storiaB = await Ask(writerB, prompt);

        storiaA = await ExtendUntil(storiaA, MIN_CHARS, writerA);
        storiaB = await ExtendUntil(storiaB, MIN_CHARS, writerB);

        var scoreA = await GetScore(storiaA, evaluator1, evaluator2);
        var scoreB = await GetScore(storiaB, evaluator1, evaluator2);

        string approvata = "";
        if (scoreA >= MIN_SCORE)
        {
            approvata = storiaA;
        }
        else if (scoreB >= MIN_SCORE)
        {
            approvata = storiaB;
        }

        if (!string.IsNullOrEmpty(approvata))
        {
            await File.WriteAllTextAsync(_outputPath, approvata);
            await _kernel.Memory.SaveInformationAsync(_collection, approvata, Guid.NewGuid().ToString());
            return approvata;
        }
        else
        {
            return "Entrambe le storie sono state bocciate.";
        }
    }

    private ChatCompletionAgent MakeAgent(string name, string desc, string sys) =>
        new ChatCompletionAgent(name, _kernel, desc) { Instructions = sys };

    private async Task<string> Ask(ChatCompletionAgent agent, string input)
    {
        var result = await agent.InvokeAsync(input);
        return string.Join("\n", result.Select(x => x.Content));
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
            if (extra.Length < 500) break;
            text += "\n\n" + extra;
        }
        return text;
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
} 