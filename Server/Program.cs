using RPGReServer;
using RPGReServer.Auth;
using RPGReServer.Http;
using RPGReServer.Tcp;

Console.OutputEncoding = System.Text.Encoding.UTF8;

Log.Info("========= RPGR Community Server =========");
Log.Info("社区版服务器 - 账号 + 多人联机");

// 配置（默认本地运行，可通过命令行覆盖）
var config = new ServerConfig();
bool resetAccounts = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host" when i + 1 < args.Length: config.Host = args[++i]; break;
        case "--http" when i + 1 < args.Length: config.HttpPort = int.Parse(args[++i]); break;
        case "--tcp" when i + 1 < args.Length: config.TcpPort = int.Parse(args[++i]); break;
        case "--name" when i + 1 < args.Length: config.ServerName = args[++i]; break;
        case "--motd" when i + 1 < args.Length: config.ServerMotd = args[++i]; break;
        case "--data" when i + 1 < args.Length: config.DataDirectory = Path.GetFullPath(args[++i]); break;
        case "--public-url" when i + 1 < args.Length: config.PublicBaseUrl = args[++i].TrimEnd('/'); break;
        case "--public-tcp-host" when i + 1 < args.Length: config.PublicTcpHost = args[++i]; break;
        case "--smtp-host" when i + 1 < args.Length: config.Smtp.Host = args[++i]; break;
        case "--smtp-port" when i + 1 < args.Length: config.Smtp.Port = int.Parse(args[++i]); break;
        case "--smtp-user" when i + 1 < args.Length: config.Smtp.Username = args[++i]; break;
        case "--smtp-pass" when i + 1 < args.Length: config.Smtp.Password = args[++i]; break;
        case "--smtp-from" when i + 1 < args.Length: config.Smtp.From = args[++i]; break;
        case "--reset-accounts": resetAccounts = true; break;
    }
}

var accounts = new AccountStore(config.DataDirectory);
if (resetAccounts)
{
    string adminUsername = Environment.GetEnvironmentVariable("RPGR_ADMIN_USERNAME") ?? "";
    string adminPassword = Environment.GetEnvironmentVariable("RPGR_ADMIN_PASSWORD") ?? "";
    if (!AccountStore.IsValidUsername(adminUsername) || adminPassword.Length is < 8 or > 128)
        throw new InvalidOperationException("重置账号需要有效的 RPGR_ADMIN_USERNAME 和 RPGR_ADMIN_PASSWORD 环境变量");
    accounts.ResetToSingleAdmin(adminUsername, adminPassword);
    Log.Info($"[Auth] 账号已重置，仅保留管理员 {adminUsername}");
    return;
}
Log.Info($"[Auth] 数据目录: {config.DataDirectory}");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Log.Info($"[Config] HTTP={config.Host}:{config.HttpPort}  TCP={config.TcpPort}");
Log.Info("启动中... 按 Ctrl+C 退出");

var rooms = new RoomManager();
var mail = new MailService(config.Smtp);
var announcements = new AnnouncementStore(config.DataDirectory);
var httpTask = HttpService.Run(accounts, rooms, announcements, mail, config, cts.Token);
var tcpTask = TcpService.Run(accounts, rooms, config, cts.Token);

await Task.WhenAll(httpTask, tcpTask);
