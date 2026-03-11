using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TinyGenerator.Models;

namespace TinyGenerator.Data;

public class TinyGeneratorDbContext : DbContext
{
    public TinyGeneratorDbContext(DbContextOptions<TinyGeneratorDbContext> options)
        : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Suppress PendingModelChangesWarning - we're manually managing migrations
        optionsBuilder.ConfigureWarnings(warnings =>
            warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    // DbSets using existing table names via [Table] attribute on models
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<ModelInfo> Models => Set<ModelInfo>();
    public DbSet<StoryRecord> Stories => Set<StoryRecord>();
    public DbSet<StoryEvaluation> StoryEvaluations => Set<StoryEvaluation>();
    public DbSet<StoryStatus> StoriesStatus => Set<StoryStatus>();
    public DbSet<TestDefinition> TestDefinitions => Set<TestDefinition>();
    public DbSet<ModelTestStep> ModelTestSteps => Set<ModelTestStep>();
    public DbSet<ModelTestAsset> ModelTestAssets => Set<ModelTestAsset>();
    public DbSet<StepTemplate> StepTemplates => Set<StepTemplate>();
    public DbSet<TaskTypeInfo> TaskTypes => Set<TaskTypeInfo>();
    public DbSet<TtsVoice> TtsVoices => Set<TtsVoice>();
    public DbSet<LogEntry> Logs => Set<LogEntry>();
    public DbSet<SystemReport> SystemReports => Set<SystemReport>();
    public DbSet<Series> Series => Set<Series>();
    public DbSet<SeriesEpisode> SeriesEpisodes => Set<SeriesEpisode>();
    public DbSet<SeriesCharacter> SeriesCharacters => Set<SeriesCharacter>();
    public DbSet<SeriesState> SeriesStates => Set<SeriesState>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<UsageState> UsageStates => Set<UsageState>();
    public DbSet<ModelTestRun> ModelTestRuns => Set<ModelTestRun>();
    public DbSet<TestPrompt> TestPrompts => Set<TestPrompt>();
    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();
    public DbSet<TaskExecutionStep> TaskExecutionSteps => Set<TaskExecutionStep>();
    public DbSet<GenericLookupEntry> GenericLookupEntries => Set<GenericLookupEntry>();
    public DbSet<NameGenderEntry> NameGenderEntries => Set<NameGenderEntry>();
    public DbSet<ChunkFacts> ChunkFacts => Set<ChunkFacts>();
    public DbSet<CoherenceScore> CoherenceScores => Set<CoherenceScore>();
    public DbSet<GlobalCoherence> GlobalCoherences => Set<GlobalCoherence>();
    public DbSet<LogAnalysis> LogAnalyses => Set<LogAnalysis>();
    public DbSet<AppEventDefinition> AppEvents => Set<AppEventDefinition>();
    public DbSet<ModelStatsRecord> ModelStats => Set<ModelStatsRecord>();
    public DbSet<PlannerMethod> PlannerMethods => Set<PlannerMethod>();
    public DbSet<TipoPlanning> TipoPlannings => Set<TipoPlanning>();
    public DbSet<NarrativeProfile> NarrativeProfiles => Set<NarrativeProfile>();
    public DbSet<NarrativeResource> NarrativeResources => Set<NarrativeResource>();
    public DbSet<MicroObjective> MicroObjectives => Set<MicroObjective>();
    public DbSet<FailureRule> FailureRules => Set<FailureRule>();
    public DbSet<ConsequenceRule> ConsequenceRules => Set<ConsequenceRule>();
    public DbSet<ConsequenceImpact> ConsequenceImpacts => Set<ConsequenceImpact>();
    public DbSet<StoryResourceState> StoryResourceStates => Set<StoryResourceState>();
    public DbSet<NarrativeContinuityState> NarrativeContinuityStates => Set<NarrativeContinuityState>();
    public DbSet<NarrativeStoryBlock> NarrativeStoryBlocks => Set<NarrativeStoryBlock>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<ModelRole> ModelRoles => Set<ModelRole>();
    public DbSet<ModelRoleError> ModelRoleErrors => Set<ModelRoleError>();
    public DbSet<ImageAsset> Images => Set<ImageAsset>();
    public DbSet<NumeratorStateEntry> NumeratorsState => Set<NumeratorStateEntry>();
    public DbSet<MetadataTableEntry> MetadataTables => Set<MetadataTableEntry>();
    public DbSet<MetadataFieldEntry> MetadataFields => Set<MetadataFieldEntry>();
    public DbSet<CommandEntry> Commands => Set<CommandEntry>();
    public DbSet<MetadataCommandEntry> MetadataCommands => Set<MetadataCommandEntry>();
    // Note: Memory table excluded - using Dapper for embedding queries

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Minimal configuration - rely on DataAnnotations on model classes
        // Foreign keys with SetNull are handled by shadow keys, not explicit FK constraints for SQLite compatibility
        modelBuilder.Entity<SeriesCharacter>()
            .HasOne<Series>()
            .WithMany(s => s.Characters)
            .HasForeignKey(sc => sc.SerieId);

        modelBuilder.Entity<SeriesEpisode>()
            .HasOne<Series>()
            .WithMany(s => s.Episodes)
            .HasForeignKey(se => se.SerieId);

        modelBuilder.Entity<Agent>()
            .HasOne<ModelInfo>()
            .WithMany()
            .HasForeignKey(a => a.ModelId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Agent>()
            .HasOne<TtsVoice>()
            .WithMany()
            .HasForeignKey(a => a.VoiceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Agent>()
            .HasOne<StepTemplate>()
            .WithMany()
            .HasForeignKey(a => a.MultiStepTemplateId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<SeriesState>()
            .HasIndex(ss => ss.SerieId);

        modelBuilder.Entity<Series>()
            .HasOne<PlannerMethod>()
            .WithMany()
            .HasForeignKey(s => s.PlannerMethodId);

        modelBuilder.Entity<Series>()
            .HasOne<TipoPlanning>()
            .WithMany()
            .HasForeignKey(s => s.DefaultTipoPlanningId);

        modelBuilder.Entity<SeriesEpisode>()
            .HasOne<TipoPlanning>()
            .WithMany()
            .HasForeignKey(e => e.TipoPlanningId);

        modelBuilder.Entity<TipoPlanning>()
            .HasIndex(t => t.Codice)
            .IsUnique();

        modelBuilder.Entity<PlannerMethod>()
            .HasIndex(m => m.Code)
            .IsUnique();

        // Narrative Engine relations
        modelBuilder.Entity<NarrativeResource>()
            .HasOne(r => r.NarrativeProfile)
            .WithMany(p => p.Resources)
            .HasForeignKey(r => r.NarrativeProfileId);

        modelBuilder.Entity<MicroObjective>()
            .HasOne(r => r.NarrativeProfile)
            .WithMany(p => p.MicroObjectives)
            .HasForeignKey(r => r.NarrativeProfileId);

        modelBuilder.Entity<FailureRule>()
            .HasOne(r => r.NarrativeProfile)
            .WithMany(p => p.FailureRules)
            .HasForeignKey(r => r.NarrativeProfileId);

        modelBuilder.Entity<ConsequenceRule>()
            .HasOne(r => r.NarrativeProfile)
            .WithMany(p => p.ConsequenceRules)
            .HasForeignKey(r => r.NarrativeProfileId);

        modelBuilder.Entity<ConsequenceImpact>()
            .HasOne(i => i.ConsequenceRule)
            .WithMany(r => r.Impacts)
            .HasForeignKey(i => i.ConsequenceRuleId);

        modelBuilder.Entity<StoryResourceState>()
            .HasIndex(s => new { s.StoryId, s.ChunkIndex });

        modelBuilder.Entity<StoryResourceState>()
            .HasIndex(s => s.SeriesId);

        // Narrative Engine seed data (from usage_narrative_engine.txt)
        modelBuilder.Entity<NarrativeProfile>().HasData(
            new NarrativeProfile
            {
                Id = 1,
                Name = "SciFi Militare",
                Description = "Conflitto armato ad alta tensione",
                BaseSystemPrompt = "Scrivi narrativa a chunk continuo senza conclusioni.",
                StylePrompt = "Tono tecnico, militare, concreto."
            }
        );

        modelBuilder.Entity<NarrativeResource>().HasData(
            new NarrativeResource { Id = 1, NarrativeProfileId = 1, Name = "Energia", InitialValue = 100, MinValue = 0, MaxValue = 100 },
            new NarrativeResource { Id = 2, NarrativeProfileId = 1, Name = "Integrità", InitialValue = 100, MinValue = 0, MaxValue = 100 },
            new NarrativeResource { Id = 3, NarrativeProfileId = 1, Name = "Uomini", InitialValue = 100, MinValue = 0, MaxValue = 100 }
        );

        modelBuilder.Entity<MicroObjective>().HasData(
            new MicroObjective { Id = 1, NarrativeProfileId = 1, Code = "DEFEND", Description = "Difendere un settore critico", Difficulty = 2 },
            new MicroObjective { Id = 2, NarrativeProfileId = 1, Code = "DELAY", Description = "Guadagnare tempo sotto pressione", Difficulty = 3 }
        );

        modelBuilder.Entity<FailureRule>().HasData(
            new FailureRule { Id = 1, NarrativeProfileId = 1, Description = "Decisione affrettata sotto pressione", TriggerType = "RandomUnderPressure" },
            new FailureRule { Id = 2, NarrativeProfileId = 1, Description = "Risorsa critica sotto soglia", TriggerType = "ResourceBelowThreshold" }
        );

        modelBuilder.Entity<ConsequenceRule>().HasData(
            new ConsequenceRule { Id = 1, NarrativeProfileId = 1, Description = "Perdita di uomini" },
            new ConsequenceRule { Id = 2, NarrativeProfileId = 1, Description = "Danni strutturali" }
        );

        modelBuilder.Entity<ConsequenceImpact>().HasData(
            new ConsequenceImpact { Id = 1, ConsequenceRuleId = 1, ResourceName = "Uomini", DeltaValue = -10 },
            new ConsequenceImpact { Id = 2, ConsequenceRuleId = 2, ResourceName = "Integrità", DeltaValue = -15 }
        );

        // Roles and ModelRoles relationships
        modelBuilder.Entity<ModelRole>()
            .HasOne(mr => mr.Model)
            .WithMany()
            .HasForeignKey(mr => mr.ModelId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ModelRole>()
            .HasOne(mr => mr.Role)
            .WithMany()
            .HasForeignKey(mr => mr.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ModelRole>()
            .HasOne(mr => mr.Agent)
            .WithMany()
            .HasForeignKey(mr => mr.AgentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Role>()
            .HasIndex(r => r.Name)
            .IsUnique();

        modelBuilder.Entity<ModelStatsRecord>()
            .HasKey(s => s.Id);

        modelBuilder.Entity<ModelStatsRecord>()
            .HasIndex(s => new { s.ModelName, s.Operation })
            .IsUnique();

        modelBuilder.Entity<NumeratorStateEntry>()
            .HasKey(n => n.Id);

        modelBuilder.Entity<NumeratorStateEntry>()
            .HasIndex(n => n.Key)
            .IsUnique();

        modelBuilder.Entity<AppEventDefinition>()
            .HasKey(a => a.Id);

        modelBuilder.Entity<GenericLookupEntry>()
            .HasKey(g => g.Id);

        modelBuilder.Entity<ImageAsset>()
            .HasKey(i => i.Id);

        modelBuilder.Entity<MetadataTableEntry>()
            .HasKey(m => m.Id);

        modelBuilder.Entity<MetadataFieldEntry>()
            .HasKey(m => m.Id);

        modelBuilder.Entity<CommandEntry>()
            .HasKey(c => c.Id);

        modelBuilder.Entity<MetadataCommandEntry>()
            .HasKey(m => m.Id);

        modelBuilder.Entity<MetadataCommandEntry>()
            .HasOne<CommandEntry>()
            .WithMany()
            .HasForeignKey(m => m.CommandId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MetadataCommandEntry>()
            .HasOne<MetadataTableEntry>()
            .WithMany()
            .HasForeignKey(m => m.TableId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MetadataFieldEntry>()
            .HasOne<MetadataTableEntry>()
            .WithMany()
            .HasForeignKey(m => m.ParentTableId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MetadataTableEntry>()
            .HasOne<MetadataTableEntry>()
            .WithMany()
            .HasForeignKey(m => m.ChildTableId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<LogEntry>()
            .HasKey(l => l.Id);

        modelBuilder.Entity<ModelInfo>()
            .HasKey(m => m.Id);

        // Seed default roles
        var now = DateTime.UtcNow.ToString("o");
        modelBuilder.Entity<Role>().HasData(
            new Role { Id = 1, Name = "writer", LinkedCommand = "FullStoryPipelineCommand", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 2, Name = "formatter", LinkedCommand = "AddVoiceTagsToStoryCommand", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 3, Name = "evaluator", LinkedCommand = "StoryEvaluation", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 4, Name = "tts_expert", LinkedCommand = "TtsGeneration", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 5, Name = "music_expert", LinkedCommand = "MusicGeneration", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 6, Name = "fx_expert", LinkedCommand = "FxGeneration", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 7, Name = "summarizer", LinkedCommand = "SummarizeStory", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 8, Name = "canon_extractor", LinkedCommand = "CanonExtractor", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 9, Name = "state_delta_builder", LinkedCommand = "StateDeltaBuilder", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 10, Name = "continuity_validator", LinkedCommand = "ContinuityValidator", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 11, Name = "state_updater", LinkedCommand = "StateUpdater", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 12, Name = "state_compressor", LinkedCommand = "StateCompressor", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 13, Name = "recap_builder", LinkedCommand = "RecapBuilder", CreatedAt = now, UpdatedAt = now },
            new Role { Id = 14, Name = "writer_cino", LinkedCommand = "cino_optimize_story", CreatedAt = now, UpdatedAt = now }
        );
    }
}

