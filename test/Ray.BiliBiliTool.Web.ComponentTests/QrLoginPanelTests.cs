using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.Web.Components.Pages.BiliAccount;
using Ray.BiliBiliTool.Web.Services.Pages.BiliAccount;
using Xunit;

namespace Ray.BiliBiliTool.Web.ComponentTests;

public sealed class QrLoginPanelTests : TestContext
{
    private readonly Clock _clock = new();
    private readonly IBiliAccountPageWorkflow _workflow = DispatchProxy.Create<
        IBiliAccountPageWorkflow,
        AccountsProxy
    >();
    private AccountsProxy Accounts => (AccountsProxy)_workflow;

    public QrLoginPanelTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton(_workflow);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task WaitingForPhoneConfirmationDoesNotCompleteLogin()
    {
        var received = new List<BiliCookie>();
        Accounts.Results.Enqueue(
            new()
            {
                Status = QrLoginStatus.Waiting,
                Message = "二维码已扫描，未确认",
                Cookie = Cookie(),
            }
        );
        Accounts.Results.Enqueue(new() { Status = QrLoginStatus.Success, Cookie = Cookie() });
        var panel = RenderComponent<QrLoginPanel>(p =>
            p.Add(x => x.OnAuthenticated, cookie => received.Add(cookie))
        );
        await panel.InvokeAsync(() => _clock.Timers.Last().Fire());
        panel.WaitForAssertion(() => Assert.Contains("请在手机上确认登录", panel.Markup));
        Assert.Empty(received);
        panel.WaitForAssertion(() => Assert.Equal(2, _clock.Timers.Count));
        await panel.InvokeAsync(() => _clock.Timers.Last().Fire());
        panel.WaitForAssertion(() => Assert.Single(received));
        Assert.Equal(2, Accounts.Polls);
    }

    [Fact]
    public async Task ExpiredCodeCanBeRefreshedWithANewSession()
    {
        Accounts.Results.Enqueue(new() { Status = QrLoginStatus.Expired });
        var panel = RenderComponent<QrLoginPanel>();
        await panel.InvokeAsync(() => _clock.Timers.Last().Fire());
        panel.WaitForAssertion(() => Assert.Contains("二维码已过期", panel.Markup));
        await panel.Find(".refresh-login-qr").ClickAsync(new());
        Assert.Equal(2, Accounts.Generations);
        Assert.Contains("等待扫码", panel.Markup);
        Assert.True(_clock.Timers.First().Disposed);
    }

    [Fact]
    public async Task ClosingPanelIgnoresAnInFlightSuccessfulPoll()
    {
        var received = 0;
        Accounts.PendingPoll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = RenderComponent<QrLoginPanel>(p =>
            p.Add(x => x.OnAuthenticated, _ => received++)
        );
        await panel.InvokeAsync(() => _clock.Timers.Last().Fire());
        panel.WaitForAssertion(() => Assert.Equal(1, Accounts.Polls));
        DisposeComponents();
        Accounts.PendingPoll.SetResult(new() { Status = QrLoginStatus.Success, Cookie = Cookie() });
        await Task.Yield();
        Assert.Equal(0, received);
        Assert.True(_clock.Timers.Last().Disposed);
    }

    [Fact]
    public async Task ClosingPanelCancelsPendingGeneration()
    {
        Accounts.PendingGenerate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = RenderComponent<QrLoginPanel>();
        DisposeComponents();
        Accounts.PendingGenerate.SetResult(Generated());
        await Task.Yield();
        Assert.Empty(_clock.Timers);
        Assert.Equal(0, Accounts.Polls);
    }

    private static BiliCookie Cookie() =>
        new(
            new()
            {
                ["DedeUserID"] = "1001",
                ["SESSDATA"] = "synthetic",
                ["bili_jct"] = "synthetic",
            }
        );

    private static QrLoginGenerateResult Generated() =>
        new()
        {
            QrImageBase64 = "synthetic-image",
            QrcodeKey = "synthetic-key",
            OnlineUrl = "",
        };

    public class AccountsProxy : DispatchProxy
    {
        public Queue<QrLoginCheckResult> Results { get; } = new();
        public TaskCompletionSource<QrLoginCheckResult>? PendingPoll { get; set; }
        public TaskCompletionSource<QrLoginGenerateResult>? PendingGenerate { get; set; }
        public int Polls { get; private set; }
        public int Generations { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IBiliAccountPageWorkflow.QrLoginGenerateAsync))
            {
                Generations++;
                return PendingGenerate?.Task ?? Task.FromResult(Generated());
            }
            if (method?.Name == nameof(IBiliAccountPageWorkflow.QrLoginPollAsync))
            {
                Polls++;
                return PendingPoll?.Task
                    ?? Task.FromResult(
                        Results.Count > 0
                            ? Results.Dequeue()
                            : new QrLoginCheckResult { Status = QrLoginStatus.Waiting }
                    );
            }
            throw new NotSupportedException();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public List<ClockTimer> Timers { get; } = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer = new ClockTimer(callback, state);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ClockTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }

        public void Fire()
        {
            if (!Disposed)
                callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
