using DG.Tweening;
using UnityEngine;

namespace MainCore.Common
{
    /// <summary>
    /// 跨场景的全局 BGM。
    ///
    /// 旧实现在 EntryManager / MainManager 里各挂一个 AudioSource，
    /// 跳场景时 FadeOutBgm() 会把音乐停掉，所以 MainScene 必须重新播一遍、
    /// 听感上音乐从头开始。这个管理器自己建一个 DontDestroyOnLoad 对象，
    /// AudioSource 随对象跨场景存活，因此：
    ///   - EntryScene 起播，之后一路循环
    ///   - 进入 MainScene 不重播，直接继续（可用 PlaybackTime 验证进度持续增长）
    ///   - 离开 MainScene 再回来，音乐仍在原位置继续
    ///
    /// 不挂在场景里的 [Managers] 对象上，因为那个对象只有 EntryScene 有，
    /// MainScene 没有，挂上去会导致切场景后 Instance 为 null。
    /// </summary>
    public class BgmManager : MonoSingleton<BgmManager>
    {
        /// <summary>Resources 下的音频路径（不含扩展名）。</summary>
        private const string ClipPath = "Audio/BGM_IronEcho";

        /// <summary>实例宿主对象名，便于在 Hierarchy 里辨认。</summary>
        private const string HostName = "[BGM]";

        /// <summary>
        /// MainScene 相对 EntryScene 降低的音量（0~1 比例）。
        /// 设置页关闭时恢复 BGM 应该回到这个档，而不是 1.0，
        /// 否则每次关掉设置音量都会突然变响。
        /// </summary>
        public const float MainSceneVolume = 0.45f;

        private const float FadeInTime = 1.2f;

        private AudioSource source;
        private AudioClip clip;

        /// <summary>当前目标音量。设置页调音量时也走这里。</summary>
        private float volume = 1f;

        /// <summary>当前场景应有的音量档。设置页关闭时恢复到它。</summary>
        private float sceneVolume = 1f;

        /// <summary>是否已经起播过。跨场景时用于判断是"继续"还是"重新开始"。</summary>
        public bool Playing { get; private set; }

        /// <summary>
        /// 全局访问点。首次调用时自动创建宿主对象，
        /// 因此不依赖任何场景预挂组件。
        /// </summary>
        public static BgmManager Instance
        {
            get
            {
                if (instance != null) return instance;

                // Unity 的 Destroy 是延迟的，同一帧内连续访问会拿到已排队销毁的旧实例
                if (quitting) return null;

                var host = new GameObject(HostName);
                instance = host.AddComponent<BgmManager>();
                return instance;
            }
        }

        private static BgmManager instance;
        private static bool quitting;

        /// <summary>
        /// 当前播放进度（秒）。用于验证"是否真的在继续播"。
        /// 刻意不叫 Time —— 那会遮蔽 UnityEngine.Time，类内以后写 Time.deltaTime 会踩坑。
        /// </summary>
        public float PlaybackTime => source != null && source.isPlaying ? source.time : 0f;

        /// <summary>
        /// 基类 MonoSingleton 已有 public Awake()，这里必须重写 OnAwake()。
        /// 同时把宿主对象设为 DontDestroyOnLoad，否则跨场景时整个管理器会被销毁。
        /// </summary>
        protected override void OnAwake()
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private void OnApplicationQuit()
        {
            quitting = true;
        }

        /// <summary>
        /// 起播全局 BGM。重复调用是安全的：已经在播就直接返回，
        /// 这样 EntryScene 起播后，MainScene 再调一次不会打断音乐。
        /// </summary>
        public void Play()
        {
            if (Playing && source != null && source.isPlaying) return;

            if (clip == null) clip = Resources.Load<AudioClip>(ClipPath);
            if (clip == null)
            {
                Debug.LogWarning($"[BgmManager] 找不到 BGM 资源：Resources/{ClipPath}");
                return;
            }

            if (source == null) source = gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.volume = 0f;
            source.Play();

            Playing = true;
            FadeTo(volume, FadeInTime, Ease.InQuad);
        }

        /// <summary>
        /// 只调整音量，不重新起播。MainScene 用它把音量压低。
        /// </summary>
        /// <param name="v">目标音量，0~1。</param>
        /// <param name="fadeTime">淡变时长，小于等于 0 表示立即生效。</param>
        public void SetVolume(float v, float fadeTime = 0.3f)
        {
            volume = Mathf.Clamp01(v);
            if (source == null || !source.isPlaying)
            {
                // 还没起播时只记下目标音量，等 Play() 时会用上
                return;
            }

            FadeTo(volume, fadeTime, Ease.OutQuad);
        }

        /// <summary>
        /// 进入某个场景时设置音量档。MainScene 调低音量用的就是它。
        /// </summary>
        /// <param name="v">该场景的目标音量，0~1。</param>
        /// <param name="fadeTime">淡变时长。</param>
        public void SetSceneVolume(float v, float fadeTime = 0.8f)
        {
            sceneVolume = Mathf.Clamp01(v);
            SetVolume(sceneVolume, fadeTime);
        }

        /// <summary>
        /// 恢复到当前场景应有的音量档（EntryScene 满音量，MainScene 0.45）。
        /// 设置页打开时静音、关闭时用它恢复，而不是直接给 1.0。
        /// </summary>
        public void RestoreSceneVolume(float fadeTime = 0.3f)
        {
            SetVolume(sceneVolume, fadeTime);
        }

        /// <summary>停止 BGM 并重置状态（下次 Play() 会从头开始）。</summary>
        public void Stop()
        {
            if (source == null) return;
            source.DOKill();
            source.Stop();
            Playing = false;
        }

        private void FadeTo(float target, float time, Ease ease)
        {
            source.DOKill();
            if (time <= 0f)
            {
                source.volume = target;
                return;
            }

            source.DOFade(target, time).SetEase(ease);
        }
    }
}
