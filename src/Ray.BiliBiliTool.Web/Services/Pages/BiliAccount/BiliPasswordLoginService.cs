using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace Ray.BiliBiliTool.Web.Services.Pages.BiliAccount;

// Each dialog receives its own service. Login tickets never enter configuration or logs.
public sealed class BiliPasswordLoginService(HttpClient client, TimeProvider clock)
    : IBiliPasswordLoginService
{
    private CaptchaSession? _captcha;
    private RiskSession? _risk;
    private string? _verificationKey;
    private DateTimeOffset _codeSentAt = DateTimeOffset.MinValue;

    public int VerificationRetrySeconds =>
        Math.Max(0, (int)Math.Ceiling(60 - (clock.GetUtcNow() - _codeSentAt).TotalSeconds));

    public async Task<PasswordLoginCaptcha> PrepareCaptchaAsync(
        bool verification,
        CancellationToken cancellationToken
    )
    {
        _captcha = null;
        if (verification)
            RequireRiskSession();
        else
        {
            _risk = null;
            _verificationKey = null;
            _codeSentAt = DateTimeOffset.MinValue;
        }

        using var response = verification
            ? await PostAsync(
                "/x/safecenter/captcha/pre",
                new() { ["source"] = "main-fe" },
                cancellationToken
            )
            : await GetAsync("/x/passport-login/captcha?source=main_web", cancellationToken);
        using var document = await ReadSuccessAsync(response, cancellationToken);
        var data = document.RootElement.GetProperty("data");
        var type = Value(data, "type");
        var token = Value(data, "token");
        var gt = "";
        var challenge = "";
        if (data.TryGetProperty("recaptcha_type", out _))
        {
            type = Value(data, "recaptcha_type");
            token = Value(data, "recaptcha_token");
            gt = Value(data, "gee_gt");
            challenge = Value(data, "gee_challenge");
        }
        else if (data.TryGetProperty("geetest", out var geetest))
        {
            gt = Value(geetest, "gt");
            challenge = Value(geetest, "challenge");
        }

        if (string.IsNullOrEmpty(token) || type is not ("geetest" or "img"))
            throw new PasswordLoginException("当前验证方式暂不支持，请使用扫码登录");
        string? image = null;
        if (type == "geetest")
        {
            if (string.IsNullOrEmpty(gt) || string.IsNullOrEmpty(challenge))
                throw new PasswordLoginException("验证码获取失败，请重试");
        }
        else
        {
            using var imageResponse = await GetAsync(
                "https://api.bilibili.com/x/recaptcha/img?token=" + Uri.EscapeDataString(token),
                cancellationToken
            );
            var mediaType = imageResponse.Content.Headers.ContentType?.MediaType;
            if (
                !imageResponse.IsSuccessStatusCode
                || mediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")
            )
                throw new PasswordLoginException("验证码图片加载失败，请重试");
            var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length is 0 or > 262144)
                throw new PasswordLoginException("验证码图片加载失败，请重试");
            image = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
        }
        _captcha = new(token, type, challenge, verification, clock.GetUtcNow());
        return new(type, gt, challenge, image);
    }

    public async Task<PasswordLoginResult> LoginAsync(
        string username,
        string password,
        PasswordLoginProof proof,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            throw new PasswordLoginException("请输入手机号或邮箱及密码");
        var captcha = ConsumeCaptcha(proof, false);
        _risk = null;
        _verificationKey = null;
        using var keyResponse = await GetAsync("/x/passport-login/web/key", cancellationToken);
        using var keyDocument = await ReadSuccessAsync(keyResponse, cancellationToken);
        var key = keyDocument.RootElement.GetProperty("data");
        if (string.IsNullOrEmpty(Value(key, "hash")) || string.IsNullOrEmpty(Value(key, "key")))
            throw new PasswordLoginException("登录加密参数获取失败，请重试");
        string encrypted;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(Value(key, "key"));
            var bytes = Encoding.UTF8.GetBytes(Value(key, "hash") + password);
            try
            {
                encrypted = Convert.ToBase64String(rsa.Encrypt(bytes, RSAEncryptionPadding.Pkcs1));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new PasswordLoginException("登录加密失败，请重试");
        }
        var form = new Dictionary<string, string>
        {
            ["username"] = username.Trim(),
            ["password"] = encrypted,
            ["source"] = "main_web",
            ["keep"] = "0",
            ["token"] = captcha.Token,
            ["go_url"] = "https://www.bilibili.com",
        };
        AddProof(form, proof, captcha, false);
        using var response = await PostAsync(
            "/x/passport-login/web/login",
            form,
            cancellationToken
        );
        using var document = await ReadSuccessAsync(response, cancellationToken);
        var data = document.RootElement.GetProperty("data");
        if (
            data.TryGetProperty("status", out var status)
            && status.TryGetInt32(out var number)
            && number == 0
        )
            return Complete(response);
        var url = Value(data, "url");
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || uri.Host != "passport.bilibili.com"
            || uri.Port != 443
            || uri.UserInfo.Length != 0
        )
            throw new PasswordLoginException("登录结果异常，请重新登录");
        var query = QueryHelpers.ParseQuery(uri.Query);
        var ticket = query.GetValueOrDefault("tmp_token").ToString();
        if (string.IsNullOrEmpty(ticket))
            ticket = query.GetValueOrDefault("tmp_code").ToString();
        var scene = query.GetValueOrDefault("scene").ToString();
        if (string.IsNullOrEmpty(scene))
            scene = "loginTelCheck";
        var source = query.GetValueOrDefault("source").ToString();
        if (string.IsNullOrEmpty(source))
            source = "main_web";
        if (
            string.IsNullOrEmpty(ticket)
            || scene is not ("loginTelCheck" or "deviceVerify" or "secLogin")
        )
            return new(
                PasswordLoginStatus.OfficialVerificationRequired,
                "请在 B 站完成此次账号验证后重新登录",
                VerificationUrl: uri.AbsoluteUri
            );
        using var infoResponse = await GetAsync(
            "/x/safecenter/user/info?tmp_code=" + Uri.EscapeDataString(ticket),
            cancellationToken
        );
        using var infoDocument = await ReadSuccessAsync(infoResponse, cancellationToken);
        var info = infoDocument.RootElement.GetProperty("data").GetProperty("account_info");
        var phone = Flag(info, "bind_tel");
        var email = Flag(info, "bind_mail");
        if (!phone && (!email || scene != "secLogin"))
            return new(
                PasswordLoginStatus.OfficialVerificationRequired,
                "请在 B 站完成手机号绑定后重新登录",
                VerificationUrl: uri.AbsoluteUri
            );
        _risk = new(
            ticket,
            query.GetValueOrDefault("request_id").ToString(),
            scene,
            source,
            !phone,
            clock.GetUtcNow()
        );
        return new(
            PasswordLoginStatus.VerificationRequired,
            phone ? "请验证绑定的手机号" : "请验证绑定的邮箱",
            VerificationTarget: phone ? Value(info, "hide_tel") : Value(info, "hide_mail")
        );
    }

    public async Task SendVerificationCodeAsync(
        PasswordLoginProof proof,
        CancellationToken cancellationToken
    )
    {
        var risk = RequireRiskSession();
        if (VerificationRetrySeconds > 0)
            throw new PasswordLoginException($"请在 {VerificationRetrySeconds} 秒后重新发送");
        var captcha = ConsumeCaptcha(proof, true);
        var form = new Dictionary<string, string>
        {
            ["tmp_code"] = risk.Ticket,
            [risk.Email ? "mail_type" : "sms_type"] = risk.Scene,
            ["recaptcha_token"] = captcha.Token,
        };
        AddProof(form, proof, captcha, true);
        using var response = await PostAsync(
            risk.Email ? "/x/safecenter/common/email/send" : "/x/safecenter/common/sms/send",
            form,
            cancellationToken
        );
        using var document = await ReadSuccessAsync(response, cancellationToken);
        var key = Value(document.RootElement.GetProperty("data"), "captcha_key");
        if (string.IsNullOrEmpty(key))
            throw new PasswordLoginException("验证码发送结果异常，请重试");
        _verificationKey = key;
        _codeSentAt = clock.GetUtcNow();
    }

    public async Task<PasswordLoginResult> VerifyAsync(
        string code,
        CancellationToken cancellationToken
    )
    {
        var risk = RequireRiskSession();
        if (string.IsNullOrEmpty(_verificationKey))
            throw new PasswordLoginException("请先发送验证码");
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 16)
            throw new PasswordLoginException("请输入收到的验证码");
        var form = new Dictionary<string, string>
        {
            ["tmp_code"] = risk.Ticket,
            ["captcha_key"] = _verificationKey,
            ["code"] = code.Trim(),
            ["source"] = risk.Source,
        };
        var endpoint = "/x/safecenter/login/tel/verify";
        if (risk.Scene == "secLogin")
        {
            endpoint = "/x/safecenter/sec/verify";
            form["verify_type"] = risk.Email ? "email" : "sms";
        }
        else if (risk.Scene == "deviceVerify")
        {
            endpoint = "/x/safecenter/user_device/verify";
            form["sms_type"] = risk.Scene;
        }
        else
        {
            form["type"] = risk.Scene;
            if (!string.IsNullOrEmpty(risk.RequestId))
                form["request_id"] = risk.RequestId;
        }
        using var response = await PostAsync(endpoint, form, cancellationToken);
        using var document = await ReadSuccessAsync(response, cancellationToken);
        var exchangeCode = Value(document.RootElement.GetProperty("data"), "code");
        if (string.IsNullOrEmpty(exchangeCode))
            throw new PasswordLoginException("账号验证结果异常，请重新登录");
        // Verification tickets are consumed even if the following exchange fails.
        _risk = null;
        _verificationKey = null;
        using var exchange = await PostAsync(
            "/x/passport-login/web/exchange_cookie",
            new()
            {
                ["source"] = risk.Source,
                ["code"] = exchangeCode,
                ["go_url"] = "https://www.bilibili.com",
            },
            cancellationToken
        );
        using var exchangeDocument = await ReadSuccessAsync(exchange, cancellationToken);
        return Complete(exchange);
    }

    private static PasswordLoginResult Complete(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
            throw new PasswordLoginException("未获取到登录状态，请重新登录");
        var cookie = CookieStrFactory<BiliCookie>.CreateNew(
            CookieInfo.ConvertSetCkHeadersToCkStr(headers)
        );
        try
        {
            cookie.Check();
        }
        catch (Exception)
        {
            throw new PasswordLoginException("登录状态不完整，请重新登录");
        }
        return new(PasswordLoginStatus.Success, "登录成功", cookie);
    }

    private CaptchaSession ConsumeCaptcha(PasswordLoginProof proof, bool verification)
    {
        var captcha = _captcha;
        if (
            captcha is null
            || captcha.Verification != verification
            || clock.GetUtcNow() - captcha.CreatedAt > TimeSpan.FromMinutes(2)
        )
            throw new PasswordLoginException("验证码已过期，请刷新验证码");
        if (
            captcha.Type == "geetest"
            && (
                string.IsNullOrEmpty(proof.Validate)
                || string.IsNullOrEmpty(proof.Seccode)
                || !proof.Challenge.StartsWith(captcha.Challenge, StringComparison.Ordinal)
            )
        )
            throw new PasswordLoginException("请先完成验证码验证");
        if (captcha.Type == "img" && string.IsNullOrWhiteSpace(proof.ImageCode))
            throw new PasswordLoginException("请输入图片验证码");
        _captcha = null;
        return captcha;
    }

    private RiskSession RequireRiskSession() =>
        _risk is { } risk && clock.GetUtcNow() - risk.CreatedAt <= TimeSpan.FromMinutes(10)
            ? risk
            : throw new PasswordLoginException("登录验证已过期，请重新登录");

    private static void AddProof(
        Dictionary<string, string> form,
        PasswordLoginProof proof,
        CaptchaSession captcha,
        bool risk
    )
    {
        if (captcha.Type == "img")
            form[risk ? "img_code" : "captcha"] = proof.ImageCode.Trim();
        else
        {
            form[risk ? "gee_challenge" : "challenge"] = proof.Challenge;
            form[risk ? "gee_validate" : "validate"] = proof.Validate;
            form[risk ? "gee_seccode" : "seccode"] = proof.Seccode;
        }
    }

    private Task<HttpResponseMessage> GetAsync(string path, CancellationToken token) =>
        SendAsync(new(HttpMethod.Get, path), token);

    private Task<HttpResponseMessage> PostAsync(
        string path,
        Dictionary<string, string> form,
        CancellationToken token
    ) => SendAsync(new(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) }, token);

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken token
    )
    {
        using (request)
        {
            request.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
            );
            request.Headers.Referrer = new("https://passport.bilibili.com/login");
            request.Headers.TryAddWithoutValidation("Origin", "https://passport.bilibili.com");
            try
            {
                return await client.SendAsync(request, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new PasswordLoginException("连接 B 站超时，请重试");
            }
            catch (HttpRequestException)
            {
                throw new PasswordLoginException("连接 B 站失败，请重试");
            }
        }
    }

    private static async Task<JsonDocument> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken token
    )
    {
        if (!response.IsSuccessStatusCode)
            throw new PasswordLoginException("B 站登录服务暂时不可用，请重试");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        }
        catch (JsonException)
        {
            throw new PasswordLoginException("B 站登录服务返回异常，请重试");
        }
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new PasswordLoginException("B 站登录服务返回异常，请重试");
        }
        if (
            !document.RootElement.TryGetProperty("code", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var code)
            || code != 0
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
        )
        {
            var error =
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
                    ? parsed
                    : int.MinValue;
            document.Dispose();
            throw new PasswordLoginException(
                error switch
                {
                    -629 => "账号或密码错误，请重新输入",
                    -105 or 2406 => "验证码验证失败，请刷新验证码",
                    -662 or 2400 => "登录验证已过期，请重新登录",
                    _ => "B 站登录验证未通过，请重试",
                }
            );
        }
        return document;
    }

    private static string Value(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool Flag(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value)
        && (
            value.ValueKind == JsonValueKind.True
            || (
                value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number)
                && number != 0
            )
        );

    private sealed record CaptchaSession(
        string Token,
        string Type,
        string Challenge,
        bool Verification,
        DateTimeOffset CreatedAt
    );

    private sealed record RiskSession(
        string Ticket,
        string RequestId,
        string Scene,
        string Source,
        bool Email,
        DateTimeOffset CreatedAt
    );
}
