using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Daily;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Video;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Domain;
using Ray.BiliBiliTool.DomainService;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace Ray.BiliBiliTool.Web.UnitTests;

public class ShareCompletionTests
{
    private static readonly BiliCookie Cookie = CookieStrFactory<BiliCookie>.CreateNew(
        "DedeUserID=1001;SESSDATA=synthetic;bili_jct=synthetic"
    );
    private static readonly VideoInfoDto Video = new()
    {
        Aid = "3",
        Bvid = "synthetic",
        Title = "示例",
        Cid = 30,
    };

    [Theory]
    [InlineData(0)]
    [InlineData(71000)]
    public async Task AcceptedOrRepeatedShare_CompletesOnlyAfterPlatformConfirmation(int code)
    {
        var environment = new Environment { ShareCode = code };
        environment.Reward = _ => Task.FromResult(Reward(true));
        await environment.Run();
        Assert.Equal(1, environment.ShareRequests);
        Assert.Equal(1, environment.Reads);
        Assert.Equal(TaskRecoveryProgressState.Completed, environment.Progress.Last().State);
        Assert.Contains(
            environment.Logger.Messages,
            text => text.Contains("B 站已确认") && text.Contains("经验+5")
        );
    }

    [Fact]
    public async Task DelayedConfirmation_UsesReadOnlyPollingWithoutRepostingShare()
    {
        var environment = new Environment();
        environment.Reward = read => Task.FromResult(Reward(read == 2));
        await environment.Run();
        Assert.Equal(1, environment.ShareRequests);
        Assert.Equal(2, environment.Reads);
        Assert.Equal(TaskRecoveryProgressState.Completed, environment.Progress.Last().State);
    }

    [Fact]
    public async Task AcceptedShareWithoutConfirmation_RemainsPendingWithoutExperienceClaim()
    {
        var environment = new Environment();
        environment.Reward = _ => Task.FromResult(Reward(false));
        await environment.Run();
        Assert.Equal(1, environment.ShareRequests);
        Assert.Equal(3, environment.Reads);
        Assert.Equal(TaskRecoveryProgressState.Pending, environment.Progress.Last().State);
        Assert.DoesNotContain(
            environment.Progress,
            progress => progress.State == TaskRecoveryProgressState.Completed
        );
        Assert.DoesNotContain(environment.Logger.Messages, text => text.Contains("经验+"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-101)]
    public async Task MissingOrRejectedReward_ReportsUnavailableProgress(int code)
    {
        var environment = new Environment();
        environment.Reward = _ =>
            Task.FromResult(new BiliApiResponse<DailyTaskInfo> { Code = code });
        await environment.Run();
        Assert.Equal(1, environment.ShareRequests);
        Assert.Equal(1, environment.Reads);
        Assert.Equal(TaskRecoveryProgressState.Pending, environment.Progress.Last().State);
        Assert.Contains("暂未获取", environment.Progress.Last().Detail);
    }

    [Fact]
    public async Task FailedRewardRequest_DoesNotRepostAcceptedShare()
    {
        var environment = new Environment();
        environment.Reward = _ => throw new HttpRequestException("synthetic request failure");
        await environment.Run();
        Assert.Equal(1, environment.ShareRequests);
        Assert.Equal(1, environment.Reads);
        Assert.Equal(TaskRecoveryProgressState.Pending, environment.Progress.Last().State);
    }

    [Fact]
    public async Task CancelledConfirmation_StopsPolling()
    {
        var environment = new Environment();
        environment.Reward = _ => throw new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(environment.Run);
        Assert.Equal(1, environment.Reads);
        Assert.Equal(1, environment.ShareRequests);
    }

    [Fact]
    public async Task UnexpectedConfirmationFailure_IsNotHidden()
    {
        var environment = new Environment();
        environment.Reward = _ =>
            throw new InvalidOperationException("synthetic programming failure");
        await Assert.ThrowsAsync<InvalidOperationException>(environment.Run);
        Assert.Equal(1, environment.Reads);
    }

    [Fact]
    public async Task MissingCid_LoadsAuthenticatedVideoDetailsBeforeCompletionReport()
    {
        var environment = new Environment { Cid = 0 };
        environment.Reward = _ => Task.FromResult(Reward(true));
        await environment.Run();
        Assert.Equal(1, environment.DetailReads);
        Assert.Equal(30, environment.LastRequest!.Cid);
        Assert.Equal(3, environment.LastRequest.Aid);
        Assert.Equal(Cookie.BiliJct, environment.LastRequest.Csrf);
        Assert.Equal(1, environment.ShareRequests);
    }

    [Theory]
    [InlineData(4, 30)]
    [InlineData(3, 0)]
    public async Task IncompleteOrMismatchedVideoDetails_DoNotSubmitCompletion(long aid, long cid)
    {
        var environment = new Environment { Cid = 0 };
        environment.Detail = () =>
            Task.FromResult(
                new BiliApiResponse<VideoDetail>
                {
                    Code = 0,
                    Data = new()
                    {
                        Aid = aid,
                        Cid = cid,
                        Bvid = "synthetic",
                        Title = "synthetic",
                    },
                }
            );
        await Assert.ThrowsAsync<Ray.BiliBiliTool.Domain.Exceptions.BiliBusinessException>(
            environment.Run
        );
        Assert.Equal(0, environment.ShareRequests);
    }

    [Fact]
    public async Task RejectedMetadata_DoesNotSubmitCompletion()
    {
        var environment = new Environment { Cid = 0 };
        environment.Detail = () =>
            Task.FromResult(new BiliApiResponse<VideoDetail> { Code = -403 });
        await Assert.ThrowsAsync<Ray.BiliBiliTool.Domain.Exceptions.BiliBusinessException>(
            environment.Run
        );
        Assert.Equal(0, environment.ShareRequests);
    }

    [Fact]
    public async Task CancelledMetadata_StopsBeforeSubmittingCompletion()
    {
        var environment = new Environment { Cid = 0 };
        environment.Detail = () => throw new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(environment.Run);
        Assert.Equal(0, environment.ShareRequests);
    }

    [Theory]
    [InlineData(-403)]
    [InlineData(-101)]
    public async Task RejectedCompletionReport_DoesNotClaimRewardOrRetrySubmission(int code)
    {
        var environment = new Environment { ShareCode = code };
        await Assert.ThrowsAsync<Ray.BiliBiliTool.Domain.Exceptions.BiliBusinessException>(
            environment.Run
        );
        Assert.Equal(1, environment.ShareRequests);
        Assert.Equal(0, environment.Reads);
        Assert.DoesNotContain(environment.Logger.Messages, text => text.Contains("经验+"));
    }

    private static BiliApiResponse<DailyTaskInfo> Reward(bool completed) =>
        new()
        {
            Code = 0,
            Data = new() { Share = completed },
        };

    private sealed class Environment
    {
        public int ShareCode;
        public int ShareRequests;
        public int Reads;
        public long Cid = 30;
        public int DetailReads;
        public ShareVideoCompletionRequest? LastRequest;
        public Func<Task<BiliApiResponse<VideoDetail>>> Detail = () =>
            Task.FromResult(
                new BiliApiResponse<VideoDetail>
                {
                    Code = 0,
                    Data = new()
                    {
                        Aid = 3,
                        Cid = 30,
                        Bvid = "synthetic",
                        Title = "synthetic",
                    },
                }
            );
        public Func<int, Task<BiliApiResponse<DailyTaskInfo>>> Reward = _ =>
            throw new NotSupportedException();
        public List<TaskRecoveryProgress> Progress { get; } = [];
        public Logger Logger { get; } = new();

        public async Task Run()
        {
            var api = DispatchProxy.Create<IApiApi, Proxy>();
            ((Proxy)api).Call = (method, args) =>
            {
                Assert.Equal(
                    Cookie.ToString(),
                    args[method.Name == "GetDailyTaskRewardInfoAsync" ? 0 : 1]
                );
                if (method.Name == "GetDailyTaskRewardInfoAsync")
                    return Reward(++Reads);
                if (method.Name == "GetVideoDetail")
                {
                    DetailReads++;
                    return Detail();
                }
                if (method.Name != "CompleteVideoShare")
                    throw new NotSupportedException(method.Name);
                ShareRequests++;
                LastRequest = (ShareVideoCompletionRequest)args[0]!;
                return Task.FromResult(new BiliApiResponse { Code = ShareCode });
            };
            var video = new VideoDomainService(Logger, new Monitor(), api, new ImmediateClock());
            using var scope = new TaskRecoveryProgressScope(Progress.Add);
            await video.ShareVideo(
                new()
                {
                    Aid = Video.Aid,
                    Bvid = Video.Bvid,
                    Title = Video.Title,
                    Cid = Cid,
                },
                Cookie
            );
        }
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Call(targetMethod!, args!);
    }

    private sealed class Monitor : IOptionsMonitor<DailyTaskOptions>
    {
        public DailyTaskOptions CurrentValue { get; } = new();

        public DailyTaskOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<DailyTaskOptions, string?> listener) => null;
    }

    private sealed class Logger : ILogger<VideoDomainService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));
    }

    private sealed class ImmediateClock : TimeProvider
    {
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new Timer();
        }

        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
