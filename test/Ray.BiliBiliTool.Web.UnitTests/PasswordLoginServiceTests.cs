using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Ray.BiliBiliTool.Web.Services.Pages.BiliAccount;
using Xunit;

namespace Ray.BiliBiliTool.Web.UnitTests;

public sealed class PasswordLoginServiceTests
{
    private static readonly PasswordLoginProof Proof = new("challenge", "proof", "proof|jordan");

    [Fact]
    public async Task SuccessfulLoginEncryptsPasswordAndRequiresCompleteCookies()
    {
        using var rsa = RSA.Create(2048);
        var handler = new Handler(request =>
            request.Path switch
            {
                "/x/passport-login/captcha" => Captcha(),
                "/x/passport-login/web/key" => Key(rsa),
                "/x/passport-login/web/login" => LoggedIn(),
                _ => throw new InvalidOperationException("Unexpected request"),
            }
        );
        var service = Create(handler);
        await service.PrepareCaptchaAsync(false, default);
        var result = await service.LoginAsync(
            "  synthetic@example.invalid  ",
            "synthetic-password",
            Proof,
            default
        );
        Assert.Equal(PasswordLoginStatus.Success, result.Status);
        Assert.Equal("1001", result.Cookie!.UserId);
        var form = handler.Requests.Last().Form;
        Assert.Equal("synthetic@example.invalid", form["username"]);
        Assert.Equal(
            "salt-synthetic-01synthetic-password",
            Encoding.UTF8.GetString(
                rsa.Decrypt(Convert.FromBase64String(form["password"]), RSAEncryptionPadding.Pkcs1)
            )
        );
        Assert.Equal("proof|jordan", form["seccode"]);
        Assert.Equal("synthetic-token", form["token"]);
        Assert.DoesNotContain("synthetic-session", result.ToString());
        Assert.DoesNotContain("synthetic-password", JsonSerializer.Serialize(form));
    }

    [Theory]
    [InlineData(-629, "账号或密码错误")]
    [InlineData(-105, "验证码验证失败")]
    [InlineData(2406, "验证码验证失败")]
    [InlineData(-662, "已过期")]
    public async Task RejectedLoginDoesNotExposeResponseOrAcceptCookies(int code, string message)
    {
        using var rsa = RSA.Create(2048);
        var handler = new Handler(request =>
            request.Path switch
            {
                "/x/passport-login/captcha" => Captcha(),
                "/x/passport-login/web/key" => Key(rsa),
                _ => Json(
                    new
                    {
                        code,
                        message = "synthetic-password synthetic-session",
                        data = (object?)null,
                    }
                ),
            }
        );
        var service = Create(handler);
        await service.PrepareCaptchaAsync(false, default);
        var failure = await Assert.ThrowsAsync<PasswordLoginException>(() =>
            service.LoginAsync("synthetic", "synthetic-password", Proof, default)
        );
        Assert.Contains(message, failure.Message);
        Assert.DoesNotContain("synthetic", failure.Message);
        await Assert.ThrowsAsync<PasswordLoginException>(() =>
            service.LoginAsync("synthetic", "synthetic-password", Proof, default)
        );
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ExpiredAndMismatchedCaptchaDoNotSubmitCredentials()
    {
        var clock = new Clock();
        var handler = new Handler(_ => Captcha());
        var service = Create(handler, clock);
        await service.PrepareCaptchaAsync(false, default);
        await Assert.ThrowsAsync<PasswordLoginException>(() =>
            service.LoginAsync(
                "synthetic",
                "synthetic",
                Proof with
                {
                    Challenge = "different",
                },
                default
            )
        );
        clock.Now += TimeSpan.FromMinutes(3);
        await Assert.ThrowsAsync<PasswordLoginException>(() =>
            service.LoginAsync("synthetic", "synthetic", Proof, default)
        );
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhoneVerificationRequiresUserCodeAndExchangesItForCookies(bool securityScene)
    {
        using var rsa = RSA.Create(2048);
        var clock = new Clock();
        var scene = securityScene ? "secLogin" : "loginTelCheck";
        var handler = new Handler(request =>
            request.Path switch
            {
                "/x/passport-login/captcha" => Captcha(),
                "/x/passport-login/web/key" => Key(rsa),
                "/x/passport-login/web/login" => Json(
                    new
                    {
                        code = 0,
                        data = new
                        {
                            status = 1,
                            url = "https://passport.bilibili.com/riskVerify?tmp_token=synthetic-ticket&request_id=synthetic-request&source=risk&scene="
                                + scene,
                        },
                    }
                ),
                "/x/safecenter/user/info" => Json(
                    new
                    {
                        code = 0,
                        data = new { account_info = new { bind_tel = true, hide_tel = "***0000" } },
                    }
                ),
                "/x/safecenter/captcha/pre" => Captcha(true),
                "/x/safecenter/common/sms/send" => Json(
                    new { code = 0, data = new { captcha_key = "synthetic-code-key" } }
                ),
                "/x/safecenter/login/tel/verify" or "/x/safecenter/sec/verify" => Json(
                    new { code = 0, data = new { code = "synthetic-exchange" } }
                ),
                "/x/passport-login/web/exchange_cookie" => LoggedIn(),
                _ => throw new InvalidOperationException("Unexpected request"),
            }
        );
        var service = Create(handler, clock);
        await service.PrepareCaptchaAsync(false, default);
        var login = await service.LoginAsync("synthetic", "synthetic", Proof, default);
        Assert.Equal(PasswordLoginStatus.VerificationRequired, login.Status);
        Assert.Null(login.Cookie);
        await Assert.ThrowsAsync<PasswordLoginException>(() =>
            service.VerifyAsync("123456", default)
        );
        await service.PrepareCaptchaAsync(true, default);
        await service.SendVerificationCodeAsync(Proof, default);
        Assert.Equal(60, service.VerificationRetrySeconds);
        await Assert.ThrowsAsync<PasswordLoginException>(() =>
            service.SendVerificationCodeAsync(Proof, default)
        );
        clock.Now += TimeSpan.FromSeconds(60);
        Assert.Equal(0, service.VerificationRetrySeconds);
        var result = await service.VerifyAsync("123456", default);
        Assert.Equal(PasswordLoginStatus.Success, result.Status);
        Assert.Equal("1001", result.Cookie!.UserId);
        var verification = handler.Requests[^2].Form;
        Assert.Equal("synthetic-ticket", verification["tmp_code"]);
        Assert.Equal("123456", verification["code"]);
        if (securityScene)
            Assert.Equal("sms", verification["verify_type"]);
        else
            Assert.Equal("synthetic-request", verification["request_id"]);
        Assert.Equal("synthetic-exchange", handler.Requests.Last().Form["code"]);
        await Assert.ThrowsAsync<PasswordLoginException>(() =>
            service.VerifyAsync("123456", default)
        );
    }

    [Theory]
    [InlineData("https://example.invalid/riskVerify?tmp_token=synthetic")]
    [InlineData("http://passport.bilibili.com/riskVerify?tmp_token=synthetic")]
    [InlineData("https://passport.bilibili.com:444/riskVerify?tmp_token=synthetic")]
    [InlineData("https://synthetic@passport.bilibili.com/riskVerify?tmp_token=synthetic")]
    public async Task UntrustedVerificationUrlDoesNotTriggerFurtherRequests(string url)
    {
        using var rsa = RSA.Create(2048);
        var handler = new Handler(request =>
            request.Path switch
            {
                "/x/passport-login/captcha" => Captcha(),
                "/x/passport-login/web/key" => Key(rsa),
                _ => Json(new { code = 0, data = new { status = 1, url } }),
            }
        );
        var service = Create(handler);
        await service.PrepareCaptchaAsync(false, default);
        await Assert.ThrowsAsync<PasswordLoginException>(() =>
            service.LoginAsync("synthetic", "synthetic", Proof, default)
        );
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ImageCaptchaUsesImageProofAndDoesNotAcceptIncompleteCookie()
    {
        using var rsa = RSA.Create(2048);
        var handler = new Handler(request =>
            request.Path switch
            {
                "/x/passport-login/captcha" => Json(
                    new { code = 0, data = new { type = "img", token = "synthetic-image-token" } }
                ),
                "/x/recaptcha/img" => new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                    {
                        Headers = { ContentType = new("image/png") },
                    },
                },
                "/x/passport-login/web/key" => Key(rsa),
                _ => Json(new { code = 0, data = new { status = 0 } }),
            }
        );
        var service = Create(handler);
        var captcha = await service.PrepareCaptchaAsync(false, default);
        Assert.StartsWith("data:image/png;base64,", captcha.Image);
        await Assert.ThrowsAsync<PasswordLoginException>(() =>
            service.LoginAsync("synthetic", "synthetic", new(ImageCode: "12345"), default)
        );
        Assert.Equal("12345", handler.Requests.Last().Form["captcha"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"code\":\"0\",\"data\":{}}")]
    [InlineData("{\"code\":0,\"data\":null}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("not json")]
    public async Task MalformedResponseReturnsRecoverableError(string json)
    {
        var service = Create(
            new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) })
        );
        await Assert.ThrowsAsync<PasswordLoginException>(() =>
            service.PrepareCaptchaAsync(false, default)
        );
    }

    private static BiliPasswordLoginService Create(Handler handler, TimeProvider? clock = null) =>
        new(
            new HttpClient(handler) { BaseAddress = new("https://passport.bilibili.com") },
            clock ?? TimeProvider.System
        );

    private static HttpResponseMessage Key(RSA rsa) =>
        Json(
            new
            {
                code = 0,
                data = new
                {
                    hash = "salt-synthetic-01",
                    key = rsa.ExportSubjectPublicKeyInfoPem(),
                },
            }
        );

    private static HttpResponseMessage Captcha(bool risk = false) =>
        risk
            ? Json(
                new
                {
                    code = 0,
                    data = new
                    {
                        recaptcha_type = "geetest",
                        recaptcha_token = "synthetic-token",
                        gee_gt = "synthetic-gt",
                        gee_challenge = "challenge",
                    },
                }
            )
            : Json(
                new
                {
                    code = 0,
                    data = new
                    {
                        type = "geetest",
                        token = "synthetic-token",
                        geetest = new { gt = "synthetic-gt", challenge = "challenge" },
                    },
                }
            );

    private static HttpResponseMessage LoggedIn()
    {
        var response = Json(new { code = 0, data = new { status = 0 } });
        response.Headers.TryAddWithoutValidation(
            "Set-Cookie",
            new[]
            {
                "DedeUserID=1001; Path=/",
                "SESSDATA=synthetic-session; HttpOnly",
                "bili_jct=synthetic-csrf; Path=/",
            }
        );
        return response;
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private sealed record Request(string Path, Dictionary<string, string> Form);

    private sealed class Handler(Func<Request, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage message,
            CancellationToken cancellationToken
        )
        {
            var body = message.Content is null
                ? ""
                : await message.Content.ReadAsStringAsync(cancellationToken);
            var form = QueryHelpers
                .ParseQuery(body)
                .ToDictionary(item => item.Key, item => item.Value.ToString());
            var request = new Request(message.RequestUri!.AbsolutePath, form);
            Requests.Add(request);
            return respond(request);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-07T00:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
