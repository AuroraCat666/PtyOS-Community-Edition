using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using JetBrains.Annotations;
using MainCore.Common;
using MainCore.Settings;
using MainCore.UI.Utils;
using MainCore.Utilities;
using Network;
using Network.Account;
using Network.Account.Utils;
using Network.Multiplayer.Managers;
using UnityEngine;
using UnityEngine.Video;

namespace MainCore.UI
{
    public class EntryManager : MonoBehaviour
    {
        // [SerializeField] private Button touchToStart;
        // [SerializeField] private Text touchToStartText;
        [SerializeField] private GameObject debugText;
        [SerializeField, UsedImplicitly] private GameObject inGameDebugConsolePrefab;
        [SerializeField] private VideoPlayer splashPlayer;
        [SerializeField] private GameObject splashCanvas, splashBgCanvas;

        private bool _splashPlayed = false;
        private bool _loaded = false;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        private void Awake()
        {
            GlobalSetting.OriginResolution = Screen.currentResolution;
            GlobalSetting.UnityThreadId = Thread.CurrentThread.ManagedThreadId;
#if !RELEASE_VERSION && !UNITY_EDITOR
            debugText.SetActive(true);
            Instantiate(inGameDebugConsolePrefab).GetComponent<IngameDebugConsole.DebugLogManager>().enableCommand = false;
#else
            debugText.SetActive(false);
#endif
            GlobalSetting.ReadUserSettings();
            SceneTransit.OnSceneClosing.AddListener(HitEffectManager.GetInstance().Reset);
            SocketManager.Init();
            UniTask.Void(async () =>
            {
                await new WaitForSeconds(0.01f);
                Resources.Load<Sprite>("1920x1080_Black");
            });
#if UNITY_ANDROID && !UNITY_EDITOR
            UniTask.Void(async () =>
            {
                await UniTask.Delay(1500);
                CheckExternalStoragePermission();
            });
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void CheckExternalStoragePermission()
        {
            try
            {
                using (var buildVersion = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    int sdkInt = buildVersion.GetStatic<int>("SDK_INT");
                    if (sdkInt < 30) return;
                }

                using (var environment = new AndroidJavaClass("android.os.Environment"))
                {
                    if (environment.CallStatic<bool>("isExternalStorageManager")) return;
                }

                InGameUIManager.ShowModalWindowWithClose("权限",
                    "需要开启「所有文件访问」权限才能读取谱面目录\n请点击确定，并在系统设置中允许该权限", () =>
                    {
                        try
                        {
                            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                            using (var uriClass = new AndroidJavaClass("android.net.Uri"))
                            {
                                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                                var intent = new AndroidJavaObject("android.content.Intent",
                                    "android.settings.MANAGE_APP_ALL_FILES_ACCESS_PERMISSION");
                                var uri = uriClass.CallStatic<AndroidJavaObject>("fromParts", "package",
                                    Application.identifier, null);
                                intent.Call<AndroidJavaObject>("setData", uri);
                                activity.Call("startActivity", intent);
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                        }
                    }, "确定");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
#endif

        private void Start()
        {
            PlayBgm();
#if !UNITY_EDITOR
            PlaySplash();
#else
            _splashPlayed = true;
#endif
        }

        private void PlayBgm()
        {
            // 交给全局 BgmManager 起播。它挂在 DontDestroyOnLoad 对象上，
            // 进入 MainScene 后音乐继续循环，不会重新从头播。
            BgmManager.Instance.Play();
        }

        private void FadeOutBgm()
        {
            // 不再在这里停音乐 —— 切到 MainScene 应当继续播放。
            // 音量改由 MainManager 调低。
        }

        private void PlaySplash()
        {
            splashCanvas.SetActive(true);
            splashBgCanvas.SetActive(true);
            splashPlayer.time = 0f;
            splashPlayer.Play();
            splashPlayer.started += async delegate 
            { 
                await UniTask.Delay(3000);
                splashCanvas.SetActive(false);
                splashBgCanvas.GetComponent<CanvasGroup>().DOFade(0, .5f).OnComplete(delegate
                {
                    _splashPlayed = true;
                });
            };
        }
        
        private void Update()
        {
            if (!_splashPlayed || _loaded) return;
            if (Input.GetMouseButtonUp(0))
            {
                PlayClickSound();
                _loaded = true;
                LoadIn();
            }
        }

        private AudioClip clickSound;

        private void PlayClickSound()
        {
            if (clickSound == null)
            {
                clickSound = Resources.Load<AudioClip>("Audio/dragon-studio-button-press-382713");
            }
            if (clickSound != null)
            {
                AudioSource.PlayClipAtPoint(clickSound, Vector3.zero, 1f);
            }
        }

        private async void LoadIn()
        {
            await UniTask.WaitUntil(() => SkinManager.Instance.Initialized);
            Application.targetFrameRate = 120;
            GameUtils.ResetDSPBuffer(PlayerPrefs.GetInt("dsp_pow", 8));
            if (!File.Exists(Path.Combine(Application.persistentDataPath, "IOS PlaceHolder")))
            {
                var t = File.Create(Path.Combine(Application.persistentDataPath, "IOS PlaceHolder"));
                await t.DisposeAsync();
                await File.WriteAllTextAsync(Path.Combine(Application.persistentDataPath, "IOS PlaceHolder"),
                    "Just a simple placeholder");
            }

            await Connect();

            if (!PlayerPrefs.HasKey("announcement_shown"))
            {
                PlayerPrefs.SetInt("announcement_shown", 1);
                PlayerPrefs.Save();
                InGameUIManager.ShowModalWindowWithClose("公告",
                    "欢迎来到 PtyOS 社区版本！\n\n本版本为社区爱好者搭建！不得以任何形式转卖！\n如果你发现你是从付费渠道获得的模拟器请你立即退款并举报！\n\n本版本作者：云辞树 QQ3053860096\n请尊重原作者Kagari 939，谢谢",
                    () =>
                    {
                        FadeOutBgm();
                        SceneTransit.Instance.JumpScene("MainScene", 0);
                    }, "确定");
            }
            else
            {
                FadeOutBgm();
                SceneTransit.Instance.JumpScene("MainScene", 0);
            }
        }


        private async UniTask Connect()
        {
            await UniTask.SwitchToMainThread();
            PopupMessageManager.Instance.Message("尝试连接服务器...");
            await UniTask.SwitchToThreadPool();
            if (!await RepAPI.Init())
            {
                await UniTask.SwitchToMainThread();
                // 社区版：连接失败不阻塞进入游戏，可离线游玩
                PopupMessageManager.Instance.ChangeContent("离线模式：未连接服务器");
                return;
            }

            await UniTask.SwitchToMainThread();
            if (AccountManager.GetLastUser() == "")
            {
                PopupMessageManager.Instance.ChangeContent("连接成功");
                return;
            }

            PopupMessageManager.Instance.ChangeContent("尝试登录...");
            List<AccountManager.AccountInfo> accountInfos = AccountManager.GetAccountList();
            int a = -1;
            AccountManager.AccountInfo info = null;
            for (var i = 0; i < accountInfos.Count; i++)
            {
                if (accountInfos[i].Username != AccountManager.GetLastUser()) continue;
                a = i;
                info = accountInfos[i];
                break;
            }

            if (info == null) throw new ArgumentException();
            await UniTask.SwitchToThreadPool();
            (StatusCode code, string token) = await LoginManager.Verify(info);
            await UniTask.SwitchToMainThread();
            bool completed = false;
            switch (code)
            {
                case StatusCode.Unknown:
                    InGameUIManager.ShowModalWindowWithClose("致命错误", "收到了未定义的状态码，请联系开发者\n程序即将退出", Util.QuitApp, "确定");
                    break;
                case StatusCode.OK:
                    accountInfos.Remove(info);
                    info.VerifyToken = token;
                    accountInfos.Insert(0, info);
                    AccountManager.SaveAccountList(accountInfos, info.Username);
                    Finally(info);
                    PopupMessageManager.Instance.ChangeContent("登录成功");
                    completed = true;
                    break;
                case StatusCode.InvalidParam:
                    // 社区版：本地服务器对空 token 返回 InvalidParam，视作失效账号，直接清除登录状态
                    accountInfos.Remove(info);
                    AccountManager.SaveAccountList(accountInfos);
                    AccountManager.ClearLoginState();
                    GlobalSetting.Username = "";
                    GlobalSetting.VerifyToken = "";
                    PopupMessageManager.Instance.ChangeContent("登录已失效，请重新登录");
                    completed = true;
                    break;
                case StatusCode.ServerInternalError:
                    InGameUIManager.ShowModalWindowWithClose("致命错误", "服务器出错，请联系开发者\n程序即将退出", Util.QuitApp, "确定");
                    break;
                case StatusCode.IllegalLogin:
                    InGameUIManager.ShowModalWindowWithClose("错误", "登录次数已用完", () => completed = true, "确定");
                    break;
                case StatusCode.InvalidUsername:
                    InGameUIManager.ShowModalWindowWithClose("错误", "用户名不合法（但是已经登录过了为啥报这个）", () => completed = true,
                        "确定");
                    break;
                case StatusCode.InvalidToken:
                    accountInfos.Remove(info);
                    AccountManager.SaveAccountList(accountInfos);
                    AccountManager.ClearLoginState();
                    GlobalSetting.Username = "";
                    GlobalSetting.VerifyToken = "";
                    PopupMessageManager.Instance.ChangeContent("登录已失效，请重新登录");
                    completed = true;
                    break;
                case StatusCode.NoPermission:
                    InGameUIManager.ShowModalWindowWithClose("错误", "没有内测权限", () => completed = true, "确定");
                    break;
                case StatusCode.InvalidPassword:
                    InGameUIManager.ShowModalWindowWithClose("致命错误", "调用验证接口时收到了不应出现的状态码：密码不合法\n程序即将退出", Util.QuitApp,
                        "确定");
                    break;
                case StatusCode.UserBanned:
                    InGameUIManager.ShowModalWindowWithClose("悲报", "您已被封禁", () => completed = true, "确定");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            await UniTask.WaitUntil(() => completed);
        }

        private void Finally(AccountManager.AccountInfo info)
        {
            GlobalSetting.Username = info.Username;
            GlobalSetting.VerifyToken = info.VerifyToken;
        }
    }
}