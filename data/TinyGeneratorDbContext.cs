using Microsoft.EntityFrameworkCore;
using TinyGenerator.Models;

namespace TinyGenerator.Data;

public class TinyGeneratorDbContext : DbContext
{
    public TinyGeneratorDbContext(DbContextOptions<TinyGeneratorDbContext> options)
        : base(options)
    {
    }

    // DbSets using existing table names via [Table] attribute on models
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<ModelInfo> Models => Set<ModelInfo>();
    public DbSet<StoryRecord> Stories => Set<StoryRecord>();
    public DbSet<StoryEvaluation> StoryEvaluations => Set<StoryEvaluation>();
    public DbSet<StoryStatus> StoriesStatus => Set<StoryStatus>();
    public DbSet<TestDefinition> TestDefinitions => Set<TestDefinition>();
    public DbSet<StepTemplate> StepTemplates => Set<StepTemplate>();
    public DbSet<TaskTypeInfo> TaskTypes => Set<TaskTypeInfo>();
    public DbSet<TtsVoice> TtsVoices => Set<TtsVoice>();
    public DbSet<LogEntry> Logs => Set<LogEntry>();
    // Note: Memory table excluded - using Dapper for embedding queries

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Minimal configuration - rely on DataAnnotations on model classes
        // Only configure relationships if needed
    }
}
