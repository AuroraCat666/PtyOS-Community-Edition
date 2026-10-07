using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MainCore.UI
{
    /// <summary>
    /// Phigros 风格的启动背景（EntryScene 专用）。
    ///
    /// 对标 Phigros 4.0.1 的 SplashScene（Tap to Start 画面）观感：
    ///   · 深色近黑底
    ///   · 一层极淡的「交叉线条」缓慢漂浮 + 自转，并整体由暗渐亮
    ///   · 少量柔光光斑缓慢呼吸（对应官方粒子用的「光斑」贴图）
    ///   · 中心区域自动避让 + 轻暗角，保证标题 / Click to start 的对比度
    ///
    /// 全程序化生成：线条用顶点直接绘制（不需要贴图），光斑 / 暗角用程序化贴图，
    /// 不引入任何官方美术资源；也不需要手动改场景 —— EntryScene 加载时会自动挂上。
    ///
    /// 调参重点：
    ///   · 嫌背景太抢眼 → 降 <see cref="lineBrightness"/> / <see cref="glowBrightness"/>
    ///   · 嫌线条不够明显 → 加 <see cref="lineGroupCount"/>、提 <see cref="lineBrightness"/>
    ///   · 嫌中心文字看不清 → 加 <see cref="focusKeep"/> 的反面（调小）、加 <see cref="focusVignette"/>
    /// </summary>
    [DisallowMultipleComponent]
    public class SplashLineBackdrop : MonoBehaviour
    {
        public enum FadeStyle
        {
            Linear,
            ExpOut,
            QuadIn,
            Smoothstep,
        }

        public enum LineFade
        {
            /// <summary>一端最亮，逐渐隐没到另一端。</summary>
            HeadToTail,
            /// <summary>中间最亮，向两端同时隐没成透明 —— 更像一道光，也会让交叉点更亮。</summary>
            CenterOut,
        }

        // ------------------------------------------------------------------ 底色

        [Header("底色（关掉就叠在场景原有背景上，比如樱花 MainBG）")]
        public bool useBackdrop = true;
        [Range(0f, 1f)] public float backdropAlpha = 0.96f;
        public Color backdropColor = new Color(0.020f, 0.024f, 0.036f, 1f);

        // ------------------------------------------------------------------ 交叉线条

        [Header("交叉线条")]
        [Tooltip("线条组数量。每组由若干条互相相交的细线构成")]
        [Range(0, 40)] public int lineGroupCount = 16;
        [Tooltip("每组线条的条数范围")]
        public Vector2Int linesPerGroup = new Vector2Int(2, 4);
        [Tooltip("单条线长度范围（参考分辨率像素）")]
        public Vector2 lineLength = new Vector2(420f, 1250f);
        [Tooltip("线宽范围（参考分辨率像素）")]
        public Vector2 lineWidth = new Vector2(2.5f, 7.0f);
        [Tooltip("线条颜色；实际亮度还要乘下面的「线条亮度」")]
        public Color lineColor = new Color(0.75f, 0.85f, 1f, 1f);
        [Tooltip("线条最高亮度（0~1）。这是「别糊住文字」最主要的旋钮")]
        [Range(0f, 0.5f)] public float lineBrightness = 0.27f;
        [Tooltip("组间亮度随机抖动：0 = 全部一样亮，1 = 最暗组只有约 45% 亮度")]
        [Range(0f, 1f)] public float lineBrightnessJitter = 0.55f;
        [Tooltip("线与线之间的角度分散：0 = 全部平行，1 = 完全自由交叉")]
        [Range(0f, 1f)] public float crossingSpread = 0.85f;
        [Tooltip("线条渐变方式")]
        public LineFade lineFade = LineFade.CenterOut;
        [Tooltip("线两端渐隐：亮处保持 100%，暗端衰减到这个比例（0 = 完全隐没成透明）")]
        [Range(0f, 1f)] public float lineFadeTail = 0f;

        // ------------------------------------------------------------------ 运动

        [Header("线条运动")]
        [Tooltip("漂浮幅度（参考分辨率像素）。用正弦往复，不会漂出屏幕")]
        public Vector2 driftAmplitude = new Vector2(10f, 46f);
        [Tooltip("漂浮频率范围（Hz，越小越慢）")]
        public Vector2 driftFrequency = new Vector2(0.015f, 0.055f);
        [Tooltip("自转速度范围（度/秒），方向随机")]
        public Vector2 spinSpeed = new Vector2(0.4f, 2.2f);

        // ------------------------------------------------------------------ 光斑

        [Header("柔光光斑")]
        [Range(0, 40)] public int glowCount = 9;
        public Color glowColor = new Color(0.42f, 0.60f, 1f, 1f);
        [Range(0f, 0.6f)] public float glowBrightness = 0.18f;
        [Tooltip("光斑直径范围（参考分辨率像素）")]
        public Vector2 glowSize = new Vector2(220f, 560f);
        [Tooltip("呼吸幅度")]
        [Range(0f, 1f)] public float glowPulse = 0.5f;
        [Tooltip("呼吸频率范围（Hz）")]
        public Vector2 glowPulseFrequency = new Vector2(0.04f, 0.12f);

        // ------------------------------------------------------------------ 中心避让

        [Header("中心避让（保证标题 / Click to start 看得清）")]
        [Tooltip("静默椭圆区半宽 / 半高（相对屏幕宽高）。落在椭圆内的元素会被压暗")]
        public Vector2 focusArea = new Vector2(0.36f, 0.30f);
        [Tooltip("椭圆内保留的亮度比例")]
        [Range(0f, 1f)] public float focusKeep = 0.18f;
        [Tooltip("椭圆向外的过渡带宽度（相对静默区半径）")]
        [Range(0.05f, 3f)] public float focusFeather = 0.85f;
        [Tooltip("额外叠一层中心暗角（0 = 关闭）。可进一步拉开文字与背景的对比")]
        [Range(0f, 1f)] public float focusVignette = 0.35f;

        // ------------------------------------------------------------------ 渐亮

        [Header("入场渐亮")]
        public float fadeInDuration = 2.6f;
        public FadeStyle fadeStyle = FadeStyle.Smoothstep;
        [Tooltip("渐亮结束后保留的整体亮度")]
        [Range(0f, 1f)] public float idleAlpha = 1f;
        [Tooltip("渐亮结束后的整体呼吸幅度")]
        [Range(0f, 0.4f)] public float idleBreathe = 0.05f;
        [Tooltip("渐亮结束后的整体呼吸频率（Hz）")]
        public float idleBreatheFrequency = 0.08f;
        [Tooltip("false = 渐亮结束后自毁，只做一次入场")]
        public bool keepAliveAfterFade = true;

        // ------------------------------------------------------------------ 渲染

        [Header("渲染层")]
        [Tooltip("相对场景「主 UI Canvas」（装着 Banner / Click to start 的那个）的排序偏移。\n" +
                 "默认 -1 = 排在它之下，不会盖住文字。")]
        public int sortingOrderOffset = -1;
        [Tooltip("找不到主 UI Canvas 时的兜底：改用 ScreenSpaceOverlay 并取这个 sortingOrder")]
        public int fallbackOverlaySortingOrder = 0;
        [Tooltip("参考分辨率，所有像素尺寸都按它缩放")]
        public Vector2 referenceResolution = new Vector2(1920f, 1080f);
        [Tooltip("随机种子，0 = 每次都不一样")]
        public int randomSeed = 20261007;

        // ------------------------------------------------------------------ runtime

        private RectTransform _root;
        private float _elapsed;
        private bool _built;
        private readonly List<Item> _items = new List<Item>();

        private struct Item
        {
            public RectTransform rt;
            public CanvasRenderer cr;
            public Vector2 origin;      // 正弦漂浮的基准位置
            public Vector2 driftDir;    // 单位方向
            public float driftAmp;      // 像素
            public float driftFreq;     // Hz
            public float spin;          // 度/秒
            public float startRot;      // 起始角度
            public float baseAlpha;
            public float phase;
            public bool isGlow;
            public float baseSize;      // 光斑直径
            public float glowFreq;      // 光斑呼吸频率（Hz），线条组用不到
        }

        private void Awake() => Build();

        private void OnEnable()
        {
            if (!_built) Build();
        }

        private void Update()
        {
            if (!_built) return;

            float dt = Time.unscaledDeltaTime;
            _elapsed += dt;
            float now = Time.unscaledTime;

            float global = EvaluateGlobalFade(_elapsed, now);

            // 用实时尺寸（CanvasScaler 处理过的虚拟分辨率），拿不到就退回参考分辨率
            float w = _root.rect.width;
            float h = _root.rect.height;
            if (w < 1f) w = referenceResolution.x;
            if (h < 1f) h = referenceResolution.y;
            float px = h / Mathf.Max(referenceResolution.y, 1f);   // 尺寸缩放系数

            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.rt == null || it.cr == null) continue;

                // ---- 漂浮（正弦往复，始终留在屏幕内）
                float s = Mathf.Sin((now * it.driftFreq + it.phase) * Mathf.PI * 2f);
                it.rt.anchoredPosition = it.origin + it.driftDir * (it.driftAmp * px * s);

                // ---- 自转
                if (it.spin != 0f)
                    it.rt.localEulerAngles = new Vector3(0f, 0f, it.startRot + it.spin * now);

                // ---- 亮度 = 基础 × 中心避让 × 全局渐亮
                float a = it.baseAlpha * FocusAtten(it.rt.anchoredPosition, w, h) * global;

                if (it.isGlow)
                {
                    float pulse = 0.5f + 0.5f * Mathf.Sin((now * it.glowFreq + it.phase) * Mathf.PI * 2f);
                    a *= Mathf.Lerp(1f - glowPulse, 1f, pulse);
                    float sz = it.baseSize * px * Mathf.Lerp(0.86f, 1.14f, pulse);
                    it.rt.sizeDelta = new Vector2(sz, sz);
                }

                it.cr.SetAlpha(a);
            }

            if (!keepAliveAfterFade && fadeInDuration > 0f && _elapsed > fadeInDuration + 0.5f)
                Destroy(gameObject);
        }

        private float EvaluateGlobalFade(float elapsed, float now)
        {
            float t = fadeInDuration <= 0f ? 1f : Mathf.Clamp01(elapsed / fadeInDuration);

            float v;
            switch (fadeStyle)
            {
                case FadeStyle.ExpOut: v = 1f - Mathf.Exp(-4.5f * t); break;
                case FadeStyle.QuadIn: v = t * t; break;
                case FadeStyle.Smoothstep: v = t * t * (3f - 2f * t); break;
                default: v = t; break;
            }

            if (t >= 1f)
            {
                float b = 1f + idleBreathe * Mathf.Sin(now * idleBreatheFrequency * Mathf.PI * 2f);
                v = idleAlpha * b;
            }
            else
            {
                v *= idleAlpha;
            }

            return Mathf.Max(0f, v);
        }

        /// <summary>中心静默椭圆：椭圆内压到 focusKeep，向外经 focusFeather 平滑过渡回 1。</summary>
        private float FocusAtten(Vector2 pos, float w, float h)
        {
            if (focusKeep >= 1f) return 1f;

            float rx = Mathf.Max(focusArea.x * w, 1f);
            float ry = Mathf.Max(focusArea.y * h, 1f);
            float nx = pos.x / rx;
            float ny = pos.y / ry;
            float d = Mathf.Sqrt(nx * nx + ny * ny);

            float k = Mathf.Clamp01((d - 1f) / Mathf.Max(focusFeather, 0.01f));
            k = k * k * (3f - 2f * k);   // smoothstep
            return Mathf.Lerp(focusKeep, 1f, k);
        }

        // ------------------------------------------------------------------ build

        public void Build()
        {
            if (_built) return;
            _built = true;

            var rng = randomSeed == 0 ? new System.Random() : new System.Random(randomSeed);

            // ---- 自建 Canvas：必须排在场景主 UI（Banner / Click to start）之下
            //      刻意不加 GraphicRaycaster —— 整层背景绝不拦截点击。
            var canvasGO = new GameObject("SplashLineCanvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGO.transform.SetParent(transform, false);

            SetupCanvas(canvasGO.GetComponent<Canvas>());

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _root = canvasGO.GetComponent<RectTransform>();
            Stretch(_root);

            float refW = referenceResolution.x;
            float refH = referenceResolution.y;
            float halfW = refW * 0.5f;
            float halfH = refH * 0.5f;

            // ---- 1. 底色
            if (useBackdrop)
            {
                var bg = NewGraphic<Image>("Backdrop", _root);
                Stretch(bg.rectTransform);
                var c = backdropColor;
                c.a = backdropAlpha;
                bg.color = c;
                bg.raycastTarget = false;
            }

            // ---- 2. 柔光光斑（画在线条下面）
            if (glowCount > 0)
            {
                var glowSprite = GetGlowSprite();
                for (int i = 0; i < glowCount; i++)
                {
                    var img = NewGraphic<Image>("Glow_" + i, _root);
                    img.sprite = glowSprite;
                    img.color = glowColor;
                    img.raycastTarget = false;

                    float baseSize = Next(rng, glowSize.x, glowSize.y);
                    img.rectTransform.sizeDelta = new Vector2(baseSize, baseSize);

                    var origin = RandPos(rng, halfW, halfH);

                    _items.Add(new Item
                    {
                        rt = img.rectTransform,
                        cr = img.canvasRenderer,
                        origin = origin,
                        driftDir = RandDir(rng),
                        driftAmp = Next(rng, driftAmplitude.x, driftAmplitude.y) * 0.6f,
                        driftFreq = Next(rng, driftFrequency.x, driftFrequency.y),
                        spin = 0f,
                        startRot = 0f,
                        baseAlpha = glowBrightness * Next(rng, 0.45f, 1f),
                        phase = Next(rng, 0f, 1f),
                        isGlow = true,
                        baseSize = baseSize,
                        glowFreq = Next(rng, glowPulseFrequency.x, glowPulseFrequency.y),
                    });
                }
            }

            // ---- 3. 交叉线条
            for (int g = 0; g < lineGroupCount; g++)
            {
                var go = new GameObject("Lines_" + g,
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(LineGroupGraphic));
                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(_root, false);
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);

                var gfx = go.GetComponent<LineGroupGraphic>();
                gfx.raycastTarget = false;
                gfx.color = lineColor;

                // 外框留足余量：UGUI 会按 RectTransform 做视口剔除，框太小线会被整组剔掉
                float box = Mathf.Max(lineLength.y, lineLength.x) * 1.8f;
                rt.sizeDelta = new Vector2(box, box);

                int n = RandomRange(rng, linesPerGroup.x, linesPerGroup.y);
                float baseAngle = Next(rng, 0f, 360f);
                float spread = Mathf.Lerp(6f, 170f, Mathf.Clamp01(crossingSpread));
                // 同一组用统一线宽（组间随机）—— 交叉出来的「网」更整齐
                float groupWidth = Next(rng, lineWidth.x, lineWidth.y);

                var segs = new List<LineGroupGraphic.Seg>(n);
                for (int i = 0; i < n; i++)
                {
                    float len = Next(rng, lineLength.x, lineLength.y);
                    float ang = baseAngle
                                + (i - (n - 1) * 0.5f) * spread
                                + Next(rng, -12f, 12f);
                    float rad = ang * Mathf.Deg2Rad;
                    var dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

                    // 只做很小的中心偏移，让同组线互相交叉而不是完全共点
                    var off = new Vector2(Next(rng, -0.13f, 0.13f), Next(rng, -0.13f, 0.13f)) * box;
                    Vector2 pa = off - dir * (len * 0.5f);
                    Vector2 pb = off + dir * (len * 0.5f);

                    if (lineFade == LineFade.CenterOut)
                    {
                        // 拆成两半，中间最亮、两端隐没 —— 交叉点自然成为最亮处
                        segs.Add(new LineGroupGraphic.Seg
                        {
                            a = pa, b = off, width = groupWidth,
                            alphaA = lineFadeTail, alphaB = 1f,
                        });
                        segs.Add(new LineGroupGraphic.Seg
                        {
                            a = off, b = pb, width = groupWidth,
                            alphaA = 1f, alphaB = lineFadeTail,
                        });
                    }
                    else
                    {
                        bool flip = rng.Next(2) == 0;
                        segs.Add(new LineGroupGraphic.Seg
                        {
                            a = pa, b = pb, width = groupWidth,
                            alphaA = flip ? lineFadeTail : 1f,
                            alphaB = flip ? 1f : lineFadeTail,
                        });
                    }
                }
                gfx.SetSegments(segs);

                float alpha = lineBrightness
                              * Mathf.Lerp(1f - lineBrightnessJitter * 0.55f, 1f, Next(rng, 0f, 1f));

                var origin2 = RandPos(rng, halfW, halfH);
                rt.anchoredPosition = origin2;

                float rot0 = Next(rng, 0f, 360f);
                rt.localEulerAngles = new Vector3(0f, 0f, rot0);

                float spd = Next(rng, spinSpeed.x, spinSpeed.y);
                if (rng.Next(2) == 0) spd = -spd;

                _items.Add(new Item
                {
                    rt = rt,
                    cr = gfx.canvasRenderer,
                    origin = origin2,
                    driftDir = RandDir(rng),
                    driftAmp = Next(rng, driftAmplitude.x, driftAmplitude.y),
                    driftFreq = Next(rng, driftFrequency.x, driftFrequency.y),
                    spin = spd,
                    startRot = rot0,
                    baseAlpha = alpha,
                    phase = Next(rng, 0f, 1f),
                    isGlow = false,
                    baseSize = 0f,
                });
            }

            // ---- 4. 中心暗角（最上层，压在线条之上）
            if (focusVignette > 0f)
            {
                var vig = NewGraphic<Image>("FocusVignette", _root);
                vig.sprite = GetVignetteSprite();
                vig.color = new Color(0f, 0f, 0f, focusVignette);
                vig.raycastTarget = false;
                var vrt = vig.rectTransform;
                vrt.sizeDelta = new Vector2(refW * 1.6f, refH * 1.6f);
                vrt.anchoredPosition = Vector2.zero;
            }
        }

        // ------------------------------------------------------------------ canvas

        /// <summary>
        /// 决定背景层画在哪一层。
        ///
        /// ⚠️ 这里**不能**用 ScreenSpaceOverlay：EntryScene 的 UI（Banner / Click to start）
        /// 挂在 ScreenSpaceCamera 的 Canvas 上，而 **Overlay Canvas 永远渲染在所有相机之上**，
        /// sortingOrder 再低也不参与比较 —— 底色与暗角会把白色 logo / 文字整片压暗
        /// （踩过一次：用户反馈「文字看不清」，就是这一条）。
        ///
        /// 所以复刻场景主 UI Canvas 的相机与 planeDistance，只把 sortingOrder 压到它下面。
        /// </summary>
        private void SetupCanvas(Canvas canvas)
        {
            var reference = FindReferenceCanvas();

            if (reference != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = reference.worldCamera;        // 为 null 时 Unity 自动用 Camera.main
                canvas.planeDistance = reference.planeDistance;
                canvas.sortingOrder = reference.sortingOrder + sortingOrderOffset;
                return;
            }

            // 兜底：找不到主 UI Canvas（例如以后换了场景结构）
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = fallbackOverlaySortingOrder;
        }

        /// <summary>
        /// 找场景里的「主 UI Canvas」作为排序参照。
        /// 首选装着 Banner / Click to start 的那个；找不到就取 ScreenSpaceCamera 里 sortingOrder 最小的。
        /// WorldSpace / ScreenSpaceOverlay 的 Canvas 不能当参照（前者由别的相机渲染，后者永远在最上层）。
        /// </summary>
        private static Canvas FindReferenceCanvas()
        {
            Canvas best = null;
            var all = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var c in all)
            {
                if (c == null || c.renderMode != RenderMode.ScreenSpaceCamera) continue;

                if (c.transform.Find("Banner") != null || c.transform.Find("Click to start") != null)
                    return c;

                if (best == null || c.sortingOrder < best.sortingOrder) best = c;
            }
            return best;
        }

        // ------------------------------------------------------------------ helpers

        private static T NewGraphic<T>(string name, RectTransform parent) where T : MaskableGraphic
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(T));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var g = go.GetComponent<T>();
            g.raycastTarget = false;
            return g;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static float Next(System.Random rng, float min, float max)
            => min + (float)rng.NextDouble() * (max - min);

        private static int RandomRange(System.Random rng, int min, int max)
        {
            if (max < min) max = min;
            return rng.Next(min, max + 1);
        }

        private static Vector2 RandPos(System.Random rng, float halfW, float halfH)
            => new Vector2(Next(rng, -halfW, halfW), Next(rng, -halfH, halfH));

        /// <summary>随机单位方向，整体略微偏「向上飘」。</summary>
        private static Vector2 RandDir(System.Random rng)
        {
            float a = Next(rng, 0f, Mathf.PI * 2f);
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a) * 0.6f + 0.4f).normalized;
        }

        // ------------------------------------------------------------------ sprites

        private static Sprite _glowSprite;
        private static Sprite _vignetteSprite;

        /// <summary>程序化柔光圆点（径向 smoothstep），对应官方的「光斑」贴图。</summary>
        private static Sprite GetGlowSprite()
        {
            if (_glowSprite != null) return _glowSprite;

            const int S = 256;
            var tex = NewTex(S);
            var px = new Color32[S * S];
            float half = S * 0.5f;
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float v = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    v = v * v * (3f - 2f * v);
                    px[y * S + x] = new Color(1f, 1f, 1f, v);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);

            _glowSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f,
                (uint)SpriteMeshType.FullRect);
            _glowSprite.hideFlags = HideFlags.HideAndDontSave;
            return _glowSprite;
        }

        /// <summary>程序化中心暗角：圆心不透明（黑）→ 边缘全透明。</summary>
        private static Sprite GetVignetteSprite()
        {
            if (_vignetteSprite != null) return _vignetteSprite;

            const int S = 256;
            var tex = NewTex(S);
            var px = new Color32[S * S];
            float half = S * 0.5f;
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float v = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    v = v * v * (3f - 2f * v);
                    px[y * S + x] = new Color(0f, 0f, 0f, v);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);

            _vignetteSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f,
                (uint)SpriteMeshType.FullRect);
            _vignetteSprite.hideFlags = HideFlags.HideAndDontSave;
            return _vignetteSprite;
        }

        private static Texture2D NewTex(int size)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        // ------------------------------------------------------------------ auto boot

        /// <summary>
        /// EntryScene 加载后自动挂上，不需要手动改场景。
        /// 若场景里已经手动挂了同组件，则不重复创建。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "EntryScene") return;
            if (FindFirstObjectByType<SplashLineBackdrop>() != null) return;

            var go = new GameObject("[SplashLineBackdrop]");
            go.AddComponent<SplashLineBackdrop>();
        }
    }

    /// <summary>
    /// 用顶点直接绘制的一组细线段 —— 每条线可以两端不同透明度（天然渐隐），
    /// 一个 Graphic 画一整组线，mesh 只在参数变化时重建，运行时开销几乎为零。
    /// </summary>
    public class LineGroupGraphic : MaskableGraphic
    {
        public struct Seg
        {
            public Vector2 a;
            public Vector2 b;
            public float width;
            public float alphaA;   // 端点 a 的相对亮度（相对 Graphic.color 的 alpha）
            public float alphaB;   // 端点 b 的相对亮度
        }

        private readonly List<Seg> _segs = new List<Seg>();

        public void SetSegments(List<Seg> segs)
        {
            _segs.Clear();
            if (segs != null) _segs.AddRange(segs);
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            for (int i = 0; i < _segs.Count; i++) AddSegment(vh, _segs[i]);
        }

        private void AddSegment(VertexHelper vh, Seg s)
        {
            Vector2 d = s.b - s.a;
            float len = d.magnitude;
            if (len < 1e-4f || s.width <= 0f) return;

            Vector2 dir = d / len;
            Vector2 half = new Vector2(-dir.y, dir.x) * (s.width * 0.5f);
            // 两端各延长半个线宽，交叉处衔接更自然
            Vector2 ext = dir * (s.width * 0.5f);

            Vector2 pa = s.a - ext;
            Vector2 pb = s.b + ext;

            Color32 ca = color;
            ca.a = ToByte(color.a * Mathf.Clamp01(s.alphaA));
            Color32 cb = color;
            cb.a = ToByte(color.a * Mathf.Clamp01(s.alphaB));

            int i0 = vh.currentVertCount;
            vh.AddVert(pa + half, ca, Vector2.zero);
            vh.AddVert(pa - half, ca, Vector2.zero);
            vh.AddVert(pb - half, cb, Vector2.zero);
            vh.AddVert(pb + half, cb, Vector2.zero);
            vh.AddTriangle(i0, i0 + 1, i0 + 2);
            vh.AddTriangle(i0, i0 + 2, i0 + 3);
        }

        private static byte ToByte(float v)
            => (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);
    }
}
