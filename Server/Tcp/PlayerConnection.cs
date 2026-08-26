using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RPGReServer.Auth;
using RPGReServer.Protocol;

namespace RPGReServer.Tcp;

/// <summary>一个已连接的 TCP 客户端（对应游戏内一名玩家）</summary>
public class PlayerConnection : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null
    };

    private readonly System.Net.Sockets.TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly RoomManager _rooms;
    private readonly AccountStore _accounts;
    private readonly ServerConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sendLock = new();

    public string Username { get; private set; } = "";
    public string Token { get; private set; } = "";
    public bool LoggedIn => Token != "";
    public Room? CurrentRoom { get; set; }
    public bool IsOwner => CurrentRoom != null && CurrentRoom.Owner == this;
    public bool IsReady { get; set; }
    public bool GameEnded { get; set; }

    public PlayerConnection(System.Net.Sockets.TcpClient client, RoomManager rooms, AccountStore accounts, ServerConfig config)
    {
        _client = client;
        _stream = client.GetStream();
        _rooms = rooms;
        _accounts = accounts;
        _config = config;
    }

    public async Task RunLoop()
    {
        try
        {
            // 读原始字节流（对齐客户端 General.Receive：累积 TCP 数据）
            var buffer = new byte[4096];
            var data = new List<byte>();
            while (!_cts.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer, _cts.Token);
                if (read <= 0) break;
                data.AddRange(buffer.Take(read));

                while (TryExtractNextPack(data, out string json))
                {
                    await ProcessPack(json);
                }

                if (data.Count > 1024 * 1024 * 4)
                {
                    Log.Info($"[TCP] 客户端 {Username} 接收缓冲溢出，断开连接");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Info($"[TCP] 客户端 {Username} 连接中断: {e.Message}");
        }
        finally
        {
            _rooms.RemovePlayer(this);
        }
    }

    /// <summary>
    /// 从字节缓冲中提取第一个完整 JSON 包（字符串感知的花括号配平，兼容消息内容含 {} 的情况）。
    /// 未完整的包保留在缓冲中等待后续数据。{ } " \ 均为单字节 ASCII，不会与 UTF-8 多字节字符混淆。
    /// </summary>
    private static bool TryExtractNextPack(List<byte> data, out string json)
    {
        json = null;
        bool inString = false, escaped = false;
        int depth = 0;
        for (int i = 0; i < data.Count; i++)
        {
            byte b = data[i];
            if (escaped) { escaped = false; continue; }
            if (b == (byte)'\\') { if (inString) escaped = true; continue; }
            if (b == (byte)'"') { inString = !inString; continue; }
            if (inString) continue;
            if (b == (byte)'{') depth++;
            else if (b == (byte)'}') depth--;
            if (depth == 0 && i > 0)
            {
                json = Encoding.UTF8.GetString(data.GetRange(0, i + 1).ToArray());
                data.RemoveRange(0, i + 1);
                return true;
            }
        }
        return false;
    }

    private async Task ProcessPack(string json)
    {
        ClientPack? pack;
        try { pack = JsonSerializer.Deserialize<ClientPack>(json); }
        catch { Log.Info($"[TCP] 无法解析包: {json}"); return; }
        if (pack == null || string.IsNullOrEmpty(pack.Operate)) return;

        Log.Info($"[TCP] {Username} -> {pack.Operate}");

        CurrentRequestOperate = pack.Operate;
        switch (pack.Operate)
        {
            case "Ping":
                SendPingResponse();
                break;
            case "User_LoginToServer":
                await HandleLogin(pack);
                break;
            case "User_LeaveServer":
                _rooms.RemovePlayer(this);
                break;
            case "Server_Sync":
                SendSync();
                break;
            case "User_CreateNewRoom":
                _rooms.CreateRoom(this, pack.Addition);
                break;
            case "User_CloseRoom":
                _rooms.CloseRoom(this);
                break;
            case "User_JoinRoom":
                _rooms.JoinRoom(this, pack.Addition);
                break;
            case "User_QuitRoom":
                _rooms.QuitRoom(this);
                break;
            case "User_Ready":
                IsReady = true;
                SendBack(true, "已准备");
                break;
            case "User_UnReady":
                IsReady = false;
                SendBack(true, "已取消准备");
                break;
            case "User_GameEnd":
                GameEnded = true;
                break;
            case "Room_UserQuitGame":
                _rooms.UserQuitGame(this);
                break;
            case "Room_UpdateSong":
                _rooms.UpdateSong(this, pack.Addition);
                break;
            case "Room_GameStart":
                _rooms.GameStart(this);
                break;
            case "Room_SendMessage":
                _rooms.SendMessage(this, pack.Addition);
                break;
            case "Room_GetRoomSongId":
                _rooms.SendRoomSongId(this);
                break;
            case "Room_Sync":
                _rooms.SendRoomInfo(this);
                break;
            case "Game_ScoreSync":
                _rooms.ScoreSync(this, pack.Addition);
                break;
            default:
                Log.Info($"[TCP] 未知操作: {pack.Operate}");
                SendBack(false, "未知操作");
                break;
        }

        await Task.CompletedTask;
    }

    private void SendPingResponse()
    {
        Send(new PingReceiveData
        {
            Type = "Back",
            Status = true,
            Name = _config.ServerName,
            Motd = _config.ServerMotd,
            IsDebug = _config.TestMode,
            Version = _config.ProtocolVersion,
            IsOnline = true,
            EnableChartUpload = _config.EnableChartUpload,
            ReceiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    private async Task HandleLogin(ClientPack pack)
    {
        string username = pack.Username ?? "";
        string verifyToken = pack.VerifyToken ?? pack.LoginToken ?? "";

        if (verifyToken == "")
        {
            // 允许无 token 游客登录（本地测试方便），分配一个临时 token
            Token = Guid.NewGuid().ToString("N");
            Username = string.IsNullOrEmpty(username) ? "Guest" + Random.Shared.Next(1000, 9999) : username;
            Send(new LoginReceive { Status = true, Token = Token, ServerId = _config.ServerId, Message = "登录成功（游客模式）" });
            Log.Info($"[TCP] 游客 {Username} 登录");
            return;
        }

        if (_accounts.VerifyToken(username, verifyToken, out var acc))
        {
            Username = username;
            Token = _accounts.RotateToken(username);
            Send(new LoginReceive { Status = true, Token = Token, ServerId = _config.ServerId, Message = "登录成功" });
            Log.Info($"[TCP] 用户 {Username} 登录");
        }
        else
        {
            Send(new LoginReceive { Status = false, Message = "登录失败：token 无效" });
        }

        await Task.CompletedTask;
    }

    private void SendSync()
    {
        var rooms = _rooms.GetRoomSummaries();
        Send(new SyncServerReceive
        {
            Status = true,
            List = rooms,
            EnableChartUpload = _config.EnableChartUpload,
            ChartServerUrl = _config.EnableChartUpload ? _config.PublicBaseUrl : null
        });
    }

    public void SendBack(bool status, string? message = null)
        => Send(new BackReceiveData { Status = status, Message = message, Operate = CurrentRequestOperate });

    public string CurrentRequestOperate { get; private set; } = "";

    public void Send(object obj)
    {
        if (obj is BackReceiveData back && string.IsNullOrEmpty(back.Operate))
            back.Operate = CurrentRequestOperate;
        string json = JsonSerializer.Serialize(obj, JsonOpts);
        SendRaw(json);
    }

    /// <summary>直接发送预序列化的 JSON 字符串</summary>
    public void SendRaw(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        lock (_sendLock)
        {
            if (_client.Connected) _stream.Write(bytes, 0, bytes.Length);
        }
        Log.Info($"[TCP] {Username} <- {json}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
    }
}
