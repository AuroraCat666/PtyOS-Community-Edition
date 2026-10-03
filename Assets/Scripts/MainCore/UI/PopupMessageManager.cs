using System.Collections.Generic;
using System.Threading;
using MainCore.Common;
using UnityEngine;
using UnityEngine.UI;

namespace MainCore.UI
{
    /// <summary>
    /// 屏幕提示条：灰色平行四边形背景 + 白色文字（文字不加描边/阴影）。
    ///
    /// 动画：出现时背景条以中心为轴从 0 横向展开到 1，文字同步淡入；
    ///       消失时反向收回。展开与收起都是 ease-out 曲线（前快后慢）。
    ///
    /// 并发：不支持堆叠。调用 Message 时若已有提示在显示，**新提示直接顶掉旧的**
    ///       （旧的立即收起）。旧实现用 `if (duration != 0) return;` 拦掉了第二条，
    ///       导致 "尝试连接服务器…" 之后紧跟的消息会把前一条静默吞掉。
    ///
    /// 线程：Message/ChangeContent 可能从非 Unity 主线程调用（见 EntryManager.Connect），
    ///       因此文本统一进队列，在 Update 里应用，避免跨线程操作 Unity 对象。
    ///
    /// 尺寸：背景条宽度跟随文字宽度自适应（左右各留 TextPaddingX 内边距），
    ///       高度由场景配置决定，并把字号反推为高度的 ~1/1.78，
    ///       保证 "条高与文字协调" 这个关系在任意高度下都成立。
    /// </summary>
    public class PopupMessageManager : MonoSingleton<PopupMessageManager>
    {
        [SerializeField] private Image background;
        [SerializeField] private Text content;

        /// <summary>文字左右各留的内边距，避免贴着斜边。</summary>
        private const float TextPaddingX = 34f;

        private const float ShowDuration = 3f;

        /// <summary>展开时长。放慢到 0.45s，让展开过程看得清。</summary>
        private const float FadeInTime = 0.45f;

        /// <summary>自动消失时的收回时长。</summary>
        private const float FadeOutTime = 0.34f;

        /// <summary>
        /// 替换时的收回时长：旧条先合上，再用新内容重新展开。
        /// 这段等待计入总时长，所以新提示不会"立刻"出现，
        /// 而是有清晰的「旧消息退场 → 新消息入场」两段节奏。
        /// </summary>
        private const float SwapOutTime = 0.32f;

        /// <summary>条高 / 字号 的比例，18px 文字对应 32px 条高 ≈ 1.78。</summary>
        private const float HeightPerFontSize = 1.78f;

        /// <summary>展开回弹的过冲量，0.1 约等于超出 10%。</summary>
        private const float Overshoot = 0.1f;

        private static float EaseOut(float t)
        {
            var inv = 1f - t;
            return 1f - inv * inv * inv;
        }

        /// <summary>
        /// 带回弹的展开曲线：先冲过 100% 再回落。
        /// 纯 ease-out 在慢速下前段变化太小，看起来像"突然出现"，
        /// 加一点过冲让起步和收尾都有明确的运动感。
        /// </summary>
        private static float EaseOutBack(float t)
        {
            var c1 = 1.70158f + Overshoot;
            var c3 = c1 + 1f;
            var inv = t - 1f;
            return 1f + c3 * inv * inv * inv + c1 * inv * inv;
        }

        private readonly Queue<string> pending = new();

        private float timer;
        private bool shown;

        /// <summary>
        /// 展开动画的**线性**时间进度（0→1）。
        /// 必须和显示值分开存：以前 SetProgress 会把缓动后的值写回 progress，
        /// 下一帧又拿它当起点继续累加，导致动画实际远快于设定时长。
        /// </summary>
        private float showTime;

        private float fadeOutTimeLeft;
        private bool fadingOut;

        private bool OnUnityThread => Thread.CurrentThread.ManagedThreadId == GlobalSetting.UnityThreadId;

        /// <summary>
        /// 初始完全收起。基类 MonoSingleton 已有 public Awake()，
        /// 这里必须重写 OnAwake() 而不是再定义一个 Awake()，
        /// 否则会隐藏基类方法、导致 Instance 初始化不执行。
        /// </summary>
        protected override void OnAwake()
        {
            FitToText();
            SetProgress(0f);
            shown = false;
        }

        private void Update()
        {
            // 主线程统一消费非主线程投递过来的文本
            while (pending.Count > 0)
            {
                var message = pending.Dequeue();
                content.text = message;
                FitToText();
            }

            if (fadingOut)
            {
                var span = SwapMode ? SwapOutTime : FadeOutTime;
                fadeOutTimeLeft -= Time.deltaTime;
                if (fadeOutTimeLeft <= 0f)
                {
                    fadingOut = false;
                    SwapMode = false;
                    shown = false;
                    SetProgress(0f);

                    if (hasPendingShow)
                    {
                        // 旧条已完全合上，现在展开新内容
                        hasPendingShow = false;
                        shown = true;
                        timer = ShowDuration;
                        showTime = 0f;
                        SetProgress(0f);
                    }
                }
                else
                {
                    var t = 1f - fadeOutTimeLeft / span;
                    SetProgress(1f - EaseOut(t));
                }

                return;
            }

            if (!shown)
            {
                return;
            }

            if (showTime < 1f)
            {
                showTime = Mathf.Min(1f, showTime + Time.deltaTime / FadeInTime);
                SetProgress(EaseOutBack(showTime));
            }

            timer -= Time.deltaTime;
            if (timer <= 0f)
            {
                fadingOut = true;
                fadeOutTimeLeft = FadeOutTime;
            }
        }

        /// <summary>当前这次收起是为了「换内容」而非「彻底消失」。</summary>
        private bool SwapMode;

        /// <summary>收起完成后是否紧接着展开新内容。</summary>
        private bool hasPendingShow;

        /// <summary>
        /// 显示一条提示。已有提示在显示时，新提示会顶掉旧的 ——
        /// 旧条先横向合上，再展开新内容，两段动画不重叠。
        /// </summary>
        /// <param name="message">要显示的消息。</param>
        public void Message(string message)
        {
            PushText(message);

            if (!OnUnityThread)
            {
                // 非主线程无法启动计时，只入队即可。
                // 主线程在 Update 里消费队列；此时 shown 仍为 false，
                // 提示条会保持收起 —— 这与旧实现的行为一致（本来就只在主线程播放动画）。
                return;
            }

            if (shown || fadingOut)
            {
                // 旧条正在显示（或正在收起）：先合上，结束后再展开新的
                SwapMode = true;
                hasPendingShow = true;
                fadingOut = true;
                fadeOutTimeLeft = SwapOutTime;
                return;
            }

            BeginShow();
        }

        /// <summary>
        /// 立刻替换当前提示的文字，不重播出现动画、不重置计时。
        /// 用于 "尝试连接服务器…" -> "连接成功" 这类同位置的进度更新。
        /// </summary>
        /// <param name="message">要显示的消息。</param>
        public void ChangeContent(string message)
        {
            PushText(message);
            if (OnUnityThread && !shown)
            {
                // 之前没在显示，直接当作新提示显示
                BeginShow();
            }
        }

        /// <summary>
        /// 收起提示条。
        /// </summary>
        public void Clear()
        {
            PushText("");
            if (OnUnityThread)
            {
                // Clear 是"彻底不要了"，必须清掉待展开标记，
                // 否则之前 Message 排队的展开会在合上后莫名重新出现。
                hasPendingShow = false;
                SwapMode = false;
                fadingOut = shown;
                fadeOutTimeLeft = FadeOutTime;
            }
        }

        private void PushText(string message)
        {
            if (OnUnityThread)
            {
                content.text = message;
                FitToText();
            }
            else
            {
                pending.Enqueue(message);
            }
        }

        private void BeginShow()
        {
            FitToText();
            shown = true;
            fadingOut = false;
            SwapMode = false;
            hasPendingShow = false;
            fadeOutTimeLeft = 0f;
            timer = ShowDuration;
            // 从 0 重新展开（ease-out-back 由 Update 逐帧推进）
            showTime = 0f;
            SetProgress(0f);
        }

        /// <summary>
        /// 让背景条宽度跟随文字宽度，高度沿用场景配置，
        /// 并按高度反推字号，使条高与文字始终保持协调比例。
        /// </summary>
        private void FitToText()
        {
            if (background == null)
            {
                return;
            }

            var bar = background.rectTransform;
            var height = bar.sizeDelta.y;
            if (height <= 0f)
            {
                height = 32f;
            }

            if (content == null)
            {
                bar.sizeDelta = new Vector2(bar.sizeDelta.x, height);
                return;
            }

            var fontSize = Mathf.Max(1, Mathf.RoundToInt(height / HeightPerFontSize));
            if (content.fontSize != fontSize)
            {
                content.fontSize = fontSize;
            }

            // preferredWidth 依赖字体与字号，必须在设置 fontSize 之后再取
            var textWidth = content.preferredWidth;
            bar.sizeDelta = new Vector2(textWidth + TextPaddingX * 2f, height);

            var text = content.rectTransform;
            text.anchoredPosition = Vector2.zero;
            text.sizeDelta = new Vector2(textWidth, height);
        }

        /// <summary>
        /// 同步展开比例。用 localScale.x 而不是改 width，
        /// 这样是以中心为轴展开，两端对称，不会往单侧跑偏。
        /// </summary>
        private void SetProgress(float value)
        {
            if (background != null)
            {
                var scale = background.rectTransform.localScale;
                scale.x = Mathf.Max(0.0001f, value);
                background.rectTransform.localScale = scale;
            }

            if (content != null)
            {
                var color = content.color;
                // 文字比背景稍晚淡入：条子先展开到位，文字才完全清晰，
                // 这样能看清"背景先到、文字后到"的两段节奏。
                color.a = Mathf.Clamp01((value - 0.15f) * 1.8f);
                content.color = color;
            }
        }
    }
}
