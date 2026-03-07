namespace TinyGenerator.Services;

public sealed class StoryEvaluationOptions
{
    // When story length reaches this threshold, no length penalty is applied.
    public int LengthPenaltyNoPenaltyChars { get; set; } = 10000;

    // Se true, invia la storia via email quando la media valutazioni supera la soglia configurata.
    public bool SendStoryAfterEvaluation { get; set; } = false;
    public bool? send_story_after_evaluation { get; set; }

    // Soglia media (scala 0-100) oltre la quale inviare email.
    public double SendStoryAfterEvaluationThreshold { get; set; } = 60.0;
    public double? send_story_after_evaluation_threshold { get; set; }

    // Destinatari separati da ';'
    public string? SendStoryAfterEvaluationRecipients { get; set; }
    public string? send_story_after_evaluation_recipients { get; set; }

    // Config SMTP
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public string? SmtpFrom { get; set; }
}
