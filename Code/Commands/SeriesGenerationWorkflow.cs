using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

internal sealed class SeriesGenerationWorkflow
{
    private readonly SeriesGenerationOptions _options;
    private readonly SeriesTagParser _parser;
    private readonly SeriesValidationRules _validationRules;
    private readonly SeriesAgentCaller _agentCaller;

    public SeriesGenerationWorkflow(
        SeriesGenerationOptions options,
        SeriesTagParser parser,
        SeriesValidationRules validationRules,
        SeriesAgentCaller agentCaller)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _validationRules = validationRules ?? throw new ArgumentNullException(nameof(validationRules));
        _agentCaller = agentCaller ?? throw new ArgumentNullException(nameof(agentCaller));
    }

    public async Task<SeriesWorkflowResult> ExecuteAsync(
        string userPrompt,
        string runId,
        IReadOnlyDictionary<string, Agent> agentsByRole,
        Action<int, int, string, Agent>? reportStep,
        CancellationToken ct)
    {
        var context = new SeriesWorkflowContext(userPrompt);

        var bibleStep = new SeriesWorkflowStep(
            RoleCode: CommandRoleCodes.SerieBibleAgent,
            OutputKey: "bible_tags",
            PhaseLabel: "Serie Bible",
            RequiredTags: _options.Validation.EnableBibleValidation ? SeriesValidationRules.RequiredBibleTags : Array.Empty<string>(),
            ValidationFunc: _options.Validation.EnableBibleValidation ? _validationRules.ValidateBibleOutput : null,
            Options: _options.Bible,
            InputFactory: ctx => BuildPrompt(
                agentsByRole,
                CommandRoleCodes.SerieBibleAgent,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["USER_PROMPT"] = ctx.UserPrompt
                }));

        var bible = await ExecuteStepAsync(bibleStep, context, runId, 1, 5, agentsByRole, reportStep, ct).ConfigureAwait(false);
        if (!bible.Success)
        {
            return SeriesWorkflowResult.Fail(bible.Error ?? "Serie bible fallita");
        }

        var charactersStep = new SeriesWorkflowStep(
            RoleCode: CommandRoleCodes.SerieCharacterAgent,
            OutputKey: "character_tags",
            PhaseLabel: "Serie Characters",
            RequiredTags: _options.Validation.EnableCharactersValidation ? SeriesValidationRules.RequiredCharacterTags : Array.Empty<string>(),
            ValidationFunc: _options.Validation.EnableCharactersValidation ? _validationRules.ValidateCharactersOutput : null,
            Options: _options.Characters,
            InputFactory: ctx => BuildPrompt(
                agentsByRole,
                CommandRoleCodes.SerieCharacterAgent,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SERIE_BIBLE"] = ctx.Get("bible_tags")
                }));

        var characters = await ExecuteStepAsync(charactersStep, context, runId, 2, 5, agentsByRole, reportStep, ct).ConfigureAwait(false);
        if (!characters.Success)
        {
            return SeriesWorkflowResult.Fail(characters.Error ?? "Serie characters fallita");
        }

        var seasonStep = new SeriesWorkflowStep(
            RoleCode: CommandRoleCodes.SerieSeasonAgent,
            OutputKey: "season_tags",
            PhaseLabel: "Serie Season",
            RequiredTags: _options.Validation.EnableSeasonValidation ? SeriesValidationRules.RequiredSeasonTags : Array.Empty<string>(),
            ValidationFunc: _options.Validation.EnableSeasonValidation ? _validationRules.ValidateSeasonOutput : null,
            Options: _options.Episodes,
            InputFactory: ctx => BuildPrompt(
                agentsByRole,
                CommandRoleCodes.SerieSeasonAgent,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SERIE_BIBLE"] = ctx.Get("bible_tags"),
                    ["CHARACTERS"] = ctx.Get("character_tags")
                }));

        var season = await ExecuteStepAsync(seasonStep, context, runId, 3, 5, agentsByRole, reportStep, ct).ConfigureAwait(false);
        if (!season.Success)
        {
            return SeriesWorkflowResult.Fail(season.Error ?? "Serie season fallita");
        }

        var baseEpisodes = _parser.ParseEpisodeBlocks(context.Get("season_tags"));
        if (baseEpisodes.Count == 0)
        {
            return SeriesWorkflowResult.Fail("Nessun episodio base generato dal serie_season_agent");
        }

        var orderedEpisodes = baseEpisodes.OrderBy(e => e.Number).ToList();
        var episodeStructures = new List<string>();
        var totalSteps = 4 + orderedEpisodes.Count;
        var episodeStepNumber = 4;

        foreach (var episode in orderedEpisodes)
        {
            ct.ThrowIfCancellationRequested();
            var episodeNumber = episode.Number;
            var episodeStep = new SeriesWorkflowStep(
                RoleCode: CommandRoleCodes.SerieEpisodeAgent,
                OutputKey: $"episode_{episodeNumber}",
                PhaseLabel: $"Serie Episode {episodeNumber:00}",
                RequiredTags: _options.Validation.EnableEpisodeStructureValidation ? SeriesValidationRules.RequiredEpisodeStructureTags : Array.Empty<string>(),
                ValidationFunc: _options.Validation.EnableEpisodeStructureValidation
                    ? text => _validationRules.ValidateEpisodeStructureOutput(text, episodeNumber)
                    : null,
                Options: _options.Episodes,
                InputFactory: ctx => BuildPrompt(
                    agentsByRole,
                    CommandRoleCodes.SerieEpisodeAgent,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["EPISODE_BASE"] = episode.RawBlock,
                        ["SERIE_BIBLE"] = ctx.Get("bible_tags"),
                        ["CHARACTERS"] = ctx.Get("character_tags")
                    }));

            var episodeResult = await ExecuteStepAsync(
                episodeStep,
                context,
                runId,
                episodeStepNumber,
                totalSteps,
                agentsByRole,
                reportStep,
                ct).ConfigureAwait(false);

            if (!episodeResult.Success)
            {
                return SeriesWorkflowResult.Fail(episodeResult.Error ?? $"Serie episode fallita (episodio {episodeNumber})");
            }

            var episodeText = context.Get(episodeStep.OutputKey);
            episodeStructures.Add(episodeText);
            episodeStepNumber++;
        }

        var validatorStep = new SeriesWorkflowStep(
            RoleCode: CommandRoleCodes.SerieValidatorAgent,
            OutputKey: "validator_tags",
            PhaseLabel: "Serie Validator",
            RequiredTags: _validationRules.RequiresValidationOkTag() ? SeriesValidationRules.RequiredValidatorTags : Array.Empty<string>(),
            ValidationFunc: null,
            Options: _options.Validator,
            InputFactory: ctx => BuildPrompt(
                agentsByRole,
                CommandRoleCodes.SerieValidatorAgent,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SERIE_BIBLE"] = ctx.Get("bible_tags"),
                    ["CHARACTERS"] = ctx.Get("character_tags"),
                    ["EPISODES_BASE"] = ctx.Get("season_tags"),
                    ["EPISODES_STRUCT"] = string.Join("\n\n", episodeStructures)
                }));

        var validation = await ExecuteStepAsync(
            validatorStep,
            context,
            runId,
            totalSteps,
            totalSteps,
            agentsByRole,
            reportStep,
            ct).ConfigureAwait(false);

        if (!validation.Success)
        {
            return SeriesWorkflowResult.Fail(validation.Error ?? "Validazione serie fallita");
        }

        var validatorText = context.Get("validator_tags");
        if (_validationRules.RequiresValidationOkTag() &&
            !validatorText.Contains("[VALIDATION_OK]", StringComparison.OrdinalIgnoreCase))
        {
            return SeriesWorkflowResult.Fail($"Validazione serie non OK: {validatorText}");
        }

        return SeriesWorkflowResult.Ok(
            context.Get("bible_tags"),
            context.Get("character_tags"),
            context.Get("season_tags"),
            episodeStructures,
            validatorText);
    }

    private async Task<SeriesStepExecutionResult> ExecuteStepAsync(
        SeriesWorkflowStep step,
        SeriesWorkflowContext context,
        string runId,
        int current,
        int max,
        IReadOnlyDictionary<string, Agent> agentsByRole,
        Action<int, int, string, Agent>? reportStep,
        CancellationToken ct)
    {
        if (!agentsByRole.TryGetValue(step.RoleCode, out var agent))
        {
            return SeriesStepExecutionResult.Fail($"Nessun agente attivo con ruolo {step.RoleCode}");
        }

        reportStep?.Invoke(current, max, step.PhaseLabel, agent);

        var prompt = step.InputFactory(context);
        var response = await _agentCaller.CallRoleWithRetriesAsync(
            new SeriesAgentCallRequest(
                agent,
                step.RoleCode,
                prompt,
                step.RequiredTags,
                step.Options,
                runId,
                step.ValidationFunc),
            ct).ConfigureAwait(false);

        if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
        {
            return SeriesStepExecutionResult.Fail(response.Error ?? $"Step {step.RoleCode} fallito");
        }

        context.Set(step.OutputKey, response.Text);
        return SeriesStepExecutionResult.Ok();
    }

    private static string BuildPrompt(
        IReadOnlyDictionary<string, Agent> agentsByRole,
        string roleCode,
        IReadOnlyDictionary<string, string> placeholders)
    {
        if (!agentsByRole.TryGetValue(roleCode, out var agent))
        {
            return string.Empty;
        }

        return SeriesPromptTemplates.ComposePrompt(agent, roleCode, placeholders);
    }
}

internal sealed record SeriesWorkflowStep(
    string RoleCode,
    string OutputKey,
    string PhaseLabel,
    IReadOnlyCollection<string> RequiredTags,
    Func<string, string?>? ValidationFunc,
    SeriesGenerationOptions.SeriesRoleOptions Options,
    Func<SeriesWorkflowContext, string> InputFactory);

internal sealed class SeriesWorkflowContext
{
    private readonly Dictionary<string, string> _outputs = new(StringComparer.OrdinalIgnoreCase);

    public string UserPrompt { get; }

    public SeriesWorkflowContext(string userPrompt)
    {
        UserPrompt = userPrompt ?? string.Empty;
    }

    public void Set(string key, string value)
    {
        _outputs[key] = value ?? string.Empty;
    }

    public string Get(string key)
    {
        return _outputs.TryGetValue(key, out var value) ? value : string.Empty;
    }
}

internal sealed record SeriesWorkflowResult(
    bool Success,
    string? Error,
    string BibleTags,
    string CharacterTags,
    string SeasonTags,
    IReadOnlyList<string> EpisodeStructures,
    string ValidationText)
{
    public static SeriesWorkflowResult Fail(string error)
        => new(false, error, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), string.Empty);

    public static SeriesWorkflowResult Ok(
        string bibleTags,
        string characterTags,
        string seasonTags,
        IReadOnlyList<string> episodeStructures,
        string validationText)
        => new(true, null, bibleTags, characterTags, seasonTags, episodeStructures, validationText);
}

internal sealed record SeriesStepExecutionResult(bool Success, string? Error)
{
    public static SeriesStepExecutionResult Ok() => new(true, null);
    public static SeriesStepExecutionResult Fail(string error) => new(false, error);
}
