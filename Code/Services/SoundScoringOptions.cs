namespace TinyGenerator.Services;

public sealed class SoundScoringOptions
{
    public string ScoreVersion { get; set; } = "v1";
    public bool LogPerSoundScoreDetailsAndDuration { get; set; } = false;

    public double TargetRmsDbFs { get; set; } = -20.0;
    public double LoudnessToleranceDb { get; set; } = 18.0;

    public double DynamicTargetDb { get; set; } = 12.0;
    public double DynamicToleranceDb { get; set; } = 12.0;

    public double ClippingThresholdAbs { get; set; } = 0.999;
    public double ClippingFailRatio { get; set; } = 0.005; // 0.5%

    public double NoiseFloorGoodDbFs { get; set; } = -55.0;
    public double NoiseFloorBadDbFs { get; set; } = -30.0;

    public double WeightLoudness { get; set; } = 0.12;
    public double WeightDynamic { get; set; } = 0.10;
    public double WeightClipping { get; set; } = 0.18;
    public double WeightNoise { get; set; } = 0.10;
    public double WeightDuration { get; set; } = 0.10;
    public double WeightFormat { get; set; } = 0.10;
    public double WeightConsistency { get; set; } = 0.10;
    public double WeightTagMatch { get; set; } = 0.10;
    public double WeightHuman { get; set; } = 0.10;

    public int ScoreScaleMin { get; set; } = 0;
    public int ScoreScaleMax { get; set; } = 100;
}
