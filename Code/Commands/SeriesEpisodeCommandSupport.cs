using System.Text;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

internal static class SeriesEpisodeCommandSupport
{
    public static string BuildSeriesContext(
        Series serie,
        PlannerMethod? plannerMethod,
        TipoPlanning? tipoPlanning,
        string? customPrompt,
        int nextEpisodeNumber)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# CONTESTO SERIE");
        sb.AppendLine();
        sb.AppendLine($"**Titolo Serie:** {serie.Titolo}");

        if (!string.IsNullOrWhiteSpace(serie.Genere)) sb.AppendLine($"**Genere:** {serie.Genere}");
        if (!string.IsNullOrWhiteSpace(serie.Sottogenere)) sb.AppendLine($"**Sottogenere:** {serie.Sottogenere}");
        if (!string.IsNullOrWhiteSpace(serie.PeriodoNarrativo)) sb.AppendLine($"**Periodo Narrativo:** {serie.PeriodoNarrativo}");
        if (!string.IsNullOrWhiteSpace(serie.TonoBase)) sb.AppendLine($"**Tono:** {serie.TonoBase}");
        if (!string.IsNullOrWhiteSpace(serie.Target)) sb.AppendLine($"**Target:** {serie.Target}");
        if (!string.IsNullOrWhiteSpace(serie.Lingua)) sb.AppendLine($"**Lingua:** {serie.Lingua}");

        sb.AppendLine();
        sb.AppendLine("## Ambientazione");
        sb.AppendLine(!string.IsNullOrWhiteSpace(serie.AmbientazioneBase) ? serie.AmbientazioneBase : "(Non specificata)");

        sb.AppendLine();
        sb.AppendLine("## Premessa della Serie");
        sb.AppendLine(!string.IsNullOrWhiteSpace(serie.PremessaSerie) ? serie.PremessaSerie : "(Non specificata)");

        sb.AppendLine();
        sb.AppendLine("## Arco Narrativo della Serie");
        sb.AppendLine(!string.IsNullOrWhiteSpace(serie.ArcoNarrativoSerie) ? serie.ArcoNarrativoSerie : "(Non specificato)");

        sb.AppendLine();
        sb.AppendLine("## Stile di Scrittura");
        sb.AppendLine(!string.IsNullOrWhiteSpace(serie.StileScrittura) ? serie.StileScrittura : "(Non specificato)");

        sb.AppendLine();
        sb.AppendLine("## Regole Narrative");
        sb.AppendLine(!string.IsNullOrWhiteSpace(serie.RegoleNarrative) ? serie.RegoleNarrative : "(Non specificate)");

        if (!string.IsNullOrWhiteSpace(serie.SerieFinalGoal))
        {
            sb.AppendLine();
            sb.AppendLine("## Final Goal della Serie");
            sb.AppendLine(serie.SerieFinalGoal);
        }

        if (!string.IsNullOrWhiteSpace(serie.NoteAI))
        {
            sb.AppendLine();
            sb.AppendLine("## Note per l'AI");
            sb.AppendLine(serie.NoteAI);
        }

        AppendPlanningSection(sb, plannerMethod, tipoPlanning);

        sb.AppendLine();
        sb.AppendLine($"**Numero Episodio:** {nextEpisodeNumber}");

        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            sb.AppendLine();
            sb.AppendLine("## Istruzioni Specifiche per Questo Episodio");
            sb.AppendLine(customPrompt);
        }

        return sb.ToString();
    }

    public static string BuildSeriesContextForEpisode(
        Series serie,
        SeriesEpisode episode,
        List<SeriesCharacter> characters,
        PlannerMethod? plannerMethod,
        TipoPlanning? tipoPlanning)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# CONTESTO SERIE");
        sb.AppendLine();
        sb.AppendLine($"**Titolo Serie:** {serie.Titolo}");
        if (!string.IsNullOrWhiteSpace(serie.Genere)) sb.AppendLine($"**Genere:** {serie.Genere}");
        if (!string.IsNullOrWhiteSpace(serie.Sottogenere)) sb.AppendLine($"**Sottogenere:** {serie.Sottogenere}");
        if (!string.IsNullOrWhiteSpace(serie.PeriodoNarrativo)) sb.AppendLine($"**Periodo Narrativo:** {serie.PeriodoNarrativo}");
        if (!string.IsNullOrWhiteSpace(serie.TonoBase)) sb.AppendLine($"**Tono:** {serie.TonoBase}");
        if (!string.IsNullOrWhiteSpace(serie.Target)) sb.AppendLine($"**Target:** {serie.Target}");
        if (!string.IsNullOrWhiteSpace(serie.Lingua)) sb.AppendLine($"**Lingua:** {serie.Lingua}");

        sb.AppendLine();
        sb.AppendLine("## Ambientazione");
        sb.AppendLine(!string.IsNullOrWhiteSpace(serie.AmbientazioneBase) ? serie.AmbientazioneBase : "(Non specificata)");

        sb.AppendLine();
        sb.AppendLine("## Premessa della Serie");
        sb.AppendLine(!string.IsNullOrWhiteSpace(serie.PremessaSerie) ? serie.PremessaSerie : "(Non specificata)");

        sb.AppendLine();
        sb.AppendLine("## Arco Narrativo della Serie");
        sb.AppendLine(!string.IsNullOrWhiteSpace(serie.ArcoNarrativoSerie) ? serie.ArcoNarrativoSerie : "(Non specificato)");

        sb.AppendLine();
        sb.AppendLine("## Stile di Scrittura");
        sb.AppendLine(!string.IsNullOrWhiteSpace(serie.StileScrittura) ? serie.StileScrittura : "(Non specificato)");

        sb.AppendLine();
        sb.AppendLine("## Regole Narrative");
        sb.AppendLine(!string.IsNullOrWhiteSpace(serie.RegoleNarrative) ? serie.RegoleNarrative : "(Non specificate)");

        if (!string.IsNullOrWhiteSpace(serie.SerieFinalGoal))
        {
            sb.AppendLine();
            sb.AppendLine("## Final Goal della Serie");
            sb.AppendLine(serie.SerieFinalGoal);
        }

        if (!string.IsNullOrWhiteSpace(serie.NoteAI))
        {
            sb.AppendLine();
            sb.AppendLine("## Note per l'AI");
            sb.AppendLine(serie.NoteAI);
        }

        AppendPlanningSection(sb, plannerMethod, tipoPlanning);

        sb.AppendLine();
        sb.AppendLine("## Personaggi Ricorrenti");
        if (characters.Count == 0)
        {
            sb.AppendLine("(Nessun personaggio specificato)");
        }
        else
        {
            foreach (var c in characters)
            {
                sb.AppendLine($"- {c.Name} ({c.Gender})");
                if (!string.IsNullOrWhiteSpace(c.Description)) sb.AppendLine($"  Descrizione: {c.Description}");
                if (!string.IsNullOrWhiteSpace(c.Eta)) sb.AppendLine($"  Eta: {c.Eta}");
                if (!string.IsNullOrWhiteSpace(c.Formazione)) sb.AppendLine($"  Formazione: {c.Formazione}");
                if (!string.IsNullOrWhiteSpace(c.Specializzazione)) sb.AppendLine($"  Specializzazione: {c.Specializzazione}");
                if (!string.IsNullOrWhiteSpace(c.Profilo)) sb.AppendLine($"  Profilo: {c.Profilo}");
                if (!string.IsNullOrWhiteSpace(c.ConflittoInterno)) sb.AppendLine($"  Conflitto Interno: {c.ConflittoInterno}");
                if (c.EpisodeIn.HasValue || c.EpisodeOut.HasValue)
                {
                    sb.AppendLine($"  Presenza: {c.EpisodeIn?.ToString() ?? "?"} - {c.EpisodeOut?.ToString() ?? "?"}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Episodio da scrivere");
        sb.AppendLine($"**Numero:** {episode.Number}");
        if (!string.IsNullOrWhiteSpace(episode.Title)) sb.AppendLine($"**Titolo:** {episode.Title}");
        if (!string.IsNullOrWhiteSpace(episode.InitialPhase)) sb.AppendLine($"**Initial phase:** {episode.InitialPhase}");

        if (!string.IsNullOrWhiteSpace(episode.StartSituation))
        {
            sb.AppendLine();
            sb.AppendLine("### Start situation");
            sb.AppendLine(episode.StartSituation);
        }

        if (!string.IsNullOrWhiteSpace(episode.EpisodeGoal))
        {
            sb.AppendLine();
            sb.AppendLine("### Episode goal");
            sb.AppendLine(episode.EpisodeGoal);
        }

        sb.AppendLine();
        sb.AppendLine("### Trama");
        sb.AppendLine(!string.IsNullOrWhiteSpace(episode.Trama) ? episode.Trama : "(Non specificata)");
        return sb.ToString();
    }

    private static void AppendPlanningSection(StringBuilder sb, PlannerMethod? plannerMethod, TipoPlanning? tipoPlanning)
    {
        sb.AppendLine();
        sb.AppendLine("## Pianificazione");
        if (plannerMethod != null)
        {
            var descr = string.IsNullOrWhiteSpace(plannerMethod.Description) ? string.Empty : $" - {plannerMethod.Description}";
            sb.AppendLine($"**Planner method (strategico):** {plannerMethod.Code}{descr}");
        }
        else
        {
            sb.AppendLine("**Planner method (strategico):** (Non assegnato)");
        }

        if (tipoPlanning != null)
        {
            sb.AppendLine($"**Tipo planning (tattico):** {tipoPlanning.Nome} ({tipoPlanning.Codice})");
            sb.AppendLine($"**Successione stati:** {tipoPlanning.SuccessioneStati}");
            sb.AppendLine("**Stati ammessi:** AZIONE, STASI, ERRORE, EFFETTO");
        }
        else
        {
            sb.AppendLine("**Tipo planning (tattico):** (Non assegnato)");
        }
    }
}
