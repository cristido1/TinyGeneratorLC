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
            "Restituisci SOLO un JSON valido nel formato richiesto dalla request.\n" +
            "Regole:\n" +
            "- Nessun markdown, nessun testo extra.\n" +
            "- Compila solo i campi previsti dallo schema.\n";
    }

    private static string GetFxFormat()
    {
        return
            "Restituisci SOLO un JSON valido nel formato richiesto dalla request.\n" +
            "Regole:\n" +
            "- Nessun markdown, nessun testo extra.\n" +
            "- Compila solo i campi previsti dallo schema.\n" +
            "- Se nel chunk non sono necessari effetti sonori, restituisci esplicitamente: {\"entries\":[]}.\n";
    }

    private static string GetMusicFormat()
    {
        return
            "Restituisci SOLO un JSON valido nel formato richiesto dalla request.\n" +
            "Regole:\n" +
            "- Nessun markdown, nessun testo extra.\n" +
            "- Compila solo i campi previsti dallo schema.\n" +
            "- Il primo tag resta uno tra: " + string.Join(", ", AllowedPrimaryMusicTags) + ".\n";
    }
}
