using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Video;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Refit;
using Xunit;

namespace Ray.BiliBiliTool.Web.UnitTests;

public class VideoSearchResponseTests
{
    [Theory]
    [InlineData("{\"code\":-403,\"message\":\"synthetic rejection\"}")]
    [InlineData("{\"code\":-403,\"message\":\"synthetic rejection\",\"data\":null}")]
    [InlineData("{\"code\":-403,\"message\":\"synthetic rejection\",\"data\":{}}")]
    public async Task ErrorEnvelope_PreservesBusinessCodeWithoutPagination(string json)
    {
        using var client = new HttpClient(new Response(json))
        {
            BaseAddress = new Uri("https://api.bilibili.com"),
        };
        var api = RestService.For<IApiApi>(client);
        var result = await api.SearchVideosByUpId(
            new SearchVideosByUpIdDto { mid = 11 },
            "synthetic"
        );
        Assert.Equal(-403, result.Code);
        Assert.Equal("synthetic rejection", result.Message);
    }

    private sealed class Response(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }
            );
    }

    [Fact]
    public async Task VideoDetail_SendsExplicitAccountCookie()
    {
        using var handler = new DetailResponse();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bilibili.com"),
        };
        var api = RestService.For<IApiApi>(client);
        var result = await api.GetVideoDetail("3", "DedeUserID=1001;SESSDATA=synthetic");
        Assert.Equal(0, result.Code);
        Assert.Equal("DedeUserID=1001;SESSDATA=synthetic", handler.Cookie);
        Assert.Equal("https://www.bilibili.com/", handler.Referer);
        Assert.Equal("https://www.bilibili.com", handler.Origin);
    }

    private sealed class DetailResponse : HttpMessageHandler
    {
        public string? Cookie { get; private set; }
        public string? Referer { get; private set; }
        public string? Origin { get; private set; }
        public string? Body { get; private set; }
        public string? Path { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Cookie = string.Join("; ", request.Headers.GetValues("Cookie"));
            Path = request.RequestUri?.AbsolutePath;
            Referer = request.Headers.Referrer?.ToString();
            Origin = string.Join("; ", request.Headers.GetValues("Origin"));
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{\"code\":0,\"data\":{\"aid\":3,\"bvid\":\"synthetic\",\"title\":\"synthetic\"}}",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        }
    }

    [Fact]
    public async Task ShareRequest_KeepsLoginAndWebPageHeaders()
    {
        using var handler = new DetailResponse();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bilibili.com"),
        };
        var api = RestService.For<IApiApi>(client);
        await api.ShareVideo(
            new(3, "synthetic"),
            "DedeUserID=1001;SESSDATA=synthetic;buvid3=synthetic"
        );
        Assert.Equal("DedeUserID=1001;SESSDATA=synthetic;buvid3=synthetic", handler.Cookie);
        Assert.Equal("https://www.bilibili.com/", handler.Referer);
        Assert.Equal("https://www.bilibili.com", handler.Origin);
        var form = System.Web.HttpUtility.ParseQueryString(handler.Body!);
        Assert.Equal("pc_client_normal", form["source"]);
        Assert.Equal("2", form["eab_x"]);
        Assert.Equal("0", form["ramval"]);
        Assert.Equal("1", form["ga"]);
        Assert.Equal("synthetic", form["csrf"]);
    }

    [Fact]
    public async Task CompletionReport_UsesClientEndpointAndExactVideoAndAccountFields()
    {
        using var handler = new DetailResponse();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.bilibili.com"),
        };
        var api = RestService.For<IApiApi>(client);
        await api.CompleteVideoShare(
            new(3, 30, "synthetic", 123),
            "SESSDATA=synthetic;bili_jct=synthetic"
        );
        Assert.Equal("/x/share/finish", handler.Path);
        Assert.Equal("SESSDATA=synthetic;bili_jct=synthetic", handler.Cookie);
        Assert.Equal("https://www.bilibili.com/", handler.Referer);
        Assert.Equal("https://www.bilibili.com", handler.Origin);
        var form = System.Web.HttpUtility.ParseQueryString(handler.Body!);
        Assert.Equal("3", form["oid"]);
        Assert.Equal("30", form["sid"]);
        Assert.Equal("synthetic", form["csrf"]);
        Assert.Equal("123", form["ts"]);
        Assert.Equal("QQ", form["share_channel"]);
        Assert.Equal("true", form["success"]);
        Assert.Equal("main.ugc-video-detail.0.0.pv", form["share_id"]);
        Assert.Null(form["access_key"]);
        Assert.Null(form["source"]);
    }
}
