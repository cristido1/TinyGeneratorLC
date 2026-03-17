namespace TinyGenerator.Services
{
    public sealed class MonomodelModeOptions
    {
        public bool Enabled { get; set; } = false;
        public string ModelDescription { get; set; } = string.Empty;
        public bool DisableThinking { get; set; } = true;
    }
}
