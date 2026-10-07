using Microsoft.Extensions.Configuration;
using Ray.Serilog.Sinks.Compatibility;

namespace Ray.BiliBiliTool.Infrastructure.Notifications;

public static class NotificationConfigurationExtensions
{
    public static IConfiguration WithCompatibleHttpNotifications(
        this IConfiguration configuration
    ) =>
        configuration.WithNotificationSinkAliases(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["GotifyBatched"] = "CompatibleGotifyBatched",
                ["OtherApiBatched"] = "HeaderOtherApiBatched",
            }
        );

    internal static IConfiguration WithNotificationSinkAliases(
        this IConfiguration configuration,
        IReadOnlyDictionary<string, string> aliases
    )
    {
        var overrides = new Dictionary<string, string?>();
        var usesPackage = false;
        foreach (var sink in configuration.GetSection("Serilog:WriteTo").GetChildren())
        {
            var name = sink["Name"];
            if (name is not null && aliases.TryGetValue(name, out var replacement))
            {
                overrides[$"{sink.Path}:Name"] = replacement;
                usesPackage = true;
            }
            else if (aliases.Values.Contains(name, StringComparer.OrdinalIgnoreCase))
                usesPackage = true;
        }
        if (!usesPackage)
            return configuration;

        var assembly = typeof(HttpNotificationBatchedSink).Assembly.GetName().Name;
        var entries = configuration.GetSection("Serilog:Using").GetChildren().ToList();
        if (!entries.Any(entry => string.Equals(entry.Value, assembly, StringComparison.Ordinal)))
        {
            var next =
                entries
                    .Select(entry => int.TryParse(entry.Key, out var index) ? index : -1)
                    .DefaultIfEmpty(-1)
                    .Max() + 1;
            overrides[$"Serilog:Using:{next}"] = assembly;
        }
        if (overrides.Count == 0)
            return configuration;
        return new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(overrides)
            .Build();
    }
}
