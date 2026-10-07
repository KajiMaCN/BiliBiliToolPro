using System.Reflection;
using Microsoft.Extensions.Configuration;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Config.SQLite;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Web.Services.Pages.BiliAccount;
using Xunit;

namespace Ray.BiliBiliTool.Web.ComponentTests;

public sealed class PasswordLoginWorkflowTests
{
    [Theory]
    [InlineData("1001", 2)]
    [InlineData("1003", 3)]
    public async Task LoginUpsertsMatchingUserAndKeepsOtherAccounts(string userId, int count)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var config = (ConfigurationRoot)
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["BiliBiliCookies:0"] =
                                "DedeUserID=1001; SESSDATA=original; bili_jct=original",
                            ["BiliBiliCookies:1"] =
                                "DedeUserID=1002; SESSDATA=other; bili_jct=other",
                        }
                    )
                    .AddSqlite(
                        $"Data Source={Path.Combine(directory, "test.db")}",
                        tableName: Ray.BiliBiliTool.Config.Constants.SqliteTableName
                    )
                    .Build();
            var login = DispatchProxy.Create<ILoginDomainService, CookieEnrichmentProxy>();
            var workflow = new BiliAccountPageWorkflow(config, login);
            await workflow.PasswordLoginCompleteAsync(
                new(
                    new()
                    {
                        ["DedeUserID"] = userId,
                        ["SESSDATA"] = "replacement",
                        ["bili_jct"] = "replacement",
                    }
                )
            );
            var accounts = await workflow.GetAllAccountsAsync();
            Assert.Equal(count, accounts.Count);
            Assert.Single(
                accounts,
                item => item.UserId == userId && item.CookieStr.Contains("SESSDATA=replacement")
            );
            Assert.Single(
                accounts,
                item => item.UserId == "1002" && item.CookieStr.Contains("SESSDATA=other")
            );
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                workflow.PasswordLoginCompleteAsync(
                    new(
                        new()
                        {
                            ["DedeUserID"] = "1004",
                            ["SESSDATA"] = "cancelled",
                            ["bili_jct"] = "cancelled",
                        }
                    ),
                    cancelled.Token
                )
            );
            Assert.Equal(count, (await workflow.GetAllAccountsAsync()).Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}

public class CookieEnrichmentProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != "SetCookieAsync")
            throw new NotSupportedException();
        ((CancellationToken)args![1]!).ThrowIfCancellationRequested();
        return Task.FromResult((BiliCookie)args[0]!);
    }
}
