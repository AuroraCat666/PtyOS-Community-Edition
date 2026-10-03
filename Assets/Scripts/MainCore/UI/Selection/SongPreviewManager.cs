using System;
using System.IO;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using MainCore.Utilities;
using UnityEngine;

namespace MainCore.UI.Selection
{
    /// <summary>
    /// 谱面列表的曲目试听：选中谱面后播放该曲目前 <see cref="PreviewSeconds"/> 秒，
    /// 循环往复，并带淡入淡出。切换选中谱面或离开列表时立即停掉。
    ///
    /// 放在 DontDestroyOnLoad 对象上：SelectionScene 里的管理器是
    /// MonoSingleton 但没有 DDOL，切场景时会被销毁，自己建对象更可靠。
    /// </summary>
    public class SongPreviewManager : MonoBehaviour
    {
        /// <summary>每次试听的长度（秒）。</summary>
        public const float PreviewSeconds = 15f;

        /// <summary>片段起止的淡入淡出时长（秒）。</summary>
        private const float FadeTime = 0.6f;

        private const float Volume = 0.75f;

        private AudioSource source;
        private AudioClip clip;

        /// <summary>当前预览的谱面路径，用于去重：同一首重复点不重新加载。</summary>
        private string currentPath;

        /// <summary>切歌/停止的版本号。异步加载完成回来时用它判断是否已被取消。</summary>
        private int version;

        /// <summary>是否正处于「淡出并准备重播」的阶段，防止每帧重复触发。</summary>
        private bool _recycling;

        private static SongPreviewManager instance;

        public static SongPreviewManager Instance
        {
            get
            {
                if (instance != null) return instance;
                if (quitting) return null;

                var host = new GameObject("[SongPreview]");
                instance = host.AddComponent<SongPreviewManager>();
                DontDestroyOnLoad(host);
                return instance;
            }
        }

        private static bool quitting;

        private void Awake()
        {
            if (instance == null) instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private void OnApplicationQuit() => quitting = true;

        private void Update()
        {
            if (source == null || clip == null) return;

            if (!source.isPlaying)
            {
                // 曲子比 15 秒还短的话，播完就自然停了，这里拉回来重播。
                // 正常情况不会走到这：片段末尾会先淡出再续上。
                if (clip.length < PreviewSeconds) RestartPreview();
                return;
            }

            // 到片段末尾就淡出，准备下一轮
            if (source.time >= PreviewSeconds - FadeTime)
            {
                FadeOutAndRecycle();
            }
        }

        /// <summary>
        /// 预览指定谱面的曲目。重复预览同一首会立即停下当前播放并从头开始，
        /// 切换到别的谱面则重新加载音频。
        /// </summary>
        /// <param name="basePath">谱面目录（BeatmapInfo.BasePath）。</param>
        /// <param name="musicPath">音频文件名（BeatmapInfo.MusicPath）。</param>
        public void Play(string basePath, string musicPath)
        {
            if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(musicPath)) return;

            var full = Path.Combine(basePath, musicPath);
            if (!File.Exists(full)) return;

            // 同一首：不用重新解码，直接从头循环
            if (full == currentPath && source != null && clip != null)
            {
                RestartPreview();
                return;
            }

            Stop();
            currentPath = full;
            LoadAndPlay(full).Forget();
        }

        private async UniTaskVoid LoadAndPlay(string full)
        {
            var myVersion = ++version;

            AudioClip loaded;
            try
            {
                loaded = await Util.ReadMusicAsAudioClipAsync(full);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SongPreview] 试听音频加载失败：{full}\n{e.Message}");
                return;
            }

            // 加载期间用户可能已经切到别的谱面，或离开了列表
            if (myVersion != version) return;
            if (loaded == null)
            {
                Debug.LogWarning($"[SongPreview] 不支持的音频格式：{full}");
                return;
            }

            if (source == null) source = gameObject.AddComponent<AudioSource>();
            source.clip = loaded;
            source.loop = false; // 循环由代码控制，才能在片段末尾淡出
            source.playOnAwake = false;
            source.volume = 0f;
            clip = loaded;

            source.Play();
            FadeIn();
        }

        /// <summary>立即停止试听并释放音频引用。</summary>
        public void Stop()
        {
            version++;
            _recycling = false;
            if (source != null)
            {
                source.DOKill();
                source.Stop();
                source.clip = null;
            }

            // 只清引用不 Destroy(clip)：这个 clip 由 UnityWebRequest 创建，
            // 销毁与否都能被 GC 回收，而 Destroy 的时机不受控，
            // 万一还有 AudioSource 正在引用它会出问题。
            clip = null;
            currentPath = null;
        }

        private void RestartPreview()
        {
            if (source == null) return;
            _recycling = false;
            source.DOKill();
            source.time = 0f;
            source.volume = 0f;
            source.Play();
            FadeIn();
        }

        private void FadeIn()
        {
            source.DOKill();
            source.DOFade(Volume, FadeTime).SetEase(Ease.InSine);
        }

        private void FadeOutAndRecycle()
        {
            // 淡出期间 Update 每帧都会满足 time >= 阈值，用标志位防止重复触发
            // 导致连续多次 source.time = 0（听起来像卡带）
            if (_recycling) return;
            _recycling = true;

            source.DOKill();
            source.DOFade(0f, FadeTime).SetEase(Ease.OutSine)
                .OnComplete(() =>
                {
                    _recycling = false;
                    // 播放期间用户可能已切歌/离开，这里要重新确认状态
                    if (source == null || clip == null) return;
                    source.time = 0f;
                    source.Play();
                    source.DOFade(Volume, FadeTime).SetEase(Ease.InSine);
                });
        }
    }
}
