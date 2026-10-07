using Ray.BiliBiliTool.Agent;

namespace Ray.BiliBiliTool.Web.Services.Pages.BiliAccount;

public sealed record PasswordLoginCaptcha(string Type, string Gt, string Challenge, string? Image);

public sealed record PasswordLoginProof(
    string Challenge = "",
    string Validate = "",
    string Seccode = "",
    string ImageCode = "",
    bool Cancelled = false
);

public enum PasswordLoginStatus
{
    Failed,
    Success,
    VerificationRequired,
    OfficialVerificationRequired,
}

public sealed record PasswordLoginResult(
    PasswordLoginStatus Status,
    string Message,
    BiliCookie? Cookie = null,
    string? VerificationTarget = null,
    string? VerificationUrl = null
)
{
    public override string ToString() => $"Password login: {Status}";
}

public interface IBiliPasswordLoginService
{
    Task<PasswordLoginCaptcha> PrepareCaptchaAsync(
        bool verification,
        CancellationToken cancellationToken
    );
    Task<PasswordLoginResult> LoginAsync(
        string username,
        string password,
        PasswordLoginProof proof,
        CancellationToken cancellationToken
    );
    Task SendVerificationCodeAsync(PasswordLoginProof proof, CancellationToken cancellationToken);
    Task<PasswordLoginResult> VerifyAsync(string code, CancellationToken cancellationToken);
    int VerificationRetrySeconds { get; }
}

public sealed class PasswordLoginException(string message) : Exception(message);
