using Microsoft.Extensions.Configuration;

namespace TinyGenerator.Configuration;

public static class ExternalServerConfig
{
    public static string? GetValue(IConfiguration configuration, string externalPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration[$"ExternalServers:{externalPath}"];
    }

    public static string GetRequiredValue(IConfiguration configuration, string externalPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[$"ExternalServers:{externalPath}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Configurazione obbligatoria mancante: ExternalServers:{externalPath}");
        }

        return value;
    }

    public static IConfigurationSection GetRequiredSection(IConfiguration configuration, string externalSection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection($"ExternalServers:{externalSection}");
        if (!section.Exists())
        {
            throw new InvalidOperationException($"Sezione di configurazione obbligatoria mancante: ExternalServers:{externalSection}");
        }

        return section;
    }
}
