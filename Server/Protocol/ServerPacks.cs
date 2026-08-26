using System.Text.Json.Serialization;

namespace RPGReServer.Protocol;

/// <summary>通用返回包（Type=Back）。字段名严格对齐客户端 BackReceiveData</summary>
public class BackReceiveData
{
    [JsonPropertyName("Type")] public string Type { get; set; } = "Back";
    [JsonPropertyName("Status")] public bool Status { get; set; }
    [JsonPropertyName("msg")] public string? Message { get; set; }
    [JsonPropertyName("operate")] public string? Operate { get; set; }
}

/// <summary>服务器探测响应（对齐客户端 PingReceiveData）</summary>
public class PingReceiveData : BackReceiveData
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("Motd")] public string Motd { get; set; } = "";
    [JsonPropertyName("DebugServer")] public bool IsDebug { get; set; }
    [JsonPropertyName("RequireVersion")] public int Version { get; set; } = 8;
    [JsonPropertyName("ReceiveTime")] public long ReceiveTime { get; set; }
    [JsonPropertyName("OnlineMode")] public bool IsOnline { get; set; } = true;
    [JsonPropertyName("ChartUploadMode")] public bool EnableChartUpload { get; set; }
}

/// <summary>登录返回（对齐客户端 LoginReceive）</summary>
public class LoginReceive : BackReceiveData
{
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("ServerID")] public string? ServerId { get; set; }
}

/// <summary>服务器同步返回（对齐客户端 SyncServerReceive）</summary>
public class SyncServerReceive : BackReceiveData
{
    [JsonPropertyName("RoomList")] public List<RoomSummary> List { get; set; } = new();
    [JsonPropertyName("SupportChart")] public bool EnableChartUpload { get; set; }
    [JsonPropertyName("ChartServer")] public string? ChartServerUrl { get; set; }
}

public class RoomSummary
{
    [JsonPropertyName("RoomID")] public int Id { get; set; }
    [JsonPropertyName("Owner")] public string Owner { get; set; } = "";
}

/// <summary>创建房间返回（对齐客户端 CreateRoomReceive）</summary>
public class CreateRoomReceive : BackReceiveData
{
    [JsonPropertyName("RoomID")] public string? RoomId { get; set; }
}

/// <summary>房间信息返回（对齐客户端 RoomInfoReceive）</summary>
public class RoomInfoReceive : BackReceiveData
{
    [JsonPropertyName("SyncReturn")] public RoomInfo RoomInfo { get; set; } = new();
}

public class RoomInfo
{
    [JsonPropertyName("Room_PlayerList")] public string[] PlayerList { get; set; } = Array.Empty<string>();
    [JsonPropertyName("Room_SongType")] public string SelectedSongType { get; set; } = "empty";
    [JsonPropertyName("Room_SongId")] public string SelectedSongID { get; set; } = "";
    [JsonPropertyName("Room_SongInfo")] public object? selectedSongInfo { get; set; }
}

/// <summary>歌曲信息（对齐客户端 SongInfo）</summary>
public class SongInfo
{
    [JsonPropertyName("FolderName")] public string FolderName { get; set; } = "";
    [JsonPropertyName("SongName")] public string SongName { get; set; } = "";
    [JsonPropertyName("SongComposer")] public string SongComposer { get; set; } = "";
    [JsonPropertyName("SongDifficulty")] public string SongDifficulty { get; set; } = "";
    [JsonPropertyName("SongCharter")] public string SongCharter { get; set; } = "";
    [JsonPropertyName("SongIllustrator")] public string SongIllustrator { get; set; } = "";
}

/// <summary>主动推送包（Type=Active）</summary>
public class ActiveReceiveData
{
    [JsonPropertyName("Type")] public string Type { get; set; } = "Active";
    [JsonPropertyName("operate")] public string Operate { get; set; } = "";
}

public class MessageActiveReceive : ActiveReceiveData
{
    [JsonPropertyName("from")] public string Author { get; set; } = "";
    [JsonPropertyName("isServer")] public bool IsServer { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public class UpdateSongActiveReceive : ActiveReceiveData
{
    [JsonPropertyName("SongType")] public string SongType { get; set; } = "";
    [JsonPropertyName("songId")] public string SongId { get; set; } = "";
    [JsonPropertyName("songInfo")] public SongInfo? SongInfo { get; set; }
}
