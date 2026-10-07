using Microsoft.Extensions.Configuration;

namespace Ray.BiliBiliTool.Infrastructure.Notifications;

public static class WorkWeiXinNotificationConfiguration
{
    public static IConfiguration WithWorkWeiXinMessageChunking(this IConfiguration configuration) =>
        configuration.WithNotificationSinkAliases(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WorkWeiXinBatched"] = "ChunkedWorkWeiXinBatched",
                ["WorkWeiXinAppBatched"] = "VerifiedWorkWeiXinAppBatched",
            }
        );
}
