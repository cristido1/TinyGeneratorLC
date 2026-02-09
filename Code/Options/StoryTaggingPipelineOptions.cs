using System;

namespace TinyGenerator.Services;

public sealed class StoryTaggingPipelineOptions
{
    public TaggedVoiceOptions TaggedVoice { get; set; } = new();
    public TaggedAmbientOptions TaggedAmbient { get; set; } = new();
    public TaggedFxOptions TaggedFx { get; set; } = new();
    public TaggedFinalOptions Tagged { get; set; } = new();

    public sealed class TaggedVoiceOptions
    {
        public bool AutolaunchNextCommand { get; set; } = true;
        public bool EnableDialogTagCheck { get; set; } = true;
        public bool EnableEmotionTagCheck { get; set; } = true;
        public int MinDistinctCharacters { get; set; } = 2;
    }

    public sealed class TaggedAmbientOptions
    {
        public bool AutolaunchNextCommand { get; set; } = true;
        public bool EnableAmbientTagCheck { get; set; } = true;
        public double MinAmbientTagDensity { get; set; } = 0.05;
    }

    public sealed class TaggedFxOptions
    {
        public bool AutolaunchNextCommand { get; set; } = true;
        public bool EnableFxTagCheck { get; set; } = true;
        public int MinFxTagCount { get; set; } = 3;
    }

    public sealed class TaggedFinalOptions
    {
        public bool AutolaunchNextCommand { get; set; } = true;
        public bool EnableMusicTagCheck { get; set; } = true;
        public int MinMusicTagCount { get; set; } = 1;
    }
}
