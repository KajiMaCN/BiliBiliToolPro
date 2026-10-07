using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.Web.Components.Pages.BiliAccount;
using Ray.BiliBiliTool.Web.Services.Pages.BiliAccount;
using Xunit;

namespace Ray.BiliBiliTool.Web.ComponentTests;

public sealed class PasswordLoginDialogTests : TestContext
{
    private readonly LoginStub _login = new();
    private readonly AccountsStub _accounts = new();
    private readonly Clock _clock = new();
    private readonly BunitJSModuleInterop _module;
    private bool _cancelCaptcha,
        _failCaptcha;

    public PasswordLoginDialogTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<TimeProvider>(_clock);
        _login.Clock = _clock;
        Services.AddSingleton<IBiliPasswordLoginService>(_login);
        Services.AddSingleton<IBiliAccountPageWorkflow>(_accounts);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _module = JSInterop.SetupModule("./password-login.js");
        _module
            .Setup<PasswordLoginProof>("verify", _ => !_cancelCaptcha && !_failCaptcha)
            .SetResult(new("challenge", "proof", "proof|jordan"));
        _module
            .Setup<PasswordLoginProof>("verify", _ => _cancelCaptcha)
            .SetResult(new(Cancelled: true));
        _module
            .Setup<PasswordLoginProof>("verify", _ => _failCaptcha)
            .SetException(new JSException("synthetic SDK error"));
    }

    [Fact]
    public async Task IdleDialogStartsWithFreshCaptchaBeforeSubmittingCredentials()
    {
        _login.EnforceCaptchaExpiry = true;
        var (provider, dialog) = await OpenAsync();
        _clock.Now += TimeSpan.FromMinutes(3);
        Fill(provider);
        _login.Response.SetResult(Success());
        await provider.Find(".password-login-submit").ClickAsync(new());
        Assert.Equal(1, _accounts.Saves);
        Assert.DoesNotContain("验证码已过期", provider.Markup);
        Assert.False((await dialog.Result)!.Canceled);
    }

    [Fact]
    public async Task ClosedCaptchaKeepsInputsAndNextAttemptGetsNewSession()
    {
        _cancelCaptcha = true;
        var (provider, dialog) = await OpenAsync();
        Fill(provider);
        await provider.Find(".password-login-submit").ClickAsync(new());
        Assert.Equal(0, _login.Logins);
        Assert.Equal(
            "synthetic-password",
            provider.Find("input[type=password]").GetAttribute("value")
        );
        var prepares = _login.Prepares;
        _cancelCaptcha = false;
        _login.Response.SetResult(Success());
        await provider.Find(".password-login-submit").ClickAsync(new());
        Assert.True(_login.Prepares > prepares);
        Assert.Equal(1, _accounts.Saves);
        Assert.False((await dialog.Result)!.Canceled);
    }

    [Fact]
    public async Task CaptchaErrorCanBeRetriedWithoutReenteringPassword()
    {
        _failCaptcha = true;
        var (provider, dialog) = await OpenAsync();
        Fill(provider);
        await provider.Find(".password-login-submit").ClickAsync(new());
        Assert.Equal(0, _login.Logins);
        Assert.Equal(0, _accounts.Saves);
        Assert.DoesNotContain("synthetic SDK error", provider.Markup);
        Assert.False(provider.Find(".password-login-submit").HasAttribute("disabled"));
        Assert.Equal(
            "synthetic-password",
            provider.Find("input[type=password]").GetAttribute("value")
        );
        var prepares = _login.Prepares;
        _failCaptcha = false;
        _login.Response.SetResult(Success());
        await provider.Find(".password-login-submit").ClickAsync(new());
        Assert.True(_login.Prepares > prepares);
        Assert.Equal(1, _accounts.Saves);
        Assert.False((await dialog.Result)!.Canceled);
    }

    [Fact]
    public async Task ImageCaptchaKeepsEnteredCodeUntilSubmitting()
    {
        _login.CaptchaType = "img";
        var (provider, dialog) = await OpenAsync();
        Fill(provider);
        Assert.True(provider.Find(".password-login-submit").HasAttribute("disabled"));
        provider.Find("input[autocomplete=off]").Input("synthetic-image-code");
        _login.Response.SetResult(Success());
        await provider.Find(".password-login-submit").ClickAsync(new());
        Assert.Equal(1, _login.Prepares);
        Assert.Equal("synthetic-image-code", _login.LastProof!.ImageCode);
        Assert.Equal(1, _accounts.Saves);
        Assert.False((await dialog.Result)!.Canceled);
    }

    [Fact]
    public async Task QrSuccessCancelsPendingCaptchaRefreshBeforePasswordIsSent()
    {
        var (provider, dialog) = await OpenAsync();
        var qrResult = provider.FindComponent<QrLoginPanel>().Instance.OnAuthenticated;
        _login.DelayedCaptcha = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Fill(provider);
        var click = provider.Find(".password-login-submit").ClickAsync(new());
        provider.WaitForAssertion(() => Assert.Equal(2, _login.Prepares));
        await provider.InvokeAsync(() => qrResult.InvokeAsync(Success().Cookie!));
        Assert.False((await dialog.Result)!.Canceled);
        _login.DelayedCaptcha.SetResult(new("geetest", "synthetic", "late", null));
        await click;
        Assert.Equal(0, _login.Logins);
        Assert.Equal(1, _accounts.Saves);
    }

    [Fact]
    public async Task LoginShowsProgressClearsPasswordAndSavesOnlyAfterSuccess()
    {
        var (provider, dialog) = await OpenAsync();
        Assert.True(provider.Find(".password-login-submit").HasAttribute("disabled"));
        Fill(provider);
        provider.WaitForAssertion(() =>
            Assert.False(provider.Find(".password-login-submit").HasAttribute("disabled"))
        );
        var click = provider.Find(".password-login-submit").ClickAsync(new());
        provider.WaitForAssertion(() => Assert.Contains("正在登录", provider.Markup));
        Assert.Equal(0, _accounts.Saves);
        Assert.True(provider.Find(".password-login-submit").HasAttribute("disabled"));
        Assert.Equal("", provider.Find("input[type=password]").GetAttribute("value") ?? "");
        await provider.InvokeAsync(() => _login.Response.SetResult(Success()));
        await click;
        var result = await dialog.Result;
        Assert.False(result!.Canceled);
        Assert.Equal("1001", result.Data);
        Assert.Equal(1, _accounts.Saves);
    }

    [Fact]
    public async Task SaveFailureOffersSaveRetryWithoutRepeatingPasswordLogin()
    {
        _accounts.FailFirst = true;
        var (provider, dialog) = await OpenAsync();
        Fill(provider);
        var click = provider.Find(".password-login-submit").ClickAsync(new());
        await provider.InvokeAsync(() => _login.Response.SetResult(Success()));
        await click;
        provider.WaitForAssertion(() => Assert.Contains("重试保存账号", provider.Markup));
        Assert.Contains("保存账号失败", provider.Markup);
        Assert.DoesNotContain("synthetic internal details", provider.Markup);
        Assert.Equal(1, _login.Logins);
        await provider.Find(".password-login-submit").ClickAsync(new());
        Assert.False((await dialog.Result)!.Canceled);
        Assert.Equal(1, _login.Logins);
        Assert.Equal(2, _accounts.Saves);
    }

    [Fact]
    public async Task RejectedPasswordShowsReasonAndDoesNotSaveAccount()
    {
        var (provider, dialog) = await OpenAsync();
        Fill(provider);
        var click = provider.Find(".password-login-submit").ClickAsync(new());
        await provider.InvokeAsync(() =>
            _login.Response.SetException(new PasswordLoginException("账号或密码错误，请重新输入"))
        );
        await click;
        Assert.Contains("账号或密码错误", provider.Markup);
        Assert.Equal(0, _accounts.Saves);
        Assert.True(provider.Find(".password-login-submit").HasAttribute("disabled"));
        Assert.False(dialog.Result!.IsCompleted);
    }

    [Fact]
    public async Task QrSuccessWinsAgainstLatePasswordResponseAndSavesOnce()
    {
        var (provider, dialog) = await OpenAsync();
        var qrResult = provider.FindComponent<QrLoginPanel>().Instance.OnAuthenticated;
        Fill(provider);
        var passwordClick = provider.Find(".password-login-submit").ClickAsync(new());
        provider.WaitForAssertion(() => Assert.Equal(1, _login.Logins), TimeSpan.FromSeconds(5));
        await provider.InvokeAsync(() => qrResult.InvokeAsync(Success().Cookie!));
        Assert.False((await dialog.Result)!.Canceled);
        await provider.InvokeAsync(() => _login.Response.SetResult(Success()));
        await passwordClick;
        Assert.Equal(1, _accounts.Saves);
    }

    [Fact]
    public async Task PasswordSuccessIgnoresLateQrResult()
    {
        var (provider, dialog) = await OpenAsync();
        var qrResult = provider.FindComponent<QrLoginPanel>().Instance.OnAuthenticated;
        Fill(provider);
        var click = provider.Find(".password-login-submit").ClickAsync(new());
        await provider.InvokeAsync(() => _login.Response.SetResult(Success()));
        await click;
        Assert.False((await dialog.Result)!.Canceled);
        await provider.InvokeAsync(() => qrResult.InvokeAsync(Success().Cookie!));
        Assert.Equal(1, _accounts.Saves);
    }

    [Fact]
    public async Task QrSaveFailureRetriesExistingLoginState()
    {
        _accounts.FailFirst = true;
        var (provider, dialog) = await OpenAsync();
        var qrResult = provider.FindComponent<QrLoginPanel>().Instance.OnAuthenticated;
        await provider.InvokeAsync(() => qrResult.InvokeAsync(Success().Cookie!));
        provider.WaitForAssertion(() => Assert.Contains("重试保存账号", provider.Markup));
        Assert.Empty(provider.FindComponents<QrLoginPanel>());
        Assert.Equal(0, _login.Logins);
        await provider.Find(".password-login-submit").ClickAsync(new());
        Assert.False((await dialog.Result)!.Canceled);
        Assert.Equal(2, _accounts.Saves);
    }

    [Fact]
    public async Task CancelIgnoresLateQrResult()
    {
        var (provider, dialog) = await OpenAsync();
        var qrResult = provider.FindComponent<QrLoginPanel>().Instance.OnAuthenticated;
        await provider
            .FindAll("button")
            .Single(button => button.TextContent == "取消")
            .ClickAsync(new());
        Assert.True((await dialog.Result)!.Canceled);
        await provider.InvokeAsync(() => qrResult.InvokeAsync(Success().Cookie!));
        Assert.Equal(0, _accounts.Saves);
    }

    private async Task<(
        IRenderedComponent<MudDialogProvider> Provider,
        IDialogReference Dialog
    )> OpenAsync()
    {
        var provider = RenderComponent<MudDialogProvider>();
        var service = ((IServiceProvider)Services).GetRequiredService<IDialogService>();
        var dialog = await provider.InvokeAsync(() =>
            service.ShowAsync<PasswordLoginDialog>("B 站密码登录")
        );
        provider.WaitForAssertion(() => Assert.Single(provider.FindAll(".password-login-submit")));
        return (provider, dialog);
    }

    private static void Fill(IRenderedComponent<MudDialogProvider> provider)
    {
        provider.Find("input[autocomplete=username]").Input("synthetic@example.invalid");
        provider.Find("input[type=password]").Input("synthetic-password");
    }

    private static PasswordLoginResult Success() =>
        new(
            PasswordLoginStatus.Success,
            "登录成功",
            new(
                new()
                {
                    ["DedeUserID"] = "1001",
                    ["SESSDATA"] = "synthetic",
                    ["bili_jct"] = "synthetic",
                }
            )
        );

    private sealed class LoginStub : IBiliPasswordLoginService
    {
        public TaskCompletionSource<PasswordLoginResult> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Logins { get; private set; }
        public int Prepares { get; private set; }
        public TimeProvider Clock { get; set; } = TimeProvider.System;
        public bool EnforceCaptchaExpiry { get; set; }
        public string CaptchaType { get; set; } = "geetest";
        public PasswordLoginProof? LastProof { get; private set; }
        public TaskCompletionSource<PasswordLoginCaptcha>? DelayedCaptcha { get; set; }
        private DateTimeOffset _preparedAt;
        public int VerificationRetrySeconds => 60;

        public Task<PasswordLoginCaptcha> PrepareCaptchaAsync(
            bool verification,
            CancellationToken cancellationToken
        )
        {
            Prepares++;
            _preparedAt = Clock.GetUtcNow();
            return DelayedCaptcha?.Task
                ?? Task.FromResult(
                    new PasswordLoginCaptcha(
                        CaptchaType,
                        "synthetic-gt",
                        "challenge",
                        CaptchaType == "img" ? "data:image/png;base64," : null
                    )
                );
        }

        public Task<PasswordLoginResult> LoginAsync(
            string username,
            string password,
            PasswordLoginProof proof,
            CancellationToken cancellationToken
        )
        {
            Logins++;
            LastProof = proof;
            if (EnforceCaptchaExpiry && Clock.GetUtcNow() - _preparedAt > TimeSpan.FromMinutes(2))
                return Task.FromException<PasswordLoginResult>(
                    new PasswordLoginException("验证码已过期，请刷新验证码")
                );
            return Response.Task;
        }

        public Task SendVerificationCodeAsync(
            PasswordLoginProof proof,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public Task<PasswordLoginResult> VerifyAsync(
            string code,
            CancellationToken cancellationToken
        ) => Response.Task;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-07T00:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AccountsStub : IBiliAccountPageWorkflow
    {
        public bool FailFirst { get; set; }
        public int Saves { get; private set; }

        public Task QrLoginCompleteAsync(BiliCookie cookie)
        {
            Saves++;
            return FailFirst && Saves == 1
                ? Task.FromException(new IOException("synthetic internal details"))
                : Task.CompletedTask;
        }

        public Task<List<BiliAccountDto>> GetAllAccountsAsync() =>
            Task.FromResult(new List<BiliAccountDto>());

        public Task AddAsync(string cookieStr) => Task.CompletedTask;

        public Task UpdateAsync(int index, string cookieStr) => Task.CompletedTask;

        public Task DeleteAsync(int index) => Task.CompletedTask;

        public Task ReorderAsync(int fromIndex, int toIndex) => Task.CompletedTask;

        public Task<QrLoginGenerateResult> QrLoginGenerateAsync() =>
            throw new NotSupportedException();

        public Task<QrLoginCheckResult> QrLoginPollAsync(string qrcodeKey) =>
            throw new NotSupportedException();
    }
}
