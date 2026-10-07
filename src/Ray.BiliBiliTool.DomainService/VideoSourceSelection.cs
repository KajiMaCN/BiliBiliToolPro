using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ray.BiliBiliTool.Domain.Exceptions;
using Refit;

namespace Ray.BiliBiliTool.DomainService;

internal static class VideoSourceSelection
{
    public static async Task<T?> TryAsync<T>(Func<Task<T?>> select, ILogger logger, string source)
        where T : class
    {
        try
        {
            return await select();
        }
        catch (Exception exception)
            when (exception
                    is BiliBusinessException
                        or HttpRequestException
                        or ApiException
                        or JsonException
            )
        {
            logger.LogWarning(
                "{source}视频查询失败，将尝试其他来源：{message}",
                source,
                exception.Message
            );
            return null;
        }
    }
}
