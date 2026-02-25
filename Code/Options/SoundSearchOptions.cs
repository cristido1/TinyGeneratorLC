namespace TinyGenerator.Services;

public sealed class SoundSearchOptions
{
    public bool Enabled { get; set; } = true;
    public string DownloadFolder { get; set; } = @"C:\Users\User\Documents\ai\sounds_library";
    public string? TempFolder { get; set; }
    public string UserAgent { get; set; } = "TinyGenerator SoundSearch/1.0";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxCandidatesPerSource { get; set; } = 10;
    public int MaxInsertPerSource { get; set; } = 10;
    public int MaxInsertPerSearch { get; set; } = 100;
    public double MinDurationSeconds { get; set; } = 0.2d;
    public double MaxDurationSecondsFx { get; set; } = 12d;
    public double MaxDurationSecondsAmb { get; set; } = 300d;
    public string[] PreferredSampleFormats { get; set; } = new[] { "wav", "mp3" };
    public string[] AllowedLicenses { get; set; } = new[] { "cc0", "cc-by", "pixabay", "mixkit" };
    public bool SkipIfAlreadyInSounds { get; set; } = true;
    public SoundSearchExecutionOptions Execution { get; set; } = new();
    public SoundSearchQueryOptions Query { get; set; } = new();
    public SoundSearchScoringOptions Scoring { get; set; } = new();
    public SoundSearchProviderTagNormalizationOptions ProviderTagNormalization { get; set; } = new();
    public SoundSearchFreesoundOptions Freesound { get; set; } = new();
    public SoundSearchPixabayOptions Pixabay { get; set; } = new();
    public SoundSearchMixkitOptions Mixkit { get; set; } = new();
    public SoundSearchOrangeOptions OrangeFreeSounds { get; set; } = new();
    public SoundSearchSoundBibleOptions SoundBible { get; set; } = new();
    public SoundSearchOpenGameArtOptions OpenGameArt { get; set; } = new();
}

public sealed class SoundSearchQueryOptions
{
    public int MaxQueryTerms { get; set; } = 8; // legacy
    public bool AddEnglishSynonyms { get; set; } = false; // legacy (tag gia' in inglese)
    public int MaxWordsPerQuery { get; set; } = 3;
    public bool IncludeFallbackQuery { get; set; } = true;
}

public sealed class SoundSearchExecutionOptions
{
    public bool StopOnFirstValidQuery { get; set; } = false;
    public bool StopOnFirstValidSource { get; set; } = false;
}

public sealed class SoundSearchScoringOptions
{
    public double MatchPrimaryWeight { get; set; } = 2.0d;
    public double MatchSecondaryWeight { get; set; } = 1.0d;
    public double MatchContextWeight { get; set; } = 1.0d;
    public double MatchMaterialOrEnergyWeight { get; set; } = 1.0d;
    public double DurationCompatibleWeight { get; set; } = 1.0d;
    public double FormatWavOrFlacWeight { get; set; } = 1.0d;
    public double SourceBonusFreesound { get; set; } = 1.0d;
    public double SourceBonusPixabay { get; set; } = 1.0d;
    public double SourceBonusMixkit { get; set; } = 1.0d;
    public double SourceBonusOrange { get; set; } = 1.0d;
    public double SourceBonusSoundBible { get; set; } = 1.0d;
    public double SourceBonusOpenGameArt { get; set; } = 1.0d;
}

public sealed class SoundSearchProviderTagNormalizationOptions
{
    public bool Enabled { get; set; } = true;
    public int DescriptionFallbackMaxWords { get; set; } = 20;
    public int MinTokenLength { get; set; } = 2;
    public string[] StopWords { get; set; } = new[]
    {
        "sound","sounds","effect","effects","audio","free","download","loop","loops",
        "hq","lq","mp3","wav","ogg","file","files","clip","clips","noise","noises"
    };
}

public sealed class SoundSearchFreesoundOptions
{
    public bool Enabled { get; set; } = true;
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://freesound.org/apiv2";
    public int PageSize { get; set; } = 15;
}

public sealed class SoundSearchPixabayOptions
{
    public bool Enabled { get; set; } = true;
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://pixabay.com";
    public bool UseOfficialApiIfPossible { get; set; } = true;
    public string OfficialApiUrl { get; set; } = "https://pixabay.com/api/";
    public string OfficialSoundsApiUrl { get; set; } = "https://pixabay.com/api/sounds/";
}

public sealed class SoundSearchMixkitOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://mixkit.co";
    public string CategoryPath { get; set; } = "/free-sound-effects/";
    public string SearchPath { get; set; } = "/free-sound-effects/search/";
}

public sealed class SoundSearchOrangeOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://orangefreesounds.com";
}

public sealed class SoundSearchSoundBibleOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://soundbible.com";
}

public sealed class SoundSearchOpenGameArtOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://opengameart.org";
    public int FieldArtTypeTid { get; set; } = 12;
}
