namespace TinyGenerator.Services.Commands;

public static class CommandRoleCodes
{
    public const string Summarizer = "summarizer";
    public const string AmbientExpert = "ambient_expert";
    public const string FxExpert = "fx_expert";
    public const string MusicExpert = "music_expert";
    public const string Formatter = "formatter";
    public const string CanonExtractor = "canon_extractor";
    public const string StateDeltaBuilder = "state_delta_builder";
    public const string ContinuityValidator = "continuity_validator";
    public const string StateCompressor = "state_compressor";
    public const string RecapBuilder = "recap_builder";
    public const string SerieBibleAgent = "serie_bible_agent";
    public const string SerieCharacterAgent = "serie_character_agent";
    public const string SerieSeasonAgent = "serie_season_agent";
    public const string SerieEpisodeAgent = "serie_episode_agent";
    public const string SerieValidatorAgent = "serie_validator_agent";
}

public static class CommandStatusCodes
{
    public const string TaggedVoice = "tagged_voice";
    public const string TaggedAmbient = "tagged_ambient";
    public const string TaggedFx = "tagged_fx";
    public const string Tagged = "tagged";
}

public static class CommandTriggerCodes
{
    public const string VoiceTagsCompleted = "voice_tags_completed";
    public const string AmbientTagsCompleted = "ambient_tags_completed";
    public const string FxTagsCompleted = "fx_tags_completed";
    public const string MusicTagsCompleted = "music_tags_completed";
}

public static class CommandScopePaths
{
    public const string AddAmbientTagsToStory = "story/add_ambient_tags_to_story";
    public const string AddVoiceTagsToStory = "story/add_voice_tags_to_story";
}

