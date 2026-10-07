using Microsoft.Extensions.Configuration;

namespace Ray.BiliBiliTool.Infrastructure.Notifications;

public static class TelegramNotificationConfiguration
{
    public static IConfiguration WithTelegramMessageChunking(this IConfiguration configuration) =>
        configuration.WithNotificationSinkAliases(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TelegramBatched"] = "ChunkedTelegramBatched",
            }
        );
}
