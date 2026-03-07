namespace TinyGenerator.Services
{
    public sealed class AudioMixOptions
    {
        public double MusicVolume { get; set; } = 3;
        public double BackgroundSoundsVolume { get; set; } = 3;
        public double FxSourdsVolume { get; set; } = 6;
        public double VoiceVolume { get; set; } = 7;
        public int DefaultPhraseGapMs { get; set; } = 2000;
        public int CommaAttributionGapMs { get; set; } = 350;
        public double MaxSilenceGapSeconds { get; set; } = 0.8;
        public bool EnableFinalSilenceTrim { get; set; } = true;
        public double FinalSilenceTrimThresholdDb { get; set; } = -42;
        public double FinalSilenceTrimMaxGapSeconds { get; set; } = 0.8;
        public double FinalSilenceTrimKeepSeconds { get; set; } = 0.12;
        public bool EnableFinalDynamicNormalization { get; set; } = false;
        public double FinalLimiterLevel { get; set; } = 0.97;
    }
}
