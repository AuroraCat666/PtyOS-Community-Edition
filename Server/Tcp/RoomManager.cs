using System.Text.Json;
using RPGReServer.Protocol;

namespace RPGReServer.Tcp;

public class Room
{
    public int Id { get; init; }
    public PlayerConnection Owner { get; set; } = null!;
    public List<PlayerConnection> Players { get; } = new();
    public string SongId { get; set; } = "";
    public string SongType { get; set; } = "empty";
    public SongInfo? SongInfo { get; set; }
    public bool GameRunning { get; set; }
    public bool IsPublic { get; set; } = true;

    public bool IsOwner(PlayerConnection p) => p == Owner;
}

public class RoomManager
{
    private readonly object _lock = new();
    private int _nextRoomId = 1;
    private readonly Dictionary<int, Room> _rooms = new();

    public List<RoomSummary> GetRoomSummaries()
    {
        lock (_lock)
        {
            return _rooms.Values
                .Where(r => !r.GameRunning)
                .Select(r => new RoomSummary { Id = r.Id, Owner = r.Owner.Username })
                .ToList();
        }
    }

    public object GetCommunitySnapshot()
    {
        lock (_lock)
        {
            var publicRooms = _rooms.Values.Where(r => r.IsPublic).ToList();
            return new
            {
                onlinePlayers = publicRooms.SelectMany(r => r.Players).Distinct().Count(),
                activeRooms = publicRooms.Count,
                runningGames = publicRooms.Count(r => r.GameRunning),
                rooms = publicRooms.OrderBy(r => r.Id).Select(r => new
                {
                    id = r.Id,
                    owner = r.Owner.Username,
                    players = r.Players.Select(p => new
                    {
                        username = p.Username,
                        ready = p.IsReady,
                        ended = p.GameEnded
                    }).ToArray(),
                    playerCount = r.Players.Count,
                    songId = r.SongId,
                    songName = r.SongInfo?.SongName ?? "未选择谱面",
                    songDifficulty = r.SongInfo?.SongDifficulty ?? "",
                    gameRunning = r.GameRunning
                }).ToArray()
            };
        }
    }

    public void CreateRoom(PlayerConnection player, Dictionary<string, object>? addition)
    {
        lock (_lock)
        {
            if (player.LoggedIn == false) { player.SendBack(false, "未登录"); return; }
            if (player.CurrentRoom != null) { player.SendBack(false, "你已在房间中"); return; }

            bool isPublic = true;
            if (addition != null && addition.TryGetValue("IsPublic", out var pubVal))
            {
                if (pubVal is bool b) isPublic = b;
                else if (bool.TryParse(pubVal.ToString(), out var parsed)) isPublic = parsed;
            }

            var room = new Room { Id = _nextRoomId++, Owner = player, IsPublic = isPublic };
            room.Players.Add(player);
            _rooms[room.Id] = room;
            player.CurrentRoom = room;

            player.Send(new CreateRoomReceive { Status = true, RoomId = room.Id.ToString(), Message = $"房间 {room.Id} 已创建" });
        }
    }

    public void JoinRoom(PlayerConnection player, Dictionary<string, object>? addition)
    {
        lock (_lock)
        {
            if (player.LoggedIn == false) { player.SendBack(false, "未登录"); return; }
            if (player.CurrentRoom != null) { player.SendBack(false, "你已在房间中"); return; }

            string roomIdStr = GetString(addition, "RoomID") ?? "";
            if (!int.TryParse(roomIdStr, out int roomId) || !_rooms.TryGetValue(roomId, out var room) || room.GameRunning)
            {
                player.SendBack(false, "房间不存在或游戏已开始");
                return;
            }

            room.Players.Add(player);
            player.CurrentRoom = room;
            player.SendBack(true, $"已加入房间 {roomId}");

            // 通知房间内其他人
            Broadcast(room, null,
                new MessageActiveReceive { Operate = "Message", Author = "joinRoom", IsServer = true, Message = $"{player.Username} 加入了房间" });
        }
    }

    public void QuitRoom(PlayerConnection player)
    {
        lock (_lock)
        {
            var room = player.CurrentRoom;
            if (room == null) { player.SendBack(false, "不在房间中"); return; }
            RemoveFromRoom(player, room, true);
        }
    }

    public void CloseRoom(PlayerConnection player)
    {
        lock (_lock)
        {
            var room = player.CurrentRoom;
            if (room == null) { player.SendBack(false, "不在房间中"); return; }
            if (!room.IsOwner(player)) { player.SendBack(false, "只有房主能关闭房间"); return; }

            foreach (var p in room.Players.ToList())
            {
                if (p == player) continue;
                p.Send(new ActiveReceiveData { Operate = "RoomClosed" });
                p.CurrentRoom = null;
            }
            player.SendBack(true, "房间已关闭");
            _rooms.Remove(room.Id);
            player.CurrentRoom = null;
        }
    }

    private void RemoveFromRoom(PlayerConnection player, Room room, bool sendOwnerClosed)
    {
        room.Players.Remove(player);
        player.CurrentRoom = null;
        player.IsReady = false;

        if (room.IsOwner(player))
        {
            // 房主退出，转移房主或关房
            var next = room.Players.FirstOrDefault();
            if (next != null)
            {
                room.Owner = next;
                Broadcast(room, null, new MessageActiveReceive { Operate = "Message", Author = "joinRoom", IsServer = true, Message = $"房主转移给 {next.Username}" });
            }
            else
            {
                _rooms.Remove(room.Id);
            }
        }
        else
        {
            Broadcast(room, null,
                new MessageActiveReceive { Operate = "Message", Author = "joinRoom", IsServer = true, Message = $"{player.Username} 退出了房间" });
        }

        player.SendBack(true, "已退出房间");
    }

    public void UpdateSong(PlayerConnection player, Dictionary<string, object>? addition)
    {
        lock (_lock)
        {
            var room = player.CurrentRoom;
            if (room == null) { player.SendBack(false, "不在房间中"); return; }
            if (!room.IsOwner(player)) { player.SendBack(false, "只有房主能选歌"); return; }

            string songId = GetString(addition, "songId") ?? "";
            string songType = GetString(addition, "songType") ?? "rep";
            SongInfo? songInfo = null;
            if (addition != null && addition.TryGetValue("songInfo", out var si) && si is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                songInfo = el.Deserialize<SongInfo>();
            }

            room.SongId = songId;
            room.SongType = songType;
            room.SongInfo = songInfo;
            room.GameRunning = false;

            player.SendBack(true, "谱面已更新");
            Broadcast(room, player, new UpdateSongActiveReceive
            {
                Operate = "UpdateSong",
                SongId = songId,
                SongType = songType,
                SongInfo = songInfo
            });
        }
    }

    public void GameStart(PlayerConnection player)
    {
        lock (_lock)
        {
            var room = player.CurrentRoom;
            if (room == null) { player.SendBack(false, "不在房间中"); return; }
            if (!room.IsOwner(player)) { player.SendBack(false, "只有房主能开始游戏"); return; }
            if (room.SongId == "" && room.SongType != "rep") { player.SendBack(false, "尚未选歌"); return; }
            if (room.Players.Any(p => !room.IsOwner(p) && !p.IsReady)) { player.SendBack(false, "还有玩家未准备"); return; }

            room.GameRunning = true;
            foreach (var p in room.Players) p.GameEnded = false;

            var names = room.Players.Select(p => p.Username).ToArray();
            var pack = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Type"] = "Active",
                ["operate"] = "GameStart",
                ["PlayerList"] = names
            });
            foreach (var p in room.Players) p.SendRaw(pack);
            Log.Info($"[TCP] 房间 {room.Id} 游戏开始，玩家: {string.Join(",", names)}");
        }
    }

    public void UserQuitGame(PlayerConnection player)
    {
        lock (_lock)
        {
            var room = player.CurrentRoom;
            if (room == null) return;
            player.GameEnded = true;
            Broadcast(room, null,
                new MessageActiveReceive { Operate = "Message", Author = "User_QuitGame", IsServer = true, Message = $"{player.Username} 退出了游戏" });
        }
    }

    public void SendMessage(PlayerConnection player, Dictionary<string, object>? addition)
    {
        lock (_lock)
        {
            var room = player.CurrentRoom;
            if (room == null) { player.SendBack(false, "不在房间中"); return; }
            string msg = GetString(addition, "Message") ?? "";
            player.SendBack(true, "消息已发送");
            Broadcast(room, null,
                new MessageActiveReceive { Operate = "Message", Author = player.Username, IsServer = false, Message = msg });
        }
    }

    public void SendRoomSongId(PlayerConnection player)
    {
        lock (_lock)
        {
            var room = player.CurrentRoom;
            if (room == null) { player.SendBack(false, "不在房间中"); return; }
            player.Send(new BackReceiveData { Status = true, Message = room.SongId });
        }
    }

    public void SendRoomInfo(PlayerConnection player)
    {
        lock (_lock)
        {
            var room = player.CurrentRoom;
            if (room == null) { player.SendBack(false, "不在房间中"); return; }
            player.Send(new RoomInfoReceive
            {
                Status = true,
                RoomInfo = new RoomInfo
                {
                    PlayerList = room.Players.Select(p => p.Username).ToArray(),
                    SelectedSongType = room.SongType,
                    SelectedSongID = room.SongId,
                    selectedSongInfo = room.SongInfo
                }
            });
        }
    }

    public void ScoreSync(PlayerConnection player, Dictionary<string, object>? addition)
    {
        lock (_lock)
        {
            var room = player.CurrentRoom;
            if (room == null) return;
            string score = GetString(addition, "Score") ?? "0";
            foreach (var p in room.Players)
            {
                if (p == player) continue;
                p.SendRaw(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["Type"] = "Active",
                    ["operate"] = "UpdateScore",
                    ["from"] = player.Username,
                    ["Score"] = score
                }));
            }
        }
    }

    public void RemovePlayer(PlayerConnection player)
    {
        lock (_lock)
        {
            var room = player.CurrentRoom;
            if (room == null) return;
            RemoveFromRoom(player, room, true);
        }
    }

    private void Broadcast(Room room, PlayerConnection? except, object pack)
    {
        foreach (var p in room.Players)
        {
            if (p == except) continue;
            p.Send(pack);
        }
    }

    private static string? GetString(Dictionary<string, object>? addition, string key)
    {
        if (addition == null || !addition.TryGetValue(key, out var val)) return null;
        return val?.ToString();
    }
}
