using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MainCore.UI
{
    /// <summary>
    /// Phigros 风格的启动背景（EntryScene 专用）。
    ///
    /// 参考 Phigros 4.0.1 的 SplashScene（Tap to Start 画面）观感复刻：
    ///   · 深色底
    ///   · 大量白色半透明三角形缓慢漂浮 + 自转
    ///   · 若干柔光光斑缓慢呼吸（对应官方粒子用的「光斑」贴图）
    ///   · 整体由暗到亮地渐入
    ///
    /// 全部程序化生成，不依赖任何美术资源；也不需要手动改场景 ——
    /// EntryScene 加载时会自动挂上（见 AutoBoot）。
    /// 如果场景里已经手动挂了本组件，则不会再重复创建。
    /// </summary>
    [DisallowMultipleComponent]
    public class SplashTriangleBackdrop : MonoBehaviour
    {
        [Header("底色（盖住原有背景图；关掉就叠加在原背景上）")]
        public bool useBackdrop = true;
        [Range(0f, 1f)] public float backdropAlpha = 0.93f;
        public Color backdropColor = new Color(0.027f, 0.031f, 0.047f, 1f);

        [Header("三角形")]
        [Min(0)] public int triangleCount = 34;
        /// <summary>三角形尺寸范围，单位是「屏幕高度」的比例（0.05 = 屏高的 5%）。</summary>
        public Vector2 sizeRange = new Vector2(0.05f, 0.26f);
        [Range(0f, 1f)] public float triangleAlphaRange = 0.16f;
        public Color triangleColor = Color.white;
        /// <summary>漂移速度（屏幕高度/秒）。</summary>
        public float driftSpeed = 0.035f;
        /// <summary>自转速度（度/秒）。</summary>
        public float spinSpeed = 7f;
        /// <summary>是否允许顺时针/逆时针混合。</summary>
        public bool spinBothWays = true;

        [Header("光斑")]
        [Min(0)] public int glowCount = 16;
        public Color glowColor = new Color(0.62f, 0.82f, 1f);
        [Range(0f, 1f)] public float glowAlpha = 0.42f;
        /// <summary>光斑尺寸，同样是屏高比例。</summary>
        public Vector2 glowSizeRange = new Vector2(0.12f, 0.46f);
        /// <summary>呼吸速度（次/秒）。</summary>
        public float glowPulseSpeed = 0.35f;

        [Header("渐亮")]
        public float fadeInDuration = 2.6f;
        public AnimationCurve fadeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        /// <summary>渐亮完成后是否继续保留（false = 渐亮结束后自毁，只做一次入场）。</summary>
        public bool keepAliveAfterFade = true;

        [Header("渲染")]
        public int sortingOrder = -100;
        public Vector2 referenceResolution = new Vector2(1920f, 1080f);

        [Header("随机种子（0 = 每次随机）")]
        public int randomSeed = 0;

        // ---------------------------------------------------------------- runtime

        private RectTransform _root;
        private CanvasGroup _group;
        private float _elapsed;
        private readonly List<Item> _items = new List<Item>();
        private bool _built;

        private struct Item
        {
            public RectTransform rt;
            public Image img;
            public Vector2 size;
            public Vector2 dir;      // 归一化漂移方向
            public float speed;      // 屏高/秒
            public float spin;       // 度/秒
            public float baseAlpha;
            public float phase;      // 光斑呼吸相位
            public float baseSize;   // 光斑用
            public bool isGlow;
        }

        private void Awake()
        {
            Build();
        }

        private void OnEnable()
        {
            if (!_built) Build();
        }

        private void Update()
        {
            if (!_built) return;

            float dt = Time.unscaledDeltaTime;
            _elapsed += dt;

            // ---- 整体渐亮
            if (_group != null)
            {
                float t = fadeInDuration <= 0f ? 1f : Mathf.Clamp01(_elapsed / fadeInDuration);
                _group.alpha = fadeCurve != null ? fadeCurve.Evaluate(t) : t;

                if (!keepAliveAfterFade && t >= 1f)
                {
                    // 渐亮完成后停留一小会儿再自毁
                    if (_elapsed > fadeInDuration + 0.5f) Destroy(gameObject);
                }
            }

            // ---- 参考分辨率下的可视范围（留 30% 余量，让对象从屏外进出）
            float refH = referenceResolution.y;
            float refW = referenceResolution.x;
            float halfW = refW * 0.5f * 1.3f;
            float halfH = refH * 0.5f * 1.3f;

            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.rt == null) continue;

                // 漂移
                Vector2 pos = it.rt.anchoredPosition;
                pos += it.dir * (it.speed * refH * dt);

                // 回绕
                if (pos.x > halfW) pos.x = -halfW;
                else if (pos.x < -halfW) pos.x = halfW;
                if (pos.y > halfH) pos.y = -halfH;
                else if (pos.y < -halfH) pos.y = halfH;
                it.rt.anchoredPosition = pos;

                // 自转
                if (it.spin != 0f)
                {
                    var e = it.rt.localEulerAngles;
                    e.z += it.spin * dt;
                    it.rt.localEulerAngles = e;
                }

                // 光斑呼吸
                if (it.isGlow && it.img != null)
                {
                    float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * glowPulseSpeed * Mathf.PI * 2f + it.phase);
                    pulse = Mathf.Lerp(0.35f, 1f, pulse);
                    var c = glowColor;
                    c.a = it.baseAlpha * pulse;
                    it.img.color = c;
                    float s = it.baseSize * Mathf.Lerp(0.85f, 1.15f, pulse);
                    it.rt.sizeDelta = new Vector2(s, s);
                }
            }
        }

        // ---------------------------------------------------------------- build

        public void Build()
        {
            if (_built) return;
            _built = true;

            var rng = randomSeed == 0
                ? new System.Random()
                : new System.Random(randomSeed);

            // ---- 自建 Canvas（Screen Space Overlay，sortingOrder 很低，压在已有 UI 之下）
            var canvasGO = new GameObject("SplashTriangleCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
            canvasGO.transform.SetParent(transform, false);

            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _group = canvasGO.GetComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            _root = canvasGO.GetComponent<RectTransform>();
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = Vector2.zero;
            _root.offsetMax = Vector2.zero;

            float refW = referenceResolution.x;
            float refH = referenceResolution.y;
            float halfW = refW * 0.5f;
            float halfH = refH * 0.5f;

            var triSprite = GetTriangleSprite();
            var glowSprite = GetGlowSprite();

            // ---- 底色
            if (useBackdrop)
            {
                var bg = NewImage("Backdrop", _root, null);
                var c = backdropColor;
                c.a = backdropAlpha;
                bg.color = c;
                Stretch(bg.rectTransform);
                bg.raycastTarget = false;
            }

            // ---- 柔光光斑（画在三角形下面）
            for (int i = 0; i < glowCount; i++)
            {
                var img = NewImage("Glow_" + i, _root, glowSprite);
                img.raycastTarget = false;
                img.color = glowColor;

                float baseSize = Mathf.Lerp(glowSizeRange.x, glowSizeRange.y, (float)rng.NextDouble()) * refH;
                var rt = img.rectTransform;
                rt.sizeDelta = new Vector2(baseSize, baseSize);
                rt.anchoredPosition = RandPos(rng, halfW, halfH);

                _items.Add(new Item
                {
                    rt = rt,
                    img = img,
                    size = new Vector2(baseSize, baseSize),
                    dir = RandDir(rng),
                    speed = driftSpeed * 0.55f * Mathf.Lerp(0.5f, 1.5f, (float)rng.NextDouble()),
                    spin = 0f,
                    baseAlpha = glowAlpha * Mathf.Lerp(0.5f, 1f, (float)rng.NextDouble()),
                    phase = (float)rng.NextDouble() * Mathf.PI * 2f,
                    baseSize = baseSize,
                    isGlow = true,
                });
            }

            // ---- 三角形
            for (int i = 0; i < triangleCount; i++)
            {
                var img = NewImage("Triangle_" + i, _root, triSprite);
                img.raycastTarget = false;

                float alpha = triangleAlphaRange * Mathf.Lerp(0.35f, 1f, (float)rng.NextDouble());
                var c = triangleColor;
                c.a = alpha;
                img.color = c;

                float size = Mathf.Lerp(sizeRange.x, sizeRange.y, (float)rng.NextDouble()) * refH;
                var rt = img.rectTransform;
                rt.sizeDelta = new Vector2(size, size);
                rt.anchoredPosition = RandPos(rng, halfW, halfH);
                rt.localEulerAngles = new Vector3(0f, 0f, (float)rng.NextDouble() * 360f);

                float spd = spinSpeed * Mathf.Lerp(0.4f, 1.6f, (float)rng.NextDouble());
                if (spinBothWays && rng.Next(2) == 0) spd = -spd;

                _items.Add(new Item
                {
                    rt = rt,
                    img = img,
                    size = new Vector2(size, size),
                    dir = RandDir(rng),
                    speed = driftSpeed * Mathf.Lerp(0.5f, 1.5f, (float)rng.NextDouble()),
                    spin = spd,
                    baseAlpha = alpha,
                    phase = 0f,
                    baseSize = size,
                    isGlow = false,
                });
            }
        }

        private static Vector2 RandPos(System.Random rng, float halfW, float halfH)
        {
            return new Vector2(
                ((float)rng.NextDouble() * 2f - 1f) * halfW,
                ((float)rng.NextDouble() * 2f - 1f) * halfH);
        }

        private static Vector2 RandDir(System.Random rng)
        {
            float a = (float)rng.NextDouble() * Mathf.PI * 2f;
            // 整体偏「向上飘」一点，更接近官方观感
            return Quaternion.Euler(0f, 0f, 0f) * new Vector2(Mathf.Cos(a), Mathf.Sin(a) * 0.6f + 0.4f).normalized;
        }

        private static Image NewImage(string name, RectTransform parent, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.type = Image.Type.Simple;
            img.preserveAspect = false;
            return img;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        // ---------------------------------------------------------------- sprites

        private static Sprite _triSprite;
        private static Sprite _glowSprite;

        /// <summary>程序化生成一个白色实心等边三角形（带边缘抗锯齿）。</summary>
        private static Sprite GetTriangleSprite()
        {
            if (_triSprite != null) return _triSprite;

            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var a = new Vector2(0.5f, 0.955f);
            var b = new Vector2(0.035f, 0.045f);
            var c = new Vector2(0.965f, 0.045f);

            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    var p = new Vector2((x + 0.5f) / S, (y + 0.5f) / S);
                    float d = TriangleSdf(p, a, b, c);       // 内部为负
                    float cov = Mathf.Clamp01(0.5f - d * S); // 1px 抗锯齿带
                    px[y * S + x] = new Color(1f, 1f, 1f, cov);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);

            _triSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f,
                (uint)SpriteMeshType.FullRect);
            _triSprite.hideFlags = HideFlags.HideAndDontSave;
            return _triSprite;
        }

        /// <summary>程序化生成一个柔光圆点（径向衰减），对应官方的「光斑」贴图。</summary>
        private static Sprite GetGlowSprite()
        {
            if (_glowSprite != null) return _glowSprite;

            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var px = new Color32[S * S];
            float half = S * 0.5f;
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float v = Mathf.Clamp01(1f - r);
                    v = v * v * (3f - 2f * v);   // smoothstep，中心更亮、边缘更柔
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

        private static float SegmentDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 pa = p - a, ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Mathf.Max(Vector2.Dot(ba, ba), 1e-6f));
            return (pa - ba * h).magnitude;
        }

        private static bool InsideTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(hasNeg && hasPos);
        }

        private static float TriangleSdf(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d = Mathf.Min(SegmentDist(p, a, b), Mathf.Min(SegmentDist(p, b, c), SegmentDist(p, c, a)));
            return InsideTriangle(p, a, b, c) ? -d : d;
        }

        // ---------------------------------------------------------------- auto boot

        /// <summary>
        /// EntryScene 加载后自动挂上，不需要手动改场景。
        /// 若场景里已经手动了同组件，则不重复创建。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.name != "EntryScene") return;
            if (FindFirstObjectByType<SplashTriangleBackdrop>() != null) return;

            var go = new GameObject("[SplashTriangleBackdrop]");
            go.AddComponent<SplashTriangleBackdrop>();
        }
    }
}
