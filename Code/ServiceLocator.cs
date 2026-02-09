namespace TinyGenerator.Services
{
    // Simple service locator to allow legacy static callers to access DatabaseService.
    // This is a pragmatic bridge during the refactor to centralize sqlite access.
    public static class ServiceLocator
    {
        public static DatabaseService? Database { get; set; }
    }
}
