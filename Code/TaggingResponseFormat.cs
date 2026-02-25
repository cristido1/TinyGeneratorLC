using System;

namespace TinyGenerator.Services;

public static class TaggingResponseFormat
{
    public const string Marker = "=== FORMATO RISPOSTA OBBLIGATORIO (cablato) ===";
    private static readonly string[] AllowedPrimaryMusicTags =
    {
        "opening", "ending", "transition", "mystery", "suspense", "tension",
        "love", "combat", "activity", "exploration", "victory", "defeat",
        "aftermath", "ambient", "silence"
    };

    public static string AppendToSystemPrompt(string? systemPrompt, string role)
    {
        var basePrompt = RemoveLegacyMarker(systemPrompt ?? string.Empty).TrimEnd();

        var format = role switch
        {
            StoryTaggingService.TagTypeFormatter => GetFormatterFormat(),
            StoryTaggingService.TagTypeAmbient => GetAmbientFormat(),
            StoryTaggingService.TagTypeFx => GetFxFormat(),
            StoryTaggingService.TagTypeMusic => GetMusicFormat(),
            _ => GetGenericFormat(role)
        };

        if (string.IsNullOrWhiteSpace(format))
        {
            return basePrompt;
        }

        if (string.IsNullOrWhiteSpace(basePrompt))
        {
            return format;
        }

        return basePrompt.TrimEnd() + "\n\n" + format;
    }

    private static string RemoveLegacyMarker(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return prompt;

        return prompt.Replace(Marker + "\n", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(Marker + "\r\n", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(Marker, string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetGenericFormat(string role)
    {
        return
            "- Output SOLO testo (no JSON / no markdown).\n" +
            "- Non aggiungere spiegazioni o introduzioni.\n" +
            $"- Ruolo: {role}.\n";
    }

    private static string GetFormatterFormat()
    {
        return
            "Restituisci SOLO un mapping per righe richieste nel formato:\n" +
            "ID [PERSONAGGIO: Nome] [EMOZIONE: ...]\n" +
            "Regole:\n" +
            "- ID = quello delle righe numerate in input (es: 004).\n" +
            "- Puoi restituire SOLO tag tra parentesi quadre; nessun testo extra.\n" +
            "- Non modificare il testo originale: stai solo classificando le righe.\n" +
            "- Se in alto ci sono istruzioni in conflitto con questo formato, IGNORA quelle e segui questo formato.\n";
    }

    private static string GetAmbientFormat()
    {
        return
            "Restituisci SOLO righe nel formato (una per riga):\n" +
            "ID || tag1, tag2, tag3\n" +
            "Regole:\n" +
            "- Usa SEMPRE il separatore '||' seguito da una lista di tag CSV.\n" +
            "- Restituisci SOLO tag (nessuna descrizione libera).\n" +
            "- I tag devono essere in inglese, lowercase, ordinati dal piu importante al meno importante, separati da virgola.\n" +
            "- Inserisci PIU' tag (non uno solo), quando possibile.\n" +
            "- Non aggiungere spiegazioni o altro testo.\n" +
            "- Se non c'e' rumore ambientale per una riga, NON restituire quella riga.\n" +
            "- Se in alto ci sono istruzioni in conflitto con questo formato, IGNORA quelle e segui questo formato.\n";
    }

    private static string GetFxFormat()
    {
        return
            "Restituisci SOLO righe nel formato (una per riga):\n" +
            "ID [secondi] || tag1, tag2, tag3\n" +
            "Regole:\n" +
            "- I secondi DEVONO essere tra parentesi quadre: [2], [2s], [2 sec], [2sec], [2.5].\n" +
            "- Usa SEMPRE il separatore '||' seguito da una lista di tag CSV.\n" +
            "- Restituisci SOLO tag (nessuna descrizione libera dell'effetto).\n" +
            "- I tag devono essere in inglese, lowercase, ordinati dal piu importante al meno importante, separati da virgola.\n" +
            "- Inserisci PIU' tag (non uno solo), quando possibile.\n" +
            "- Non usare tag [FX] nell'output: il sistema li costruisce automaticamente.\n" +
            "- Non aggiungere spiegazioni o altro testo.\n" +
            "- Se non c'e' un FX per una riga, NON restituire quella riga.\n" +
            "- Se in alto ci sono istruzioni in conflitto con questo formato, IGNORA quelle e segui questo formato.\n";
    }

    private static string GetMusicFormat()
    {
        return
            "Restituisci SOLO righe nel formato (una per riga):\n" +
            "ID || tag1, tag2, tag3\n" +
            "oppure\n" +
            "ID [secondi] || tag1, tag2, tag3\n" +
            "Regole:\n" +
            "- Se specifichi la durata, mettila tra parentesi quadre vicino all'ID: [8], [8s], [8 sec], [8.5].\n" +
            "- Usa SEMPRE il separatore '||' seguito da una lista di tag CSV.\n" +
            "- Restituisci SOLO tag (nessuna descrizione/mood libera).\n" +
            "- I tag devono essere in inglese, lowercase, ordinati dal piu importante al meno importante, separati da virgola.\n" +
            "- Il PRIMO tag DEVE essere uno tra: " + string.Join(", ", AllowedPrimaryMusicTags) + ".\n" +
            "- Puoi aggiungere altri tag dopo il primo (liberi ma pertinenti).\n" +
            "- Non aggiungere spiegazioni o altro testo.\n" +
            "- Se non c'e' musica per una riga, NON restituire quella riga.\n" +
            "- Se in alto ci sono istruzioni in conflitto con questo formato, IGNORA quelle e segui questo formato.\n";
    }
}
