namespace Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Video;

public class ShareVideoRequest
{
    public ShareVideoRequest(long aid, string csrf)
    {
        Aid = aid;
        Csrf = csrf;
    }

    public long Aid { get; set; }

    public string Csrf { get; set; }

    public string Eab_x { get; set; } = "2";

    public string Ramval { get; set; } = "0";

    public string Source { get; set; } = "pc_client_normal";

    public string Ga { get; set; } = "1";
}
