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
    public DbSet<Series> Series => Set<Series>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<UsageState> UsageStates => Set<UsageState>();
    public DbSet<ModelTestRun> ModelTestRuns => Set<ModelTestRun>();
    public DbSet<TestPrompt> TestPrompts => Set<TestPrompt>();
    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();
    public DbSet<TaskExecutionStep> TaskExecutionSteps => Set<TaskExecutionStep>();
    public DbSet<ChunkFacts> ChunkFacts => Set<ChunkFacts>();
    public DbSet<CoherenceScore> CoherenceScores => Set<CoherenceScore>();
    public DbSet<GlobalCoherence> GlobalCoherences => Set<GlobalCoherence>();
    public DbSet<LogAnalysis> LogAnalyses => Set<LogAnalysis>();
    public DbSet<AppEventDefinition> AppEvents => Set<AppEventDefinition>();
    // Note: Memory table excluded - using Dapper for embedding queries

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Minimal configuration - rely on DataAnnotations on model classes
        // Foreign keys with SetNull are handled by shadow keys, not explicit FK constraints for SQLite compatibility
    }
}
