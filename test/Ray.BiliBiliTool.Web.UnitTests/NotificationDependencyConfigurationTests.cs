using Microsoft.Extensions.Configuration;
using Ray.BiliBiliTool.Infrastructure.Notifications;
using Ray.Serilog.Sinks.Compatibility;

namespace Ray.BiliBiliTool.Web.UnitTests;

public class NotificationDependencyConfigurationTests
{
    [Theory]
    [InlineData("CompatibleGotifyBatched")]
    [InlineData("HeaderOtherApiBatched")]
    [InlineData("ChunkedTelegramBatched")]
    [InlineData("ChunkedWorkWeiXinBatched")]
    [InlineData("VerifiedWorkWeiXinAppBatched")]
    public void PreviouslyConfiguredAdapterLoadsPackageAndMappingIsIdempotent(string name)
    {
        var input = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Serilog:Using:0"] = "Ray.BiliBiliTool.Console",
                    ["Serilog:WriteTo:0:Name"] = name,
                    ["Serilog:WriteTo:0:Args:token"] = "synthetic-token",
                }
            )
            .Build();
        var effective = Map(input);
        var assembly = typeof(HttpNotificationBatchedSink).Assembly.GetName().Name;
        Assert.Equal(name, effective["Serilog:WriteTo:0:Name"]);
        Assert.Equal("synthetic-token", effective["Serilog:WriteTo:0:Args:token"]);
        Assert.Equal("Ray.BiliBiliTool.Console", effective["Serilog:Using:0"]);
        Assert.Single(
            effective.GetSection("Serilog:Using").GetChildren(),
            item => item.Value == assembly
        );
        Assert.Null(input["Serilog:Using:1"]);
        Assert.Same(effective, Map(effective));
    }

    private static IConfiguration Map(IConfiguration configuration) =>
        configuration
            .WithCompatibleHttpNotifications()
            .WithTelegramMessageChunking()
            .WithWorkWeiXinMessageChunking();
}
