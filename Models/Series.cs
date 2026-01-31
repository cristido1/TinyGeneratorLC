using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

/// <summary>
/// Serie TV / Racconti: struttura separata tra campi di filtraggio/catalogo e campi per generazione episodi.
/// Personaggi ed eventi specifici NON vanno qui ma negli Episodi.
/// </summary>
[Table("series")]
public sealed class Series
{
    // ==========================================
    // CAMPI DI FILTRAGGIO / CATALOGO
    // ==========================================
    
    [Column("id")]
    [Key]
    public int Id { get; set; }
    
    [Column("titolo")]
    [Required]
    [MaxLength(200)]
    public string Titolo { get; set; } = string.Empty;
    
    [Column("genere")]
    [MaxLength(100)]
    public string? Genere { get; set; }
    
    [Column("sottogenere")]
    [MaxLength(100)]
    public string? Sottogenere { get; set; }
    
    [Column("periodo_narrativo")]
    [MaxLength(100)]
    public string? PeriodoNarrativo { get; set; }
    
    [Column("tono_base")]
    [MaxLength(100)]
    public string? TonoBase { get; set; }
    
    [Column("target")]
    [MaxLength(50)]
    public string? Target { get; set; }
    
    [Column("lingua")]
    [MaxLength(50)]
    public string? Lingua { get; set; } = "Italiano";

    // ==========================================
    // ASSET / FILESYSTEM
    // ==========================================

    /// <summary>
    /// Optional folder name under series_folder/ (e.g. 0001_mia_serie). Used to store series-consistent assets (music, etc.).
    /// </summary>
    [Column("folder")]
    [MaxLength(255)]
    public string? Folder { get; set; }
    
    // ==========================================
    // CAMPI PER GENERAZIONE EPISODI (PROMPT WRITER)
    // ==========================================
    
    [Column("ambientazione_base")]
    public string? AmbientazioneBase { get; set; }
    
    [Column("premessa_serie")]
    public string? PremessaSerie { get; set; }
    
    [Column("arco_narrativo_serie")]
    public string? ArcoNarrativoSerie { get; set; }
    
    [Column("stile_scrittura")]
    public string? StileScrittura { get; set; }
    
    [Column("images_style")]
    public string? ImagesStyle { get; set; }
    
    [Column("regole_narrative")]
    public string? RegoleNarrative { get; set; }

    [Column("serie_final_goal")]
    public string? SerieFinalGoal { get; set; }

    // Narrative Engine extensions
    [Column("default_narrative_profile_id")]
    public int? DefaultNarrativeProfileId { get; set; }

    /// <summary>
    /// Default planner mode for stories in this series: Off / Assist / Auto.
    /// </summary>
    [Column("default_planner_mode")]
    [MaxLength(20)]
    public string? DefaultPlannerMode { get; set; }

    [Column("narrative_consistency_level")]
    public int NarrativeConsistencyLevel { get; set; } = 0;
    
    [Column("note_ai")]
    public string? NoteAI { get; set; }

    [Column("serie_state_summary")]
    public string? SerieStateSummary { get; set; }

    [Column("last_major_event")]
    public string? LastMajorEvent { get; set; }

    [Column("cosa_non_deve_mai_succedere")]
    public string? CosaNonDeveMaiSuccedere { get; set; }

    [Column("temi_obbligatori")]
    public string? TemiObbligatori { get; set; }

    [Column("livello_tecnologico_medio")]
    public string? LivelloTecnologicoMedio { get; set; }

    [Column("world_rules_locked")]
    public bool WorldRulesLocked { get; set; } = false;

    // ==========================================
    // PLANNING (Strategico/Tattico)
    // ==========================================

    /// <summary>
    /// Strategic planning framework for the whole series (planner_methods.id)
    /// </summary>
    [Column("planner_method_id")]
    public int? PlannerMethodId { get; set; }

    /// <summary>
    /// Default tactical planning grammar for episodes in this series (tipo_planning.id_tipo_planning)
    /// </summary>
    [Column("default_tipo_planning_id")]
    public int? DefaultTipoPlanningId { get; set; }
    
    // ==========================================
    // METADATI
    // ==========================================
    
    [Column("episodi_generati")]
    public int EpisodiGenerati { get; set; } = 0;
    
    [Column("data_inserimento")]
    public DateTime DataInserimento { get; set; } = DateTime.UtcNow;
    
    [Column("timestamp")]
    [Timestamp]
    public byte[]? Timestamp { get; set; }

    public List<SeriesCharacter> Characters { get; set; } = new();
    public List<SeriesEpisode> Episodes { get; set; } = new();
}
