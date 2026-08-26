using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using MainCore;
using MainCore.Common;
using MainCore.Data;
using MainCore.UI;
using MainCore.UI.Utils;
using MainCore.Serialized;
using MainCore.Utilities;
using Network.Chart;
using Network.Multiplayer.Data;
using Network.Multiplayer.Managers;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

public class MPServerTest : MonoBehaviour
{
    public ChatManager chatManager;

    public Button bDisconnect;
    public Button bCreateRoom, bSelectRoom, bCloseRoom, bQuitRoom, bDownloadSong, bStartGame, bUpdateSong;
    public Toggle_Button bReady;
    public Toggle isPublicOnCreateRoom;

    private static SongType selectedSongType = SongType.empty;
    private static string selectedSongId = "";
    private static SongInfo selectedSongInfo = null;
    private static bool downloaded = false;

    public Animator roomListAnimator;
    public Button bRefreshRoomList, bCloseRoomList;
    public RectTransform rtRoomList;
    public GameObject roomListItemPrefab;

    private int roomId = -1;

    public Text tConnectState;

    public Text tLoginToken, tRoomId;

    public GameObject loginObj, createRoomObj;

    private bool IsFromUnityThread => GlobalSetting.UnityThreadId == Thread.CurrentThread.ManagedThreadId;

    public GameObject sendMask, downloadMask, uploadMask;

    private RoomSummary[] roomList = null;

    private Dictionary<GameObject, RoomState> buttonToState = new();
    private RoomState displayedRoomState;

    private static string ownerLocalPath = "";
    private static string localChartDirectory = "";
    private bool ownerChartPrepared;
    private bool closeRoomPending;

    private static string[] generalErrorMessages = { "未连接服务器", "无法发送数据包", "你小子没登录" };

    private void Awake()
    {
        // ZipConstants.DefaultCodePage = 65001; // UTF-8
        buttonToState = new()
        {
            { bCreateRoom.gameObject, RoomState.NotInRoom },
            { bSelectRoom.gameObject, RoomState.NotInRoom },
            { bCloseRoom.gameObject, RoomState.RoomOwner },
            { bQuitRoom.gameObject, RoomState.RoomMember },
            { bDownloadSong.gameObject, RoomState.RoomMember | RoomState.RoomOwner },
            { bStartGame.gameObject, RoomState.RoomOwner },
            { bUpdateSong.gameObject, RoomState.RoomOwner },
            { bReady.gameObject, RoomState.RoomMember }
        };
        SocketManager.OnCloseRoomSucceeded += ChartHandler.OnRoomClosed;
        SocketManager.OnQuitRoomSucceeded += ChartHandler.OnRoomQuited;
        // SocketManager.OnCloseRoomSucceeded += chatManager.OnInitOrRoomClosed;
        // SocketManager.OnQuitRoomSucceeded += chatManager.OnInitOrRoomClosed;
        // SocketManager.OnCreateRoomSucceeded += chatManager.OnRoomJoinedOrCreated;
        // SocketManager.OnJoinRoomSucceeded += chatManager.OnRoomJoinedOrCreated;
        GlobalSetting.ReadUserSettings();
        chatManager.RevertChatHistory();
        // try
        // {
        //     InitAPI();
        // }
        // catch (ArgumentException)
        // {
        // }
    }

    // private async void InitAPI()
    // {
    //     await UniTask.SwitchToMainThread();
    //     PopupMessageManager.Instance.Message("尝试连接api服务器……");
    //     LoginManager.ReadAccountFromPlayerPrefs();
    //     bool succeeded = await RepAPI.Init();
    //     if (succeeded)
    //     {
    //         PopupMessageManager.Instance.Message("连接成功");
    //     }
    //     else
    //     {
    //         InGameUIManager.ShowModalWindowWithClose("致命错误", "无法连接至服务器\n程序即将退出", Util.QuitApp, "确定");
    //     }
    // }

    // Start is called before the first frame update
    void Start()
    {
        // Username = RepAPI.Username;
        loginObj.SetActive(true);
        createRoomObj.SetActive(false);
        tConnectState.text = "服务器状态：未连接";
        bDisconnect.onClick.AddListener(Disconnect);

        bSelectRoom.onClick.AddListener(() => { roomListAnimator.SetTrigger(Enabled); });
        bCloseRoomList.onClick.AddListener(() => { roomListAnimator.SetTrigger(Disabled); });
        bRefreshRoomList.onClick.AddListener(() =>
        {
            bRefreshRoomList.interactable = false;
            GeneralListener(SocketManager.FetchServerInfo, generalErrorMessages);
            bRefreshRoomList.interactable = true;
        });
        bCreateRoom.onClick.AddListener(CreateRoomPrepare);
        bCloseRoom.onClick.AddListener(CloseRoom);
        bQuitRoom.onClick.AddListener(() => GeneralListener(SocketManager.QuitRoom, generalErrorMessages));
        bReady.onOnLabel = "取消准备";
        bReady.onOffLabel = "准备";
        bReady.OnValueChanged += OnReadyButtonValueChanged;
        bStartGame.onClick.AddListener(() => GeneralListener(SocketManager.StartGame, generalErrorMessages));
        bUpdateSong.onClick.AddListener(() =>
        {
            async Task OnGetFileSuccess(string path)
            {
                ownerChartPrepared = false;
                SetDownloaded(false);
                ownerLocalPath = path;
                ownerChartPrepared = await ProcessChart(path);
                await UniTask.SwitchToMainThread();
                if (!ownerChartPrepared)
                {
                    ChatManager.AddMessage("Server", "所选文件夹不是有效的单个谱面目录", MessageType.Error);
                    SetDownloaded(false);
                    return;
                }

                localChartDirectory = path;
                int state = SocketManager.UpdateSong(await ChartHandler.Upload(path), SongType.rep,
                    (await GameUtils.GetSongInfo(path)).Item1);
                await UniTask.SwitchToMainThread();
                if (state == 0)
                {
                    SetDownloaded(true);
                    return;
                }
                ownerChartPrepared = false;
                SetDownloaded(false);
                if (state == -4)
                    ChatManager.AddMessage("Server", "上传时遇到未知错误", MessageType.Error);
                else
                    ChatManager.AddMessage("Server", generalErrorMessages[-state - 1], MessageType.Error);
            }

            uploadMask.SetActive(true);
            OpenFile.LoadFolder(async path =>
            {
                await OnGetFileSuccess(path);
                uploadMask.SetActive(false);
            }, () => { uploadMask.SetActive(false); }, Util.DataPath, "选择谱面...", "上传");
        });
        bDownloadSong.onClick.AddListener(Download);
        SocketManager.OnLoginSucceeded += () => { loginObj.SetActive(false); };
        SocketManager.OnCreateRoomSucceeded += () =>
        {
            SetButtonState(RoomState.RoomOwner);
            ownerChartPrepared = false;
            SetDownloaded(false);
            ChatManager.AddMessage("", $"房间已创建，房间号：{SocketManager.GetRoomId()}", MessageType.Server);
        };
        SocketManager.OnCloseRoomSucceeded += () =>
        {
            closeRoomPending = false;
            if (sendMask != null) sendMask.SetActive(false);
            SetButtonState(RoomState.NotInRoom);
            selectedSongId = "";
            selectedSongType = SongType.empty;
            selectedSongInfo = null;
            ownerChartPrepared = false;
        };
        SocketManager.OnJoinRoomSucceeded += () =>
        {
            SetButtonState(RoomState.RoomMember);
            SocketManager.FetchRoomInfo();
        };
        SocketManager.OnGetRoomInfoSucceeded += info =>
        {
            OnUpdateSongReceived(info.SelectedSongID, Enum.Parse<SongType>(info.SelectedSongType),
                info.selectedSongInfo);
        };
        SocketManager.OnQuitRoomSucceeded += () =>
        {
            SetButtonState(RoomState.NotInRoom);
            selectedSongId = "";
            selectedSongType = SongType.empty;
            selectedSongInfo = null;
            ownerChartPrepared = false;
        };
        SocketManager.OnSendPrepared += clientOperate =>
        {
            if (clientOperate is ClientOperate.Room_SendMessage or ClientOperate.User_LoginToServer
                or ClientOperate.Room_UpdateSong or ClientOperate.Room_GameStart
                or ClientOperate.User_Ready or ClientOperate.User_UnReady
                or ClientOperate.User_GameEnd or ClientOperate.Room_UserQuitGame
                or ClientOperate.Game_ScoreSync or ClientOperate.User_LeaveServer) return;
            if (sendMask != null) sendMask.SetActive(true);
        };
        SocketManager.OnBackReceived += operation =>
        {
            if (operation == ClientOperate.User_CloseRoom) closeRoomPending = false;
            if (sendMask != null) sendMask.SetActive(false);
        };
        SocketManager.OnUpdateSongReceived += OnSongReceived;
        SocketManager.OnUpdateSongSucceeded += () =>
        {
            if (!SocketManager.IsOwner || !ownerChartPrepared) return;
            localChartDirectory = ownerLocalPath;
            SetDownloaded(true);
        };
        SocketManager.OnGameStarted += playerList =>
        {
            GlobalSetting.PlayerList = playerList;
            EnterGame();
        };
        SocketManager.OnGetRoomListSucceeded += list =>
        {
            roomList = list;
            for (int i = 0; i < rtRoomList.childCount; i++)
            {
                Destroy(rtRoomList.GetChild(i).gameObject);
            }

            foreach (RoomSummary summary in roomList)
            {
                GameObject o = Instantiate(roomListItemPrefab, rtRoomList);
                o.GetComponent<RoomListItem>().Set(this, summary);
            }
        };
        sendMask.SetActive(false);
        if (SocketManager.GetToken() != "")
        {
            tConnectState.text = "服务器状态：连接成功";
            loginObj.SetActive(false);
            SetButtonState(SocketManager.GetRoomId() == "" ? RoomState.NotInRoom :
                SocketManager.IsOwner ? RoomState.RoomOwner : RoomState.RoomMember);
            SetDownloaded(downloaded && SocketManager.GetSongId() == selectedSongId &&
                          SocketManager.GetSongType() == selectedSongType);
            // if (SocketManager.GetRoomId() != "") SocketManager.GetSong();
        }
        else
        {
            loginObj.SetActive(true);
            SetButtonState(RoomState.NotInRoom);
        }

        displayedRoomState = SocketManager.GetRoomId() == ""
            ? RoomState.NotInRoom
            : SocketManager.IsOwner ? RoomState.RoomOwner : RoomState.RoomMember;
        GlobalSetting.Reset();
    }

    private void SelectRoom()
    {
        roomListAnimator.SetTrigger(Enabled);
        if (roomList == null) bRefreshRoomList.onClick.Invoke();
    }

    public void SelectRoom(int id)
    {
        if (SocketManager.GetRoomId() != "") return;
        chatManager.SetText(id.ToString());
        chatManager.Invoke();
        roomListAnimator.SetTrigger(Disabled);
    }

    private int createRoomFlag;

    private async void CreateRoomPrepare()
    {
        // () => GeneralListener(SocketManager.CreateRoom, generalErrorMessages)
        createRoomObj.SetActive(true);
        createRoomFlag = 0;
        await UniTask.WaitWhile(() => createRoomFlag == 0);
        createRoomObj.SetActive(false);
        if (createRoomFlag < 0) return;
        GeneralListener(() => SocketManager.CreateRoom(isPublicOnCreateRoom.isOn), generalErrorMessages);
    }

    public void SetCreateRoomFlag(int flag)
    {
        createRoomFlag = flag;
    }

    private void OnSongReceived(string id, SongType type, SongInfo info)
    {
        ChatManager.AddMessage("", SocketManager.IsOwner ? "您已更新曲目" : "房主更新了曲目", MessageType.Room);
        if (type == SongType.rep)
            ChatManager.AddMessage("",
                "曲目信息：\n" +
                $" - ID：{id}\n" +
                $" - 歌曲名称：{info.SongName}\n" +
                $" - 作曲家：{info.SongComposer}\n" +
                $" - 难度：{info.SongDifficulty}\n" +
                $" - 谱师：{info.SongCharter}\n" +
                $" - 曲绘绘师：{info.SongIllustrator}\n" +
                $" - [备用]文件夹名称：{info.FolderName}",
                MessageType.Room);
        OnUpdateSongReceived(id, type, JObject.FromObject(info));
        if (SocketManager.IsOwner && ownerChartPrepared)
        {
            localChartDirectory = ownerLocalPath;
            SetDownloaded(true);
        }
    }

    private async void Download()
    {
        if (selectedSongType == SongType.empty) return;
        downloadMask.SetActive(true);
        try
        {
            if (await DownloadSong())
            {
                ChatManager.AddMessage("downloadSucceeded",
                    $"成功从{selectedSongType switch { SongType.rep => "官方谱面服务器", SongType.Phizone => "PhiZone谱面服务器", SongType.empty => throw new ArgumentOutOfRangeException(), _ => throw new ArgumentOutOfRangeException() }}下载谱面{selectedSongId}",
                    MessageType.Server);
                SetDownloaded(true);
            }
            else
            {
                ChatManager.AddMessage("downloadFailed",
                    $"错误：无法从{selectedSongType switch { SongType.rep => "官方谱面服务器", SongType.Phizone => "PhiZone谱面服务器", SongType.empty => throw new ArgumentOutOfRangeException(), _ => throw new ArgumentOutOfRangeException() }}下载谱面{selectedSongId}，或谱面文件不规范",
                    MessageType.Error);
                SetDownloaded(false);
            }
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            ChatManager.AddMessage("downloadFailed",
                $"错误：无法下载谱面{selectedSongId}：{e.Message}", MessageType.Error);
            SetDownloaded(false);
        }
        finally
        {
            downloadMask.SetActive(false);
        }
    }

    private void OnReadyButtonValueChanged(bool isOn)
    {
        if (isOn)
            SocketManager.Ready();
        else
            SocketManager.Unready();
    }

    private void Disconnect()
    {
        selectedSongId = "";
        selectedSongType = SongType.empty;
        selectedSongInfo = null;
        localChartDirectory = "";
        ownerChartPrepared = false;
        closeRoomPending = false;
        if (sendMask != null) sendMask.SetActive(false);
        SocketManager.Disconnect();
        if (SceneTransit.Instance != null)
        {
            SceneTransit.Instance.JumpScene("NetworkTest", 0);
        }
        else
        {
            SocketManager.ResetForSceneChange();
            SceneManager.LoadScene("NetworkTest");
        }
    }

    private async void CloseRoom()
    {
        if (closeRoomPending) return;
        closeRoomPending = true;
        int state = SocketManager.CloseRoom();
        if (state != 0)
        {
            closeRoomPending = false;
            if (sendMask != null) sendMask.SetActive(false);
            GeneralListener(() => state, generalErrorMessages);
            return;
        }

        // Do not leave the input mask permanently active if the server drops the reply.
        await UniTask.Delay(5000);
        if (closeRoomPending)
        {
            closeRoomPending = false;
            if (sendMask != null) sendMask.SetActive(false);
            ChatManager.AddMessage("Server", "关闭房间请求超时，请检查服务器连接", MessageType.Error);
        }
    }

    public void UpdateConnectState(string str)
    {
        tConnectState.text = $"服务器状态：{str}";
    }

    private void OnUpdateSongReceived(string id, SongType type, JObject songInfo)
    {
        selectedSongId = id;
        selectedSongType = type;
        localChartDirectory = "";
        if (type == SongType.rep)
        {
            selectedSongInfo = songInfo.ToObject<SongInfo>();
        }
        else if (type == SongType.Phizone)
        {
            // TODO: 接入PhiZone
        }

        if (!SocketManager.IsOwner || !ownerChartPrepared) SetDownloaded(false);
        bReady.IsOn = false;
    }

    private void GeneralListener(Func<int> getState, params string[] errorMessages)
    {
        int state = getState.Invoke();
        if (state == 0) return;
        ChatManager.AddMessage("Server", errorMessages[-state - 1], MessageType.Error);
    }

    private PhiraChartInfoData phiraChartInfoData;
    private static readonly int Enabled = Animator.StringToHash("Enabled");
    private static readonly int Disabled = Animator.StringToHash("Disabled");

    private string GetDirectory()
    {
        string directory = $"{ChartHandler.TmpPathRoot}/decompressed_online_charts/rep/{selectedSongId}";
        Debug.Log("Directory: " + directory);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task<bool> DownloadSong() // TODO: 接入PhiZone
    {
        string directory = GetDirectory();
        localChartDirectory = directory;

        ZipUtils.UnZip(await ChartHandler.Download(selectedSongId), directory);

        // 如果出现了zip里是单个根文件夹的情况就把文件夹里的东西移出来
        string[] entries = Directory.GetFileSystemEntries(directory);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            string[] directories = Directory.GetDirectories(entries[0]);
            foreach (var qwq in directories)
            {
                Directory.Move(qwq, directory);
            }

            string[] files = Directory.GetFiles(entries[0]);
            foreach (var awa in files)
            {
                File.Move(awa, directory);
            }

            Directory.Delete(entries[0]);
        }

        return await ProcessChart(directory);
    }

    private async Task<bool> ProcessChart(string directory)
    {
        try
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                Debug.LogError("[Multiplayer] Chart directory does not exist: " + directory);
                return false;
            }

            var beatmapInfo = new BeatmapInfo { BasePath = directory };
            var parsed = await beatmapInfo.ReloadFromPathFallback(directory);
            if (parsed == null)
            {
                Debug.LogError("[Multiplayer] Failed to parse chart info from " + directory);
                return false;
            }
            beatmapInfo.BasePath = directory;
            GlobalSetting.SetBeatmap(beatmapInfo);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            return false;
        }
    }

    private string GetExistingChartDirectory()
    {
        if (!string.IsNullOrEmpty(localChartDirectory)) return localChartDirectory;
        if (SocketManager.IsOwner && !string.IsNullOrEmpty(ownerLocalPath) && Directory.Exists(ownerLocalPath))
            return ownerLocalPath;
        if (selectedSongType != SongType.rep || string.IsNullOrEmpty(selectedSongId)) return "";

        return Path.Combine(ChartHandler.TmpPathRoot, "decompressed_online_charts", "rep", selectedSongId);
    }

    private async Task<bool> ProcessExistingChart()
    {
        string directory = GetExistingChartDirectory();
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Directory.Exists(directory) && await ProcessChart(directory)) return true;
            await Task.Delay(250);
        }

        Debug.LogError("[Multiplayer] Timed out waiting for chart files: " + directory);
        return false;
    }

    private async void EnterGame()
    {
        string directory = GetExistingChartDirectory();
        if (string.IsNullOrEmpty(directory))
        {
            Debug.LogError("[Multiplayer] Chart files are not available: " + directory);
            ChatManager.AddMessage("downloadFailed", "错误：开始游戏前找不到已下载谱面", MessageType.Error);
            return;
        }

        // The owner can receive GameStarted before its background copy finishes.
        // Reparse the existing directory here so the game never starts with the default BeatmapInfo.
        if (!await ProcessExistingChart())
        {
            ChatManager.AddMessage("downloadFailed", "错误：开始游戏前无法读取谱面信息", MessageType.Error);
            return;
        }

        GlobalSetting.IsMultiplayer = true;
        GlobalSetting.YayaKawaii = GlobalSetting.YayaMode.冲;
        GlobalSetting.PepoyoDaisuki = GlobalSetting.PepoyoMode.Waraninja;
        PopupMessageManager.Instance.Clear();

        try
        {
            await UniTask.SwitchToMainThread();
            await GlobalSetting.CurrentBeatmapInfo.LoadIllustration();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            ChatManager.AddMessage("downloadFailed", "错误：开始游戏前无法加载曲绘", MessageType.Error);
            return;
        }

        var success = await GlobalSetting.CurrentBeatmapInfo.LoadBeatmap();
        if (!success)
        {
            Debug.LogError("[Multiplayer] Failed to load beatmap");
            ChatManager.AddMessage("downloadFailed", "错误：开始游戏前无法加载谱面", MessageType.Error);
            return;
        }
        
        GlobalSetting.IsMultiplayer = true;
        GlobalSetting.ReadUserSettings();
        GlobalSetting.AutoPlay = false;
        GlobalSetting.StrictJudgeMode = false;
        GlobalSetting.NewScoreCalcType = false;
        GlobalSetting.Pitch = 1.0f;
        HitSoundManager.UpdateVolume();
        
        if (SceneTransit.Instance != null)
        {
            SceneTransit.Instance.LoadScene("PlayingScene");
        }
        else
        {
            SceneManager.LoadScene("PlayingScene");
        }
    }

    // private int counter = 0;

    private void Update()
    {
        RoomState roomState = SocketManager.GetRoomId() == ""
            ? RoomState.NotInRoom
            : SocketManager.IsOwner ? RoomState.RoomOwner : RoomState.RoomMember;
        if (roomState != displayedRoomState)
        {
            displayedRoomState = roomState;
            SetButtonState(roomState);
        }

        bStartGame.interactable = SocketManager.CanStartGame;

        string token = SocketManager.GetToken();
        string roomId = SocketManager.GetRoomId();
        tLoginToken.text = "Login Token: " + (string.IsNullOrEmpty(token) ? "未登录" : token);
        tRoomId.text = "Room Id: " + (string.IsNullOrEmpty(roomId) ? "无" : roomId);
        // if (!Input.GetMouseButtonDown(2)) return;
        // switch (counter % 2)
        // {
        //     case 0:
        //         Debug.Log("开始压缩");
        //         DateTime dateTime = DateTime.Now;
        //         ZipUtils.ZipDirectory("G:/RPGR-Data/でんでん心電図", "G:/RPGR-Data", "test");
        //         DateTime dateTime1 = DateTime.Now;
        //         Debug.Log("压缩完了,用时：" + (dateTime1 - dateTime).TotalMilliseconds + "ms");
        //         break;
        //     case 1:
        //         Debug.Log("开始解压");
        //         dateTime = DateTime.Now;
        //         ZipUtils.UnZip("G:/RPGR-Data/test.zip", "G:/RPGR-Data/test");
        //         dateTime1 = DateTime.Now;
        //         Debug.Log("解压完了,用时：" + (dateTime1 - dateTime).TotalMilliseconds + "ms");
        //         break;
        // }
        // counter++;
    }

    private void SetButtonState(RoomState state)
    {
        if (!IsFromUnityThread || Convert.ToString((int)state, 2).Replace("0", "").Length > 1) return; // 仅输入单个RoomState
        foreach (GameObject button in buttonToState.Keys)
        {
            button.SetActive((buttonToState[button] & state) == state);
        }

        if (state == RoomState.NotInRoom)
        {
            chatManager.OnInitOrRoomClosed();
            SetDownloaded(false);
        }
        else
        {
            chatManager.OnRoomJoinedOrCreated();
        }
    }

    private void SetDownloaded(bool value)
    {
        if (!IsFromUnityThread)
        {
            throw new ArgumentException("Not from Unity thread");
        }

        downloaded = value;
        bDownloadSong.interactable = !value && selectedSongType != SongType.empty;
        bReady.Interactable = true;
        bStartGame.interactable = SocketManager.CanStartGame;
    }

    public void Back()
    {
        if (SceneTransit.Instance != null)
        {
            SceneTransit.Instance.Back();
        }
        else
        {
            SceneManager.LoadScene("MainScene");
        }
    }

    /// <summary>
    /// 复制文件夹及文件
    /// </summary>
    /// <param name="sourceFolder">原文件路径</param>
    /// <param name="destFolder">目标文件路径</param>
    /// <returns></returns>
    public bool CopyFolder(string sourceFolder, string destFolder)
    {
        try
        {
            if (!Directory.Exists(sourceFolder)) return false;
            //如果目标路径不存在,则创建目标路径
            if (!Directory.Exists(destFolder))
            {
                Directory.CreateDirectory(destFolder);
            }

            //得到原文件根目录下的所有文件
            string[] files = Directory.GetFiles(sourceFolder);
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                string dest = Path.Combine(destFolder, name);
                File.Copy(file, dest); //复制文件
            }

            //得到原文件根目录下的所有文件夹
            string[] folders = Directory.GetDirectories(sourceFolder);
            foreach (string folder in folders)
            {
                string name = Path.GetFileName(folder);
                string dest = Path.Combine(destFolder, name);
                CopyFolder(folder, dest); //构建目标路径,递归复制文件
            }

            return true;
        }
        catch (Exception e)
        {
            e.Print();
            return false;
        }
    }
}

[Flags]
public enum RoomState
{
    NotInRoom = 1 << 0,
    RoomOwner = 1 << 1,
    RoomMember = 1 << 2
}
