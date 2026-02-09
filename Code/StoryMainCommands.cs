using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoryMainCommands
{
    private readonly StoriesService _service;

    public StoryMainCommands(StoriesService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    internal StoriesService.IStoryCommand? CreateCommandForStatus(StoryStatus status)
    {
        if (status == null || string.IsNullOrWhiteSpace(status.OperationType))
            return null;

        var opType = status.OperationType.ToLowerInvariant();
        switch (opType)
        {
            case "agent_call":
                var agentType = status.AgentType?.ToLowerInvariant();
                return agentType switch
                {
                    "tts_json" or "tts" => new StoriesService.GenerateTtsSchemaCommand(_service),
                    "tts_voice" or "voice" => new StoriesService.AssignVoicesCommand(_service),
                    "evaluator" or "story_evaluator" or "writer_evaluator" => new EvaluateStoryCommand(_service),
                    "revisor" => new ReviseStoryCommand(_service),
                    "formatter" => new TagStoryCommand(_service),
                    "ambient_expert" => new AddAmbientTagsToStoryStateCommand(_service),
                    "fx_expert" => new AddFxTagsToStoryStateCommand(_service),
                    "music_expert" => new AddMusicTagsToStoryStateCommand(_service),
                    _ => new StoriesService.NotImplementedCommand($"agent_call:{agentType ?? "unknown"}")
                };
            case "function_call":
                var functionName = status.FunctionName?.ToLowerInvariant();
                return functionName switch
                {
                    "evaluate_story" => new EvaluateStoryCommand(_service),
                    "tag_story" => new TagStoryCommand(_service),
                    "add_ambient_tags_to_story" => new AddAmbientTagsToStoryStateCommand(_service),
                    "add_fx_tags_to_story" => new AddFxTagsToStoryStateCommand(_service),
                    "add_music_tags_to_story" => new AddMusicTagsToStoryStateCommand(_service),
                    "add_voice_tags_to_story" => new TagStoryCommand(_service),
                    "generate_audio_master" or "audio_master" or "mix_audio" or "mix_final" or "final_mix" => new MixFinalAudioCommand(_service),
                    "assign_voices" or "voice_assignment" => new StoriesService.AssignVoicesCommand(_service),
                    "prepare_tts_schema" => new PrepareTtsSchemaCommand(_service),
                    "generate_tts_audio" or "tts_audio" or "build_tts_audio" or "generate_voice_tts" or "generate_voices" => new GenerateTtsAudioCommand(_service),
                    "generate_ambience_audio" or "ambience_audio" or "generate_ambient" or "ambient_sounds" => new GenerateAmbienceAudioCommand(_service),
                    "generate_fx_audio" or "fx_audio" or "generate_fx" or "sound_effects" => new GenerateFxAudioCommand(_service),
                    "generate_music" or "music_audio" or "generate_music_audio" => new GenerateMusicCommand(_service),
                    _ => new StoriesService.FunctionCallCommand(_service, status)
                };
            case "service_call":
                var serviceName = status.FunctionName?.ToLowerInvariant();
                return serviceName switch
                {
                    "prepare_tts_schema" => new PrepareTtsSchemaCommand(_service),
                    "generate_tts_audio" or "tts_audio" or "build_tts_audio" or "generate_voice_tts" or "generate_voices" => new GenerateTtsAudioCommand(_service),
                    "generate_ambience_audio" or "ambience_audio" or "generate_ambient" or "ambient_sounds" => new GenerateAmbienceAudioCommand(_service),
                    "generate_fx_audio" or "fx_audio" or "generate_fx" or "sound_effects" => new GenerateFxAudioCommand(_service),
                    "generate_music" or "music_audio" or "generate_music_audio" => new GenerateMusicCommand(_service),
                    _ => new StoriesService.FunctionCallCommand(_service, status)
                };
            default:
                return null;
        }
    }

    internal string? GetOperationNameForStatus(StoryStatus status)
    {
        if (status == null || string.IsNullOrWhiteSpace(status.OperationType))
            return null;

        var opType = status.OperationType.ToLowerInvariant();
        switch (opType)
        {
            case "agent_call":
                var agentType = status.AgentType?.ToLowerInvariant();
                return agentType switch
                {
                    "tts_json" or "tts" => "generate_tts_schema",
                    "tts_voice" or "voice" => "assign_voices",
                    "evaluator" or "story_evaluator" or "writer_evaluator" => "evaluate_story",
                    "revisor" => "revise_story",
                    "formatter" => "add_voice_tags_to_story",
                    _ => $"agent_call:{agentType ?? "unknown"}"
                };
            case "function_call":
                return status.FunctionName ?? "function_call";
            default:
                return status.OperationType;
        }
    }

}
