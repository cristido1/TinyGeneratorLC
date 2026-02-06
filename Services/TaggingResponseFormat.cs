using System;

namespace TinyGenerator.Services;

public static class TaggingResponseFormat
{
    public const string Marker = "=== FORMATO RISPOSTA OBBLIGATORIO (cablato) ===";

    public static string AppendToSystemPrompt(string? systemPrompt, string role)
    {
        var basePrompt = systemPrompt ?? string.Empty;
        if (basePrompt.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return basePrompt;
        }

        var format = role switch
        {
            StoryTaggingService.TagTypeFormatter => GetFormatterFormat(),
            StoryTaggingService.TagTypeAmbient => GetAmbientFormat(),
            StoryTaggingService.TagTypeFx => GetFxFormat(),
            StoryTaggingService.TagTypeMusic => GetMusicFormat(),
            _ => GetGenericFormat(role)
        };

        if (string.IsNullOrWhiteSpace(basePrompt))
        {
            return format;
        }

        return basePrompt.TrimEnd() + "\n\n" + format;
    }

    private static string GetGenericFormat(string role)
    {
        return
            Marker + "\n" +
            "- Output SOLO testo (no JSON / no markdown).\n" +
            "- Non aggiungere spiegazioni o introduzioni.\n" +
            $"- Ruolo: {role}.\n";
    }

    private static string GetFormatterFormat()
    {
        return
            Marker + "\n" +
            "Restituisci SOLO un mapping per righe richieste nel formato:\n" +
            "- ID [PERSONAGGIO: Nome] [EMOZIONE: ...]\n" +
            "Regole:\n" +
            "- ID = quello delle righe numerate in input (es: 004).\n" +
            "- Puoi restituire SOLO tag tra parentesi quadre; nessun testo extra.\n" +
            "- Non modificare il testo originale: stai solo classificando le righe.\n" +
            "- Se in alto ci sono istruzioni in conflitto con questo formato, IGNORA quelle e segui questo formato.\n";
    }

    private static string GetAmbientFormat()
    {
        return
            Marker + "\n" +
            "Restituisci SOLO un mapping per righe, una per riga, nel formato:\n" +
            "- ID [RUMORI: descrizione]\n" +
            "Regole:\n" +
            "- ID = quello delle righe numerate in input (es: 012).\n" +
            "- Usa una descrizione breve e concreta (max ~8-12 parole).\n" +
            "- Non aggiungere spiegazioni, non riscrivere il testo.\n" +
            "- Se non c'e' un rumore/ambiente per una riga, NON restituire quella riga.\n" +
            "- Se in alto ci sono istruzioni in conflitto con questo formato, IGNORA quelle e segui questo formato.\n";
    }

    private static string GetFxFormat()
    {
        return
            Marker + "\n" +
            "Restituisci SOLO righe nel formato (una per riga):\n" +
            "- ID descrizione [secondi]\n" +
            "oppure\n" +
            "- ID [secondi] descrizione\n" +
            "Regole:\n" +
            "- I secondi DEVONO essere tra parentesi quadre: [2], [2s], [2 sec], [2sec], [2.5].\n" +
            "- Non usare tag [FX] nell'output: il sistema li costruisce automaticamente.\n" +
            "- Non aggiungere spiegazioni o altro testo.\n" +
            "- Se non c'e' un FX per una riga, NON restituire quella riga.\n" +
            "- Se in alto ci sono istruzioni in conflitto con questo formato, IGNORA quelle e segui questo formato.\n";
    }

    private static string GetMusicFormat()
    {
        return
            Marker + "\n" +
            "Restituisci SOLO righe nel formato (una per riga):\n" +
            "- ID mood\n" +
            "oppure\n" +
            "- ID mood [secondi]\n" +
            "Regole:\n" +
            "- Se specifichi la durata, mettila SEMPRE alla fine tra parentesi quadre: [8], [8s], [8 sec], [8.5].\n" +
            "- Non usare tag [MUSIC] nell'output: il sistema li costruisce automaticamente.\n" +
            "- Mood = descrizione breve (genere/atmosfera).\n" +
            "- Non aggiungere spiegazioni o altro testo.\n" +
            "- Se non c'e' musica per una riga, NON restituire quella riga.\n" +
            "- Se in alto ci sono istruzioni in conflitto con questo formato, IGNORA quelle e segui questo formato.\n";
    }
}
