using System;
using UnityEngine;

namespace MainCore
{
    /// <summary>
    /// 红区渲染用到的官方纹理与材质的加载与缓存。
    ///
    /// 纹理没有走 PNG 导入，而是打包成 <c>.bytes</c>（magic + 宽高 + RGBA32 原始数据）
    /// 放在 <c>Resources/BlockArea/</c>：
    /// 这样可以完全控制 wrap / filter / 是否 sRGB，不受各平台 TextureImporter
    /// 默认设置影响 —— 红区 mask 的数值（0.09..0.12 阈值等）对采样方式非常敏感。
    ///
    /// 纹理源：Phira-Pro（GPL-3.0）assets/blockarea，本身是官方资源导出。
    /// </summary>
    public static class BlockAreaAssets
    {
        private const string ResourceDir = "BlockArea/";
        private const string ActiveShaderResource = "Shaders/BlockAreaActive";
        private const string ActiveShaderName = "PtyOS/BlockAreaActive";
        private const string DisabledShaderResource = "Shaders/BlockAreaDisabled";
        private const string DisabledShaderName = "PtyOS/BlockAreaDisabled";

        /// <summary>位移噪声（BlockNoise1）。Mirror + Point，与官方一致。</summary>
        public static Texture2D Displace { get; private set; }

        /// <summary>火花（PointNoise）。Repeat + Point。</summary>
        public static Texture2D Spark { get; private set; }

        /// <summary>噪声 SDF（FD_Noise_00000）。Mirror + Point。</summary>
        public static Texture2D Noise { get; private set; }

        /// <summary>手指 hover 精灵（TouchHover）。Clamp + Bilinear。</summary>
        public static Texture2D TouchHover { get; private set; }

        public static Shader ActiveShader { get; private set; }
        public static Shader DisabledShader { get; private set; }

        /// <summary>生效层（后处理，带 GrabPass）。</summary>
        public static Material ActiveMaterial { get; private set; }

        /// <summary>未生效 / 预警层（画在音符之下）。</summary>
        public static Material DisabledMaterial { get; private set; }

        /// <summary>所有纹理与着色器是否就绪。未就绪时红区不绘制。</summary>
        public static bool Ready { get; private set; }

        private static bool _initialized;

        /// <summary>纹理原始字节（供 CPU 侧采样，例如 mask 位移）。</summary>
        public static byte[] DisplacePixels { get; private set; }
        public static int DisplaceWidth { get; private set; }
        public static int DisplaceHeight { get; private set; }

        public static byte[] TouchPixels { get; private set; }
        public static int TouchWidth { get; private set; }
        public static int TouchHeight { get; private set; }

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            Displace = LoadTexture("BlockNoise1", TextureWrapMode.Mirror, FilterMode.Point,
                out byte[] displacePixels, out int displaceW, out int displaceH);
            DisplacePixels = displacePixels;
            DisplaceWidth = displaceW;
            DisplaceHeight = displaceH;

            Spark = LoadTexture("PointNoise", TextureWrapMode.Repeat, FilterMode.Point, out _, out _, out _);
            Noise = LoadTexture("FD_Noise_00000", TextureWrapMode.Mirror, FilterMode.Point, out _, out _, out _);

            TouchHover = LoadTexture("TouchHover", TextureWrapMode.Clamp, FilterMode.Bilinear,
                out byte[] touchPixels, out int touchW, out int touchH);
            TouchPixels = touchPixels;
            TouchWidth = touchW;
            TouchHeight = touchH;

            ActiveShader = Resources.Load<Shader>(ActiveShaderResource);
            if (ActiveShader == null) ActiveShader = Shader.Find(ActiveShaderName);
            DisabledShader = Resources.Load<Shader>(DisabledShaderResource);
            if (DisabledShader == null) DisabledShader = Shader.Find(DisabledShaderName);

            if (ActiveShader != null) ActiveMaterial = CreateMaterial(ActiveShader);
            if (DisabledShader != null) DisabledMaterial = CreateMaterial(DisabledShader);

            Ready = Displace != null && Spark != null && Noise != null && TouchHover != null &&
                    ActiveMaterial != null && DisabledMaterial != null;

            if (!Ready)
            {
                Debug.LogError("[BlockArea] 红区资源缺失，噪域将不绘制。请确认 " +
                               "Assets/Resources/BlockArea/*.bytes 与 " +
                               "Assets/Resources/Shaders/BlockAreaActive.shader、" +
                               "BlockAreaDisabled.shader 已导入。");
            }
        }

        private static Material CreateMaterial(Shader shader)
        {
            var m = new Material(shader) { hideFlags = HideFlags.DontSave };
            m.SetFloat("_EdgeOpacity", 0.8f);
            m.SetFloat("_FillStrength", 0.667f);
            m.SetFloat("_FillOpacity", 0.667f);
            m.SetFloat("_GlowIntensity", 0.8f);
            m.SetFloat("_SparkMapOpacity", 5.69f);
            m.SetFloat("_SparkHueShiftAmount", 0.2f);
            m.SetFloat("_SparkDisplaceIntensity", 2.39f);
            m.SetFloat("_DisplaceBlendIntensity", 0.411f);
            m.SetFloat("_DisplaceSpeed", 1.5f);
            m.SetFloat("_DisplaceStrength", 0.15f);
            m.SetFloat("_TouchPosRadius", 0.5f);
            m.SetFloat("_TouchPosSDFSmoothness", 0.47f);
            m.SetFloat("_TouchPosSDFFalloff", 0.41f);
            m.SetFloat("_BackgroundPixelScale", 6f);
            m.SetFloat("_ShineSpeed", 37.9f);
            m.SetFloat("_ShineBrightness", 0.12f);
            m.SetFloat("_TouchDisplaceSpeed", 2.9f);
            m.SetFloat("_TouchDisplaceStrength", 0.08f);
            m.SetFloat("_NoiseEvoSpeed", 0.03f);
            m.SetFloat("_NoiseDirChangeSpeed", 60f);
            m.SetFloat("_NoiseDisplaceStrength", 1f);
            m.SetFloat("_NoiseRadius", 0.48f);
            m.SetFloat("_NoiseSmoothness", 1f);
            m.SetFloat("_SDFCellSize", 0.11f);
            m.SetFloat("_SDFSmoothness", 0.63f);
            m.SetFloat("_SDFFalloff", 0.34f);
            m.SetFloat("_SDFMoveSpeed", 9.3f);
            m.SetFloat("_TouchBackgroundPixelScale", 8f);

            m.SetColor("_EdgeColor", new Color(1f, 0.33018857f, 0.33018857f, 1f));
            m.SetColor("_FillColor", new Color(0.7132075f, 0.23549296f, 0.23549296f, 1f));
            m.SetColor("_GlowColor", new Color(1f, 0.17924517f, 0.17924517f, 1f));
            m.SetVector("_DisplaceDirection", new Vector4(1f, 1f, 0f, 0f));
            m.SetColor("_ShineColor", Color.white);
            m.SetVector("_TouchDisplaceDirection", new Vector4(1f, 1f, 0f, 0f));
            m.SetColor("_NoiseTint", new Color(1f, 0f, 0f, 1f));
            m.SetColor("_TouchGlowColor", new Color(1f, 0f, 0f, 1f));
            m.SetColor("_SparkTint", new Color(1f, 0.28490567f, 0.28490567f, 1f));

            m.SetColor("_DisabledFillColor", new Color(0.497f, 0.13766898f, 0.13766898f, 1f));
            m.SetFloat("_DisabledFillOpacity", 0.4f);
            m.SetColor("_DisabledSparkTint", new Color(0.31132078f, 0.077830195f, 0.077830195f, 1f));
            m.SetFloat("_DisabledSparkOpacity", 3.5f);
            m.SetFloat("_DisabledSparkIntensity", 2.29f);
            m.SetFloat("_DisabledSpeed", 0.3f);

            m.SetTexture("_DisplaceTex", Displace);
            m.SetTexture("_SparkTex", Spark);
            m.SetTexture("_NoiseTex", Noise);
            m.SetColor("_Tint", Color.white);

            var zeros = new Vector4[10];
            m.SetVectorArray("_TouchPos", zeros);
            m.SetFloat("_TouchPosCount", 0f);
            m.SetFloat("_TouchPosShine", 0f);

            // 逐帧会被覆盖。这里先给一组安全值，避免首帧之前
            // clipHalfWidth = _View.y * 0.8889 / _View.x 算出 0/0 = NaN。
            m.SetVector("_UnityTime", Vector4.zero);
            m.SetVector("_View", new Vector4(16f, 9f, 16f / 9f, 0f));
            m.SetVector("_EffectRT_TexelSize", new Vector4(1f / 480f, 1f / 270f, 480f, 270f));
            return m;
        }

        /// <summary>
        /// 新建一张 mask 纹理。数据是「数值」而非颜色，所以关闭 sRGB 转换（linear = true），
        /// 否则 0.09..0.12 这类阈值在 sRGB 采样下会整体偏移。
        /// </summary>
        public static Texture2D CreateMaskTexture(int width, int height, string name)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = name
            };
            return tex;
        }

        private static Texture2D LoadTexture(string name, TextureWrapMode wrap, FilterMode filter,
            out byte[] pixels, out int width, out int height)
        {
            pixels = null;
            width = height = 0;

            var asset = Resources.Load<TextAsset>(ResourceDir + name);
            if (asset == null)
            {
                Debug.LogWarning($"[BlockArea] 找不到纹理资源 Resources/{ResourceDir}{name}.bytes");
                return null;
            }

            var raw = asset.bytes;
            if (raw == null || raw.Length < 16)
            {
                Debug.LogWarning($"[BlockArea] 纹理资源 {name}.bytes 头部损坏");
                return null;
            }

            // magic "BAX1" + uint32 宽 + uint32 高 + uint32 格式
            if (raw[0] != 'B' || raw[1] != 'A' || raw[2] != 'X' || raw[3] != '1')
            {
                Debug.LogWarning($"[BlockArea] 纹理资源 {name}.bytes magic 不匹配");
                return null;
            }

            width = BitConverter.ToInt32(raw, 4);
            height = BitConverter.ToInt32(raw, 8);
            int format = BitConverter.ToInt32(raw, 12);
            int expected = width * height * 4;
            if (format != 1 || width <= 0 || height <= 0 || raw.Length - 16 < expected)
            {
                Debug.LogWarning($"[BlockArea] 纹理资源 {name}.bytes 尺寸/格式异常 " +
                                 $"({width}x{height} fmt={format} len={raw.Length})");
                return null;
            }

            pixels = new byte[expected];
            Buffer.BlockCopy(raw, 16, pixels, 0, expected);

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                wrapMode = wrap,
                filterMode = filter,
                name = name
            };
            tex.LoadRawTextureData(pixels);
            tex.Apply(false, false);
            return tex;
        }
    }
}
