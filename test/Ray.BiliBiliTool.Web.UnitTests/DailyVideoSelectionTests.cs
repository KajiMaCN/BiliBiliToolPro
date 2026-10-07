using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Coin;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Daily;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Relation;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.UpInfo;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Video;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Domain.Exceptions;
using Ray.BiliBiliTool.DomainService;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure.Cookie;
using Xunit;

namespace Ray.BiliBiliTool.Web.UnitTests;

public class DailyVideoSelectionTests
{
    private static readonly BiliCookie Cookie = CookieStrFactory<BiliCookie>.CreateNew(
        "DedeUserID=1001;bili_jct=synthetic;SESSDATA=synthetic"
    );

    [Fact]
    public async Task RejectedConfiguredUp_FallsBackToFollowingUp()
    {
        var environment = new Environment("11");
        environment.Call = (method, args) =>
            method.Name switch
            {
                "SearchVideosByUpId" => Task.FromResult(
                    ((SearchVideosByUpIdDto)args[0]!).mid == 11
                        ? new BiliApiResponse<SearchUpVideosResponse>
                        {
                            Code = -403,
                            Message = "synthetic rejection",
                        }
                        : Videos()
                ),
                "GetFollowings" => Task.FromResult(Followings(22)),
                _ => throw new InvalidOperationException(method.Name),
            };

        var selected = await environment.Video.GetRandomVideoForWatchAndShare(Cookie);

        Assert.Equal("2", selected.Aid);
        Assert.DoesNotContain("GetRegionRankingVideosV2", environment.Calls);
        Assert.Equal(3, environment.Calls.Count(call => call == "SearchVideosByUpId"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreferredSources_UseRankingWithoutPerformingActions(
        bool transportFailure
    )
    {
        var environment = new Environment("11");
        environment.Call = (method, _) =>
            method.Name switch
            {
                "SearchVideosByUpId" => transportFailure
                    ? throw new HttpRequestException("synthetic lookup failure")
                    : Task.FromResult(
                        new BiliApiResponse<SearchUpVideosResponse>
                        {
                            Code = -403,
                            Message = "synthetic rejection",
                        }
                    ),
                "GetFollowings" => throw new HttpRequestException("synthetic followings failure"),
                "GetRegionRankingVideosV2" => Task.FromResult(Ranking()),
                _ => throw new InvalidOperationException(method.Name),
            };

        var selected = await environment.Video.GetRandomVideoForWatchAndShare(Cookie);

        Assert.Equal("3", selected.Aid);
        Assert.Equal(
            new[] { "SearchVideosByUpId", "GetFollowings", "GetRegionRankingVideosV2" },
            environment.Calls
        );
    }

    [Fact]
    public async Task SuccessfulConfiguredUp_PreservesPriorityAndCookie()
    {
        var environment = new Environment("11");
        environment.Call = (method, args) =>
        {
            Assert.Equal("SearchVideosByUpId", method.Name);
            Assert.Equal(Cookie.ToString(), args[1]);
            return Task.FromResult(Videos());
        };

        var selected = await environment.Video.GetRandomVideoForWatchAndShare(Cookie);

        Assert.Equal("2", selected.Aid);
        Assert.Equal(2, environment.Calls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidFollowingCandidates_UseRanking(long upId)
    {
        var environment = new Environment();
        environment.Call = (method, _) =>
            method.Name switch
            {
                "GetFollowings" => Task.FromResult(Followings(upId)),
                "GetRegionRankingVideosV2" => Task.FromResult(Ranking()),
                _ => throw new InvalidOperationException(method.Name),
            };
        Assert.Equal("3", (await environment.Video.GetRandomVideoForWatchAndShare(Cookie)).Aid);
    }

    [Fact]
    public async Task EmptyFollowingListWithPositiveTotal_UsesRanking()
    {
        var environment = new Environment();
        environment.Call = (method, _) =>
            method.Name switch
            {
                "GetFollowings" => Task.FromResult(
                    new BiliApiResponse<GetFollowingsResponse>
                    {
                        Code = 0,
                        Data = new() { Total = 1 },
                    }
                ),
                "GetRegionRankingVideosV2" => Task.FromResult(Ranking()),
                _ => throw new InvalidOperationException(method.Name),
            };
        Assert.Equal("3", (await environment.Video.GetRandomVideoForWatchAndShare(Cookie)).Aid);
    }

    [Fact]
    public async Task RankingRejection_RemainsFailureWithBusinessCode()
    {
        var environment = new Environment();
        environment.Call = (method, _) =>
            method.Name switch
            {
                "GetFollowings" => throw new HttpRequestException("synthetic lookup failure"),
                "GetRegionRankingVideosV2" => Task.FromResult(
                    new BiliApiResponse<Ranking>
                    {
                        Code = -403,
                        Message = "synthetic ranking rejection",
                    }
                ),
                _ => throw new InvalidOperationException(method.Name),
            };
        var failure = await Assert.ThrowsAsync<BiliBusinessException>(() =>
            environment.Video.GetRandomVideoForWatchAndShare(Cookie)
        );
        Assert.Contains("-403", failure.Message);
        Assert.Contains("synthetic ranking rejection", failure.Message);
    }

    [Fact]
    public async Task Cancellation_StopsSelectionWithoutFallback()
    {
        var environment = new Environment("11");
        environment.Call = (_, _) => throw new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            environment.Video.GetRandomVideoForWatchAndShare(Cookie)
        );
        Assert.Equal(new[] { "SearchVideosByUpId" }, environment.Calls);
    }

    [Fact]
    public async Task DisabledWatchAndShare_DoNotQueryVideoSources()
    {
        var environment = new Environment();
        environment.Call = (method, _) => throw new InvalidOperationException(method.Name);
        await environment.Video.WatchAndShareVideo(new DailyTaskInfo(), Cookie);
        Assert.Empty(environment.Calls);
    }

    [Fact]
    public async Task EmptyRanking_ReportsMissingVideos()
    {
        var environment = new Environment();
        environment.Call = (_, _) =>
            Task.FromResult(new BiliApiResponse<Ranking> { Code = 0, Data = new() });
        var failure = await Assert.ThrowsAsync<BiliBusinessException>(() =>
            environment.Video.GetRandomVideoOfRanking()
        );
        Assert.Contains("排行榜", failure.Message);
    }

    [Fact]
    public async Task MissingVideoPagination_ReportsBusinessFailure()
    {
        var environment = new Environment();
        environment.Call = (_, _) =>
            Task.FromResult(new BiliApiResponse<SearchUpVideosResponse> { Code = 0 });
        var failure = await Assert.ThrowsAsync<BiliBusinessException>(() =>
            environment.Video.GetVideoCountOfUp(11, Cookie)
        );
        Assert.Contains("分页", failure.Message);
    }

    [Fact]
    public async Task CoinSelection_FailedFollowingSources_StillChecksRankingEligibility()
    {
        var environment = new Environment();
        environment.Call = (method, args) =>
            method.Name switch
            {
                "GetFollowingsByTag" or "GetFollowings" => throw new HttpRequestException(
                    "synthetic lookup failure"
                ),
                "GetRegionRankingVideosV2" => Task.FromResult(Ranking()),
                "GetDonatedCoinsForVideo" => Task.FromResult(
                    new BiliApiResponse<DonatedCoinsForVideo>
                    {
                        Code = 0,
                        Data = new() { Multiply = 0 },
                    }
                ),
                "GetVideoDetail" => AuthenticatedDetail(args),
                _ => throw new InvalidOperationException(method.Name),
            };
        var donate = new DonateCoinDomainService(
            NullLogger<DonateCoinDomainService>.Instance,
            new Monitor<DailyTaskOptions>(new()),
            null!,
            null!,
            environment.Video,
            environment.Api
        );

        var selected = await donate.TryGetCanDonatedVideo(Cookie);

        Assert.Equal(3, selected!.Aid);
        Assert.Equal(
            new[]
            {
                "GetFollowingsByTag",
                "GetFollowings",
                "GetRegionRankingVideosV2",
                "GetDonatedCoinsForVideo",
                "GetVideoDetail",
            },
            environment.Calls
        );
    }

    [Fact]
    public async Task CoinSelection_CancellationInConfiguredUp_StopsOtherSources()
    {
        var environment = new Environment("11");
        environment.Call = (_, _) => throw new OperationCanceledException();
        var donate = new DonateCoinDomainService(
            NullLogger<DonateCoinDomainService>.Instance,
            new Monitor<DailyTaskOptions>(new() { SupportUpIds = "11" }),
            null!,
            null!,
            environment.Video,
            environment.Api
        );
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            donate.TryGetCanDonatedVideo(Cookie)
        );
        Assert.Equal(new[] { "SearchVideosByUpId" }, environment.Calls);
    }

    private static Task<BiliApiResponse<VideoDetail>> AuthenticatedDetail(object?[] args)
    {
        Assert.Equal(Cookie.ToString(), args[1]);
        return Task.FromResult(
            new BiliApiResponse<VideoDetail>
            {
                Code = 0,
                Data = new()
                {
                    Aid = 3,
                    Bvid = "synthetic-ranking-video",
                    Title = "示例排行视频",
                    Copyright = 1,
                },
            }
        );
    }

    [Theory]
    [InlineData(-403)]
    [InlineData(0)]
    public async Task UnavailableVideoDetail_ReportsBusinessFailure(int code)
    {
        var environment = new Environment();
        environment.Call = (_, args) =>
        {
            Assert.Equal(Cookie.ToString(), args[1]);
            return Task.FromResult(
                new BiliApiResponse<VideoDetail> { Code = code, Message = "synthetic rejection" }
            );
        };
        var failure = await Assert.ThrowsAsync<BiliBusinessException>(() =>
            environment.Video.GetVideoDetail("3", Cookie)
        );
        Assert.Contains(code == 0 ? "未返回视频信息" : "-403", failure.Message);
    }

    [Fact]
    public async Task AnonymousVideoDetail_RemainsAvailableWithoutCookie()
    {
        var environment = new Environment();
        environment.Call = (_, args) =>
        {
            Assert.Null(args[1]);
            return Task.FromResult(
                new BiliApiResponse<VideoDetail>
                {
                    Code = 0,
                    Data = new()
                    {
                        Aid = 3,
                        Bvid = "synthetic",
                        Title = "示例",
                    },
                }
            );
        };
        Assert.Equal(3, (await environment.Video.GetVideoDetail("3")).Aid);
    }

    [Fact]
    public async Task CoinSelection_FallbackStillRejectsAlreadyFullyDonatedVideo()
    {
        var environment = new Environment();
        environment.Call = (method, _) =>
            method.Name switch
            {
                "GetFollowingsByTag" or "GetFollowings" => throw new HttpRequestException(
                    "synthetic lookup failure"
                ),
                "GetRegionRankingVideosV2" => Task.FromResult(Ranking()),
                "GetDonatedCoinsForVideo" => Task.FromResult(
                    new BiliApiResponse<DonatedCoinsForVideo>
                    {
                        Code = 0,
                        Data = new() { Multiply = 2 },
                    }
                ),
                _ => throw new InvalidOperationException(method.Name),
            };
        var donate = new DonateCoinDomainService(
            NullLogger<DonateCoinDomainService>.Instance,
            new Monitor<DailyTaskOptions>(new()),
            null!,
            null!,
            environment.Video,
            environment.Api
        );
        Assert.Null(await donate.TryGetCanDonatedVideo(Cookie));
        Assert.Equal(1, environment.Calls.Count(call => call == "GetDonatedCoinsForVideo"));
        Assert.Equal(5, environment.Calls.Count(call => call == "GetRegionRankingVideosV2"));
        Assert.DoesNotContain("AddCoinForVideo", environment.Calls);
    }

    [Fact]
    public async Task UnexpectedProgrammingFailure_IsNotHiddenByFallback()
    {
        var environment = new Environment("11");
        environment.Call = (_, _) =>
            throw new InvalidOperationException("synthetic programming failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            environment.Video.GetRandomVideoForWatchAndShare(Cookie)
        );
        Assert.Equal(new[] { "SearchVideosByUpId" }, environment.Calls);
    }

    private static BiliApiResponse<SearchUpVideosResponse> Videos() =>
        new()
        {
            Code = 0,
            Data = new()
            {
                Page = new() { Count = 1 },
                List = new()
                {
                    Vlist =
                    [
                        new()
                        {
                            Aid = 2,
                            Bvid = "synthetic-up-video",
                            Title = "示例视频",
                            Length = "00:15",
                        },
                    ],
                },
            },
        };

    private static BiliApiResponse<GetFollowingsResponse> Followings(long id) =>
        new()
        {
            Code = 0,
            Data = new() { Total = 1, List = [new() { Mid = id, Uname = "示例主播" }] },
        };

    private static BiliApiResponse<Ranking> Ranking() =>
        new()
        {
            Code = 0,
            Data = new()
            {
                List =
                [
                    new()
                    {
                        Aid = 3,
                        Bvid = "synthetic-ranking-video",
                        Title = "示例排行视频",
                        Cid = 30,
                        Duration = 60,
                    },
                ],
            },
        };

    private sealed class Environment
    {
        public List<string> Calls { get; } = [];
        public Func<MethodInfo, object?[], object?> Call { get; set; } =
            (_, _) => throw new NotSupportedException();
        public IApiApi Api { get; }
        public VideoDomainService Video { get; }

        public Environment(string? support = null)
        {
            Api = DispatchProxy.Create<IApiApi, Proxy>();
            ((Proxy)Api).Call = (method, args) =>
            {
                Calls.Add(method.Name);
                return Call(method, args);
            };
            Video = new(
                NullLogger<VideoDomainService>.Instance,
                new Monitor<DailyTaskOptions>(new() { SupportUpIds = support }),
                Api
            );
        }
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Call(targetMethod!, args ?? []);
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
