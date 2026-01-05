using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

public static class ProsodyNormalizer
{
    private static readonly CultureInfo ItCulture = new("it-IT");

    #region Dizionari

    // Parole ambigue → forma accentata corretta
    private static readonly Dictionary<string, Func<string, string>> AmbiguousWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            {
                "ancora", context =>
                    context.Contains("nave", StringComparison.OrdinalIgnoreCase)
                        ? "àncora"
                        : "ancóra"
            },
            {
                "leggere", context =>
                    context.Contains("libro", StringComparison.OrdinalIgnoreCase) ||
                    context.Contains("rivista", StringComparison.OrdinalIgnoreCase) ||
                    context.Contains("articolo", StringComparison.OrdinalIgnoreCase) ||
                    context.Contains("storia", StringComparison.OrdinalIgnoreCase) ||
                    context.Contains("poesia", StringComparison.OrdinalIgnoreCase) ||
                    context.Contains("romanzo", StringComparison.OrdinalIgnoreCase) ||
                    context.Contains("lettura", StringComparison.OrdinalIgnoreCase)
                        ? "lèggere"
                        : "leggère"
            },
            {
                "principi", context =>
                    context.Contains("morali", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("valori", StringComparison.OrdinalIgnoreCase)
                        ? "princìpi"
                        : "prìncipi"
            },
            {
                "subito", context =>
                    context.StartsWith("ho ", StringComparison.OrdinalIgnoreCase)
                        || context.StartsWith("è ", StringComparison.OrdinalIgnoreCase)
                        ? "subìto"
                        : "sùbito"
            },
            {
                "pesca", context =>
                    context.Contains("pesce", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("amo", StringComparison.OrdinalIgnoreCase)
                        ? "pèsca"
                        : "pésca"
            },
            {
                "legge", context =>
                    context.Contains("diritto", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("norma", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("stato", StringComparison.OrdinalIgnoreCase)
                        ? "lègge"
                        : "légge"
            },
            {
                "mobile", context =>
                    context.Contains("arredo", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("casa", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("divano", StringComparison.OrdinalIgnoreCase)
                        ? "móbile"
                        : "mobìle"
            },
            {
                "salto", context =>
                    context.Contains("ha", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("è", StringComparison.OrdinalIgnoreCase)
                        ? "saltò"
                        : "sàlto"
            },
            {
                "peccato", context =>
                    context.Contains("male", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("male", StringComparison.OrdinalIgnoreCase)
                        ? "peccàto"
                        : "peccàto"
            },
                        {
                "balia", context =>
                    context.Contains("infermiera", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("bambino", StringComparison.OrdinalIgnoreCase)
                        ? "bàlia"
                        : "balìa"
            },
            {
                "abuso", context =>
                    context.Contains("maltrattamento", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("violenza", StringComparison.OrdinalIgnoreCase)
                        ? "abùso"
                        : "abúso"
            },
            {
                "esito", context =>
                    context.Contains("risultato", StringComparison.OrdinalIgnoreCase)
                        || context.Contains("fine", StringComparison.OrdinalIgnoreCase)
                        ? "èsito"
                        : "èsito"
            }
        };

    // Parole che il TTS sbaglia quasi sempre - solo le più comuni e impattanti
    private static readonly Dictionary<string, string> ForcedStressWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Core originale - Parole frequentissime che il TTS sbaglia sempre
            { "telefono", "te-le-fò-no" },
            { "automatico", "au-tò-ma-ti-co" },
            { "carattere", "ca-ràt-te-re" },
            { "difficile", "dif-fì-ci-le" },
            
            // Parole molto comuni con doppia consonante
            { "acqua", "àc-qua" },
            { "affatto", "af-fàt-to" },
            { "affetto", "af-fèt-to" },
            { "affare", "af-fà-re" },
            { "affidare", "af-fi-dà-re" },
            { "affamato", "af-fa-mà-to" },
            { "affanno", "af-fàn-no" },
            { "affannoso", "af-fan-nó-so" },
            { "affacciarsi", "af-fac-ciar-si" },
            { "affresco", "af-frès-co" },
            { "affrettare", "af-fret-tà-re" },
            { "affidamento", "af-fi-da-mèn-to" },
            { "affidabile", "af-fi-dà-bi-le" },
            { "affidabilità", "af-fi-da-bi-li-tà" },
            { "affettazione", "af-fet-ta-zi-ó-ne" },
            { "affettato", "af-fet-tà-to" },
            { "affermazione", "af-fer-ma-zi-ó-ne" },
            { "afferrare", "af-fer-rà-re" },
            { "affettuoso", "af-fet-tu-ó-so" },
            { "affettuosità", "af-fet-tu-o-si-tà" },
            { "affezionatamente", "af-fe-zio-na-ta-mèn-te" },
            { "affezionato", "af-fe-zio-nà-to" },
            
            // Parole comuni con accenti difficili
            { "adesso", "a-dès-so" },
            { "appetito", "ap-pe-tì-to" },
            { "accettazione", "ac-cet-ta-zi-ó-ne" },
            { "accettare", "ac-cet-tà-re" },
            { "accusa", "ac-cù-sa" },
            { "accendere", "ac-cèn-de-re" },
            { "accensione", "ac-cen-si-ó-ne" },
            { "accento", "ac-cèn-to" },
            { "accelerare", "ac-cel-le-rà-re" },
            { "accelerazione", "ac-cel-le-ra-zi-ó-ne" },
            { "accentramento", "ac-cen-tra-mèn-to" },
            { "accentuamento", "ac-cen-tu-a-mèn-to" },
            { "accentuato", "ac-cen-tu-à-to" },
            { "accentuazione", "ac-cen-tu-a-zi-ó-ne" },
            { "accerchiamento", "ac-cer-chia-mèn-to" },
            { "accerchiare", "ac-cer-chiá-re" },
            { "accerrimo", "ac-cèr-ri-mo" },
            { "accertamento", "ac-cer-ta-mèn-to" },
            { "accertabile", "ac-cer-tà-bi-le" },
            
            // Verbi comuni con doppia consonante
            { "appassionato", "ap-pas-si-o-nà-to" },
            { "appena", "ap-pè-na" },
            { "appendice", "ap-pèn-di-ce" },
            { "applicazione", "ap-pli-ca-zi-ó-ne" },
            { "apporto", "ap-pòr-to" },
            { "apposito", "ap-pó-si-to" },
            { "apprendimento", "ap-pren-di-mèn-to" },
            { "appresso", "ap-près-so" },
            { "appuntamento", "ap-pun-ta-mèn-to" },
            { "appunto", "ap-pùn-to" },
            
            // Aggettivi e sostantivi comuni
            { "arrabbiato", "ar-rab-bi-à-to" },
            { "arrabbiarsi", "ar-rab-bi-àr-si" },
            { "aggradamento", "ag-gra-da-mèn-to" },
            { "aggravamento", "ag-gra-va-mèn-to" },
            { "aggressione", "ag-gres-si-ó-ne" },
            { "aggressore", "ag-gres-só-re" },
            { "aggressività", "ag-gres-si-vi-tà" },
            
            // Parole con 'b'
            { "bambino", "bam-bì-no" },
            { "bambina", "bam-bì-na" },
            { "bacio", "bà-cio" },
            { "bellezza", "bel-lèz-za" },
            { "bellissimo", "bel-lìs-si-mo" },
            { "bocca", "bòc-ca" },
            { "bontà", "bon-tà" },
            
            // Parole con 'c'
            { "caso", "cà-so" },
            { "caratteristico", "ca-rat-te-rì-sti-co" },
            { "certamente", "cer-ta-mèn-te" },
            { "civile", "ci-vì-le" },
            { "civiltà", "ci-vil-tà" },
            { "coraggio", "co-ràg-gio" },
            
            // Parole con 'd'
            { "difficoltà", "dif-fi-col-tà" },
            { "diletto", "di-lèt-to" },
            { "diritto", "di-rìt-to" },
            { "donna", "dòn-na" },
            { "dottore", "dot-tó-re" },
            { "dovunque", "do-vùn-que" },
            
            // Parole con 'm'
            { "marittimo", "ma-rìt-ti-mo" },
            { "memoria", "me-mó-ria" },
            { "militare", "mi-li-tà-re" },
            { "ministro", "mi-nì-stro" },
            { "momento", "mo-mèn-to" },
            { "movimento", "movimènto" },
            { "musica", "mù-si-ca" },
            
            // Parole con 'p'
            { "parola", "pa-ró-la" },
            { "particolare", "par-ti-co-là-re" },
            { "persona", "per-só-na" },
            { "perfetto", "per-fèt-to" },
            { "perfezione", "per-fe-zi-ó-ne" },
            { "perdono", "per-dó-no" },
            { "pericolo", "pe-rì-co-lo" },
            { "periodo", "pe-rì-o-do" },
            { "poesia", "po-e-sì-a" },
            { "poeta", "po-è-ta" },
            { "politica", "po-lì-ti-ca" },
            { "popolazione", "po-po-la-zi-ó-ne" },
            { "portanza", "por-tàn-za" },
            { "possibilità", "pos-si-bi-li-tà" },
            { "potenza", "po-tèn-za" },
            { "pratica", "prà-ti-ca" },
            { "precisione", "pre-ci-zi-ó-ne" },
            { "preferenza", "pre-fe-rèn-za" },
            { "prezioso", "pre-zi-ó-so" },
            { "principale", "prin-ci-pà-le" },
            { "probabilità", "pro-ba-bi-li-tà" },
            { "procedura", "pro-ce-dù-ra" },
            { "processo", "pro-cès-so" },
            { "procura", "pro-cù-ra" },
            { "prodotto", "pro-dòt-to" },
            { "produttore", "pro-dut-tó-re" },
            { "profondo", "pro-fòn-do" },
            { "profumo", "pro-fù-mo" },
            { "programma", "pro-grám-ma" },
            { "promessa", "pro-mès-sa" },
            { "proprietà", "pro-prie-tà" },
            { "proprietario", "pro-prie-tà-rio" },
            { "proprio", "pròp-rio" },
            { "prossimità", "pros-si-mi-tà" },
            { "prossimo", "pròs-si-mo" },
            { "prova", "pró-va" },
            { "provenienza", "pro-ve-ni-èn-za" },
            { "provincia", "pro-vìn-cia" },
            { "provvidenza", "prov-vi-dèn-za" },
            { "provvigione", "prov-vi-gi-ó-ne" },
            { "provvista", "prov-vì-sta" },
            { "provvisto", "prov-vì-sto" },
            { "provocazione", "pro-vo-ca-zi-ó-ne" },
            
            // Parole con 's'
            { "simile", "sì-mi-le" },
            { "similitudine", "si-mi-li-tù-di-ne" },
            { "solidale", "so-li-dà-le" },
            { "solidarietà", "so-li-da-ri-e-tà" },
            { "soffrire", "sof-frì-re" },
            { "soffranza", "sof-frèn-za" },
            { "soffrente", "sof-frèn-te" },
            { "soffrimento", "sof-fri-mèn-to" },
            { "solido", "só-li-do" },
            { "solidità", "so-li-di-tà" },
            { "storia", "stó-ria" },
            { "storico", "stó-ri-co" },
            { "storicamente", "sto-ri-ca-mèn-te" },
            
            // Parole con 't'
            { "televisione", "te-le-vi-si-ó-ne" },
            { "televisivo", "te-le-vi-sì-vo" },
            { "televisore", "te-le-vi-só-re" },
            { "territorio", "ter-ri-tó-rio" },
            { "testimone", "te-sti-mó-ne" },
            { "testimonianza", "te-sti-mo-ni-àn-za" },
            { "titolo", "tì-to-lo" },
            { "titolare", "ti-to-là-re" },
            
            // Parole con 'v'
            { "valore", "va-ló-re" },
            { "valoroso", "va-lo-ró-so" },
            { "valutazione", "va-lu-ta-zi-ó-ne" },
            { "valutare", "va-lu-tà-re" },
            { "varietà", "va-ri-e-tà" },
            { "vario", "và-rio" },
            { "variazione", "va-ria-zi-ó-ne" },
            { "velocità", "ve-lo-ci-tà" },
            { "veloce", "ve-ló-ce" },
            { "veicolo", "vei-có-lo" },
            { "vigilanza", "vi-gi-làn-za" },
            { "vigilante", "vi-gi-làn-te" },
            { "vigore", "vi-gó-re" },
            { "vigoroso", "vi-go-ró-so" },
            
            // Parole con 'z'
            { "zitto", "zìt-to" },
            { "zero", "zé-ro" }
        };

    #endregion

    #region Entry Point

    public static string NormalizeForTTS(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        string result = text;

        result = NormalizeWhitespace(result);
        result = ResolveAmbiguousWords(result);
        result = ExpandNumbersAndTimes(result);
        result = ApplyForcedStress(result);
        result = SplitLongSentences(result);

        return result;
    }

    #endregion

    #region Step 1 - Spaziatura

    private static string NormalizeWhitespace(string text)
    {
        text = text.Trim();
        text = Regex.Replace(text, @"\s+", " ");
        return text;
    }

    #endregion

    #region Step 2 - Ambiguità lessicali

    private static string ResolveAmbiguousWords(string text)
    {
        foreach (var entry in AmbiguousWords)
        {
            string word = entry.Key;
            var resolver = entry.Value;

            text = Regex.Replace(
                text,
                $@"\b{word}\b",
                match => resolver(text),
                RegexOptions.IgnoreCase);
        }

        return text;
    }

    #endregion

    #region Step 3 - Numeri, orari, sigle

    private static string ExpandNumbersAndTimes(string text)
    {
        // Orari tipo 20:25
        text = Regex.Replace(text, @"\b(\d{1,2}):(\d{2})\b", match =>
        {
            int h = int.Parse(match.Groups[1].Value);
            int m = int.Parse(match.Groups[2].Value);

            return $"{NumberToWords(h)} e {NumberToWords(m)}";
        });

        // Anni tipo 2025
        text = Regex.Replace(text, @"\b\d{4}\b", match =>
        {
            int year = int.Parse(match.Value);
            return NumberToWords(year);
        });

        // Sigle maiuscole (CPU, SQL, RAM)
        text = Regex.Replace(text, @"\b[A-Z]{2,}\b", match =>
            string.Join("-", match.Value.Select(LetterToSyllable)));

        return text;
    }

    private static string LetterToSyllable(char c) => c switch
    {
        'A' => "à",
        'B' => "bì",
        'C' => "cì",
        'D' => "dì",
        'E' => "è",
        'F' => "èffe",
        'G' => "gì",
        'H' => "àcca",
        'I' => "ì",
        'J' => "i-lunga",
        'K' => "càppa",
        'L' => "èlle",
        'M' => "èmme",
        'N' => "ènne",
        'O' => "ò",
        'P' => "pì",
        'Q' => "cù",
        'R' => "èrre",
        'S' => "èsse",
        'T' => "tì",
        'U' => "ù",
        'V' => "vì",
        'W' => "doppia-vù",
        'X' => "ìcs",
        'Y' => "ìpsilon",
        'Z' => "zèta",
        _ => c.ToString()
    };

    private static string NumberToWords(int number)
    {
        // Soluzione semplice ma affidabile per TTS
        return number switch
        {
            < 0 => number.ToString(ItCulture),
            <= 20 => new[]
            {
                "zero","uno","due","tre","quattro","cinque","sei","sette","otto","nove",
                "dieci","undici","dodici","tredici","quattordici","quindici",
                "sedici","diciassette","diciotto","diciannove","venti"
            }[number],
            _ => number.ToString(ItCulture)
        };
    }

    #endregion

    #region Step 4 - Accenti forzati

    private static string ApplyForcedStress(string text)
    {
        foreach (var kvp in ForcedStressWords)
        {
            // Use the dictionary value but strip hyphen syllable separators when applying
            var replacement = kvp.Value?.Replace("-", "") ?? string.Empty;
            text = Regex.Replace(
                text,
                $@"\b{kvp.Key}\b",
                replacement,
                RegexOptions.IgnoreCase);
        }

        return text;
    }

    #endregion

    #region Step 5 - Spezzatura frasi lunghe

    private static string SplitLongSentences(string text)
    {
        var sentences = Regex.Split(text, @"(?<=[\.\!\?])\s+");
        var sb = new StringBuilder();

        foreach (var s in sentences)
        {
            var words = s.Split(' ');
            if (words.Length <= 25)
            {
                sb.Append(s).Append(" ");
                continue;
            }

            // Spezza su virgole
            var parts = s.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                sb.Append(parts[i].Trim());
                sb.Append(i < parts.Length - 1 ? ". " : " ");
            }
        }

        return sb.ToString().Trim();
    }

    #endregion
}