using System.Net;
using System.Net.Sockets;
using RPGReServer.Auth;

namespace RPGReServer.Tcp;

public static class TcpService
{
    public static async Task Run(AccountStore accounts, RoomManager rooms, ServerConfig config, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, config.TcpPort);
        listener.Start();
        Log.Info($"[TCP] 联机服务已启动: 端口 {config.TcpPort}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleClient(client, rooms, accounts, config));
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private static async Task HandleClient(TcpClient client, RoomManager rooms, AccountStore accounts, ServerConfig config)
    {
        using var connection = new PlayerConnection(client, rooms, accounts, config);
        await connection.RunLoop();
    }
}
