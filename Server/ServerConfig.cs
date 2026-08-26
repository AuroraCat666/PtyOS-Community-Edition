namespace RPGReServer;

public class ServerConfig
{
    /// <summary>HTTP 账号服务监听地址</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>HTTP 端口（客户端通过 manifest.json 发现）</summary>
    public int HttpPort { get; set; } = 8080;

    /// <summary>TCP 联机端口（客户端服务器列表里的 url:port）</summary>
    public int TcpPort { get; set; } = 26377;

    public string ServerName { get; set; } = "RPGR Community Server";
    public string ServerMotd { get; set; } = "社区版服务器";
    public string ServerId { get; set; } = "RPGR-COMMUNITY";
    public int ProtocolVersion { get; set; } = 8;
    public bool TestMode { get; set; } = true;
    public bool EnableChartUpload { get; set; } = true;
    public string ApiVersion { get; set; } = "v1.0";
    public string DataDirectory { get; set; } = Path.Combine(Environment.CurrentDirectory, "data");
    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:8080";
    public string PublicTcpHost { get; set; } = "127.0.0.1";
    public SmtpSettings Smtp { get; set; } = new();
}
