using System.Text;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public static class SeriesEpisodePromptBuilder
{
    public static string BuildStateDrivenEpisodeTitle(Series serie, SeriesEpisode episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.Title))
        {
            return $"{serie.Titolo} - Ep {episode.Number}: {episode.Title}";
        }

        return $"{serie.Titolo} - Ep {episode.Number}";
    }

    public static string BuildStateDrivenEpisodeTheme(Series serie, SeriesEpisode episode)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# CONTESTO SERIE");
        sb.AppendLine($"Titolo: {serie.Titolo}");
        if (!string.IsNullOrWhiteSpace(serie.Genere)) sb.AppendLine($"Genere: {serie.Genere}");
        if (!string.IsNullOrWhiteSpace(serie.Sottogenere)) sb.AppendLine($"Sottogenere: {serie.Sottogenere}");
        if (!string.IsNullOrWhiteSpace(serie.PeriodoNarrativo)) sb.AppendLine($"Periodo narrativo: {serie.PeriodoNarrativo}");
        if (!string.IsNullOrWhiteSpace(serie.TonoBase)) sb.AppendLine($"Tono: {serie.TonoBase}");
        if (!string.IsNullOrWhiteSpace(serie.Lingua)) sb.AppendLine($"Lingua: {serie.Lingua}");

        if (!string.IsNullOrWhiteSpace(serie.AmbientazioneBase))
        {
            sb.AppendLine();
            sb.AppendLine("Ambientazione:");
            sb.AppendLine(serie.AmbientazioneBase);
        }

        if (!string.IsNullOrWhiteSpace(serie.PremessaSerie))
        {
            sb.AppendLine();
            sb.AppendLine("Premessa serie:");
            sb.AppendLine(serie.PremessaSerie);
        }

        if (!string.IsNullOrWhiteSpace(serie.ArcoNarrativoSerie))
        {
            sb.AppendLine();
            sb.AppendLine("Arco narrativo serie:");
            sb.AppendLine(serie.ArcoNarrativoSerie);
        }

        if (!string.IsNullOrWhiteSpace(serie.StileScrittura))
        {
            sb.AppendLine();
            sb.AppendLine("Stile scrittura:");
            sb.AppendLine(serie.StileScrittura);
        }

        if (!string.IsNullOrWhiteSpace(serie.RegoleNarrative))
        {
            sb.AppendLine();
            sb.AppendLine("Regole narrative:");
            sb.AppendLine(serie.RegoleNarrative);
        }

        sb.AppendLine();
        sb.AppendLine("# EPISODIO");
        sb.AppendLine($"Numero: {episode.Number}");
        if (!string.IsNullOrWhiteSpace(episode.Title)) sb.AppendLine($"Titolo episodio: {episode.Title}");
        if (!string.IsNullOrWhiteSpace(episode.StartSituation))
        {
            sb.AppendLine();
            sb.AppendLine("Situazione iniziale:");
            sb.AppendLine(episode.StartSituation);
        }
        if (!string.IsNullOrWhiteSpace(episode.EpisodeGoal))
        {
            sb.AppendLine();
            sb.AppendLine("Obiettivo episodio:");
            sb.AppendLine(episode.EpisodeGoal);
        }
        if (!string.IsNullOrWhiteSpace(episode.Trama))
        {
            sb.AppendLine();
            sb.AppendLine("Trama episodio:");
            sb.AppendLine(episode.Trama);
        }

        return sb.ToString();
    }
}
