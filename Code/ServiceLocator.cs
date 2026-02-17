namespace TinyGenerator.Services
{
    // Pragmatic bridge for legacy/static callers that still need DI-resolved services.
    public static class ServiceLocator
    {
        public static DatabaseService? Database { get; set; }
        public static IServiceProvider? Services { get; set; }
    }
}
