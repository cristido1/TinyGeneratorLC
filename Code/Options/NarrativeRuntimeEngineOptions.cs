namespace TinyGenerator.Services;

public sealed class NarrativeRuntimeEngineOptions
{
    public string EngineName { get; set; } = "NRE";
    public string DefaultMethod { get; set; } = "state_driven";
    public int DefaultMaxSteps { get; set; } = 10;
    public bool SnapshotOnFailure { get; set; } = true;
    public string SnapshotSchemaVersion { get; set; } = "nre_failure_v1";
    public string ProgressMessageGenerating { get; set; } = "Generating...";
    public int MaxEngineStepRetries { get; set; } = 0;
    public int MaxAgentFailuresBeforeStop { get; set; } = 1;
    public int PreviousBlocksWindow { get; set; } = 3;
    public int DuplicateSentenceHistoryBlocksWindow { get; set; } = 5;
    public int DuplicateSentenceMinWords { get; set; } = 8;
    public double DuplicateSentenceSimilarityThreshold { get; set; } = 0.95d;
    public double StateDrivenPlannerStepsMultiplier { get; set; } = 0.5d;
    public double SinglePassPlannerStepsMultiplier { get; set; } = 1.0d;
    public int DialogueTargetPercent { get; set; } = 40;
    public int DialogueTolerancePercentPlus { get; set; } = 5;
    public int DialogueTolerancePercentMinus { get; set; } = 5;
    public int WriterMaxPromptChars { get; set; } = 6000;
    public int WriterMaxCanonStateChars { get; set; } = 10000;
    public int WriterMaxPreviousBlocksChars { get; set; } = 12000;
    public int WriterMaxPlanChars { get; set; } = 8000;
    public string BannedPhrasesCsv { get; set; } = "Sottotitoli e revisione a cura di QTSS, Grazie per aver guardato il video";
    public int EvaluatorMinScore { get; set; } = 60;
    public int PlanEvaluatorMinScore { get; set; } = 60;
    public bool UseResponseChecker { get; set; } = true;
    public bool AllowFallback { get; set; } = true;
    public int CallCenterMaxRetries { get; set; } = 2;
    public int PlannerCallTimeoutSeconds { get; set; } = 90;
    public int WriterCallTimeoutSeconds { get; set; } = 180;
    public int EvaluatorCallTimeoutSeconds { get; set; } = 90;
    public string PlannerAgentName { get; set; } = "nre_planner";
    public string PlanEvaluatorAgentName { get; set; } = "nre_plan_evaluator";
    public string WriterAgentName { get; set; } = "nre_writer";
    public string EvaluatorAgentName { get; set; } = "nre_evaluator";
    public string ResourceInitializerAgentName { get; set; } = "resource_initializer";
    public string ResourceManagerAgentName { get; set; } = "resource_manager";
    public string PromptSuggestionAgentRole { get; set; } = "nre_prompt_proposer";
    public int PromptSuggestionTimeoutSeconds { get; set; } = 120;
    public int PromptSuggestionMaxRetries { get; set; } = 2;
    public bool PromptSuggestionAllowFallback { get; set; } = true;

    public NarrativeRuntimeEngineSnapshotOptions Snapshot { get; set; } = new();
    public NarrativeRuntimeEngineTraceOptions Trace { get; set; } = new();
    public NarrativeRuntimeEngineStoryStatusesOptions StoryStatuses { get; set; } = new();
    public NarrativeRuntimeEngineStopReasonsOptions StopReasons { get; set; } = new();
}

public sealed class NarrativeRuntimeEngineSnapshotOptions
{
    public string FolderName { get; set; } = "snapshots";
    public string FilePrefix { get; set; } = "nre";
    public string FailureSuffix { get; set; } = "fail";
    public string TimestampFormat { get; set; } = "yyyyMMdd_HHmmss";
    public int SnapshotLastBlocksCount { get; set; } = 10;
    public int SnapshotLastTraceEventsCount { get; set; } = 50;
}

public sealed class NarrativeRuntimeEngineTraceOptions
{
    public int MaxBufferEvents { get; set; } = 50;
}

public sealed class NarrativeRuntimeEngineStoryStatusesOptions
{
    public string Running { get; set; } = "running";
    public string Done { get; set; } = "done";
    public string Failed { get; set; } = "failed";
}

public sealed class NarrativeRuntimeEngineStopReasonsOptions
{
    public string Completed { get; set; } = "Completed";
    public string ValidationFailed { get; set; } = "ValidationFailed";
    public string Exception { get; set; } = "Exception";
    public string Cancelled { get; set; } = "Cancelled";
    public string MaxSteps { get; set; } = "MaxSteps";
    public string NotImplemented { get; set; } = "NotImplemented";
}
