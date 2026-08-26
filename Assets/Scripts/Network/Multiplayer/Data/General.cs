using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if UNITY_EDITOR
#endif

namespace Network.Multiplayer.Data
{
    public static class General
    {
        private static readonly UTF8Encoding NoBomUtf8Encoding = new(false);

        public static int Send(this Socket socket, object? value)
        {
            string messageToSend = JsonConvert.SerializeObject(value, Formatting.None);
            Debug.Log("尝试发送" + messageToSend);
            return socket.Send(NoBomUtf8Encoding.GetBytes(messageToSend));
        }

        private static readonly List<byte> ReceiveBuffer = new List<byte>();

        public static JObject[]? Receive(this Socket socket)
        {
            if (socket.Available <= 0) return null;
            byte[] buffer = new byte[16384];
            while (socket.Available > 0)
            {
                int length = socket.Receive(buffer);
                if (length <= 0) break;
                ReceiveBuffer.AddRange(buffer.Take(length));
            }

            return ExtractCompletePacks();
        }

        /// <summary>
        /// 从接收缓冲中提取所有完整 JSON 包，未完整的数据包保留在缓冲中等待下次接收。
        /// 在字节层面扫描（{ } " \ 均为单字节 ASCII，不会与 UTF-8 多字节字符混淆），
        /// 避免 TCP 分片与 UTF-8 跨包截断导致的丢包和解析异常。
        /// </summary>
        private static JObject[] ExtractCompletePacks()
        {
            List<JObject> packs = new List<JObject>();
            bool zhuanYi = false;
            bool isInString = false;
            int depth = 0;
            int start = 0;
            for (int i = 0; i < ReceiveBuffer.Count; i++)
            {
                byte b = ReceiveBuffer[i];
                if (b == (byte)'\\')
                {
                    if (isInString) zhuanYi = !zhuanYi;
                    continue;
                }
                if (b == (byte)'"')
                {
                    if (!zhuanYi) isInString = !isInString;
                    zhuanYi = false;
                    continue;
                }
                if (!isInString)
                {
                    if (b == (byte)'{') depth++;
                    else if (b == (byte)'}') depth--;
                }
                zhuanYi = false;
                if (depth == 0 && i > start)
                {
                    string json = NoBomUtf8Encoding.GetString(ReceiveBuffer.GetRange(start, i - start + 1).ToArray());
                    try
                    {
                        packs.Add(JObject.Parse(json));
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("无法解析服务器数据包（已忽略）：" + json + "\n" + e.Message);
                    }
                    start = i + 1;
                }
            }
            if (start > 0) ReceiveBuffer.RemoveRange(0, start);
            return packs.ToArray();
        }

        public static bool TryParseHost(string url, out IPEndPoint endPoint, out string displayUrl)
        {
            int maoHaoWeiZhi = url.LastIndexOf(":", StringComparison.Ordinal);
            if (maoHaoWeiZhi < 0)
            {
                endPoint = null;
                displayUrl = "";
                return false;
            }
            

            string host = url.Substring(0, maoHaoWeiZhi);
            string portStr = url.Substring(maoHaoWeiZhi + 1);
            if (!int.TryParse(portStr, out int port) || port < 0 || port > 65535)
            {
                endPoint = null;
                displayUrl = "";
                return false;
            }

            if (host.StartsWith("[") && host.EndsWith("]"))
            {
                host = host.Substring(1);
                host = host.Substring(0, host.Length - 1);
                if (!IPAddress.TryParse(host, out IPAddress ipv6Address) ||
                    ipv6Address.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    endPoint = null;
                    displayUrl = "";
                    return false;
                }

                endPoint = new IPEndPoint(ipv6Address, port);
                displayUrl = host + ":" + port;
                return true;
            }

            if (IPAddress.TryParse(host, out IPAddress ipv4Address) &&
                ipv4Address.AddressFamily == AddressFamily.InterNetwork)
            {
                endPoint = new IPEndPoint(ipv4Address, port);
            }
            List<IPAddress> hostAddresses;
            try
            {
                hostAddresses = Dns.GetHostAddresses(host).ToList();
                
            }
            catch (Exception e) when (e is SocketException or ArgumentException)
            {
                endPoint = null;
                displayUrl = "";
                return false;
            }
            
            hostAddresses = hostAddresses.Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).ToList();
            
            if (!Socket.OSSupportsIPv6)
                hostAddresses = hostAddresses.Where(address => address.AddressFamily != AddressFamily.InterNetworkV6).ToList();
            hostAddresses.Sort((a, b) =>
            {
                int c = (int)a.AddressFamily;
                int d = (int)b.AddressFamily;
                return c - d;
            });
            if (hostAddresses.Count == 0)
            {
                endPoint = null;
                displayUrl = "";
                return false;
            }
            endPoint = new IPEndPoint(hostAddresses[0], port);
            displayUrl = host + ":" + port;
            return true;
        }
    }

    public class GeneralSendData
    {
        [JsonProperty("operate")] public string Operate;
        [JsonProperty("username")] public string Username;
        [JsonProperty("addition")] public Dictionary<string, object> Addition = new();
    }

    public class LoginSendData : GeneralSendData
    {
        [JsonProperty("verifyToken")] public string VerifyToken;
    }

    public class SendDataWithToken : GeneralSendData
    {
        [JsonProperty("loginToken")] public string LoginToken;
    }

    public class GeneralReceiveData
    {
        [JsonProperty("Type")] public string Type;
    }

    public class PingReceiveData : GeneralReceiveData
    {
        [JsonProperty("Status")] public bool Status;
        [JsonProperty("Name")] public string Name;
        [JsonProperty("Motd")] public string Motd;
        [JsonProperty("DebugServer")] public bool IsDebug = false;
        [JsonProperty("RequireVersion")] public int Version = -1;
        [JsonProperty("ReceiveTime")] public long ReceiveTime;
        [JsonProperty("OnlineMode")] public bool IsOnline;
        [JsonProperty("ChartUploadMode")] public bool EnableChartUpload;
    }

    public class BackReceiveData : GeneralReceiveData
    {
        [JsonProperty("Status")] public bool Status;
        [JsonProperty("msg")] [CanBeNull] public string Message;
    }

    public class ActiveReceiveData : GeneralReceiveData
    {
        [JsonProperty("operate")] public string Operate;
    }

    public class SongInfo
    {
        [JsonProperty("FolderName")] public string FolderName;
        [JsonProperty("SongName")] public string SongName;
        [JsonProperty("SongComposer")] public string SongComposer;
        [JsonProperty("SongDifficulty")] public string SongDifficulty;
        [JsonProperty("SongCharter")] public string SongCharter;
        [JsonProperty("SongIllustrator")] public string SongIllustrator;
    }

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

    public enum SongType
    {
        rep = 0,
        Phizone = 1,
        empty = 2
    }
}