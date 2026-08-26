using System.Text.Json.Serialization;

namespace RPGReServer.Protocol;

/// <summary>客户端发来的通用请求包（ClientOperate 序列化为 operate 字段）</summary>
public class ClientPack
{
    [JsonPropertyName("operate")] public string? Operate { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("addition")] public Dictionary<string, object>? Addition { get; set; }
    [JsonPropertyName("verifyToken")] public string? VerifyToken { get; set; }
    [JsonPropertyName("loginToken")] public string? LoginToken { get; set; }
}

/// <summary>客户端操作类型（对应客户端 ClientOperate 枚举）</summary>
public enum ClientOperate
{
    Server_Sync = 0,
    User_LoginToServer,
    User_LeaveServer,
    User_CreateNewRoom,
    User_CloseRoom,
    User_JoinRoom,
    User_QuitRoom,
    User_Ready,
    User_UnReady,
    User_GameEnd,
    Room_UserQuitGame,
    Room_UpdateSong,
    Room_GameStart,
    Room_SendMessage,
    Room_GetRoomSongId,
    Room_Sync,
    Game_ScoreSync
}

/// <summary>服务器主动推送类型（对应客户端 ServerOperate 枚举）</summary>
public enum ServerOperate
{
    Message = 0,
    GameStart,
    RoomClosed,
    UpdateSong,
    ServerClosed,
    UpdateScore,
    PlayerQuit
}
