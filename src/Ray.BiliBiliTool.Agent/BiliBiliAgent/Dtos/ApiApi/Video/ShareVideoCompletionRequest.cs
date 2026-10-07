using Refit;

namespace Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Video;

public sealed class ShareVideoCompletionRequest(long aid, long cid, string csrf, long timestamp)
{
    [AliasAs("oid")]
    public long Aid { get; } = aid;

    [AliasAs("sid")]
    public long Cid { get; } = cid;

    [AliasAs("csrf")]
    public string Csrf { get; } = csrf;

    [AliasAs("ts")]
    public long Timestamp { get; } = timestamp;

    public string panel_type { get; } = "1";
    public string share_channel { get; } = "QQ";
    public string share_id { get; } = "main.ugc-video-detail.0.0.pv";
    public string share_origin { get; } = "vinfo_share";
    public string platform { get; } = "android";
    public string mobi_app { get; } = "android";
    public string device { get; } = "android";
    public string build { get; } = "8451100";
    public string disable_rcmd { get; } = "0";
    public string c_locale { get; } = "zh_CN";
    public string s_locale { get; } = "zh_CN";
    public string success { get; } = "true";
}
