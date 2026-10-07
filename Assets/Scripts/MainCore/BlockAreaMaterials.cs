using UnityEngine;

namespace MainCore
{
    /// <summary>
    /// 红区渲染使用的共享材质与排序常量。
    ///
    /// 渲染管线（全部在同一个 sorting layer 内，靠 sortingOrder 排先后）：
    ///
    ///   19900  Preview        预警期（Disabled / Ready）的淡红，不参与 stencil
    ///   19950  StencilClear   全屏，把 stencil bit0 抹成 0
    ///   20000  NormalWriter   普通块：bit0 = 1
    ///   20001  SubtractWriter 减块：bit0 取反
    ///   20002  Fill           全屏，bit0 == 1 处画红
    ///
    /// 这样屏幕上红色出现的形状，与手指被阻断的范围逐像素相同。
    /// </summary>
    public static class BlockAreaMaterials
    {
        private const string ResourceDir = "Shaders/";

        // ---- 排序 ----
        public const int OrderPreview = 19900;
        public const int OrderStencilClear = 19950;
        public const int OrderNormalWriter = 20000;
        public const int OrderSubtractWriter = 20001;
        public const int OrderFill = 20002;

        // ---- 配色 ----
        // 取自 Phira-Pro 交叉标注的官方常量：
        //   _FillColor  = (0.713, 0.235, 0.235)
        //   _EdgeColor  = (1.000, 0.330, 0.330)
        //   _GlowColor  = (1.000, 0.179, 0.179)
        /// <summary>生效期的填充色。</summary>
        public static readonly Color ActiveFill = new Color(0.88f, 0.22f, 0.26f, 0.62f);

        /// <summary>预警期（Disabled / Ready）的淡红。</summary>
        public static readonly Color PreviewFill = new Color(0.88f, 0.22f, 0.26f, 0.28f);

        private static Material _normalWriter;
        private static Material _subtractWriter;
        private static Material _fill;
        private static Material _stencilClear;
        private static bool _loadFailed;

        /// <summary>普通块 stencil 写入材质（bit0 = 1）。</summary>
        public static Material NormalWriter =>
            _normalWriter ??= Load("BlockAreaStencilNormal", "PtyOS/BlockAreaStencilNormal");

        /// <summary>减块 stencil 翻转材质（bit0 取反）。</summary>
        public static Material SubtractWriter =>
            _subtractWriter ??= Load("BlockAreaStencilSubtract", "PtyOS/BlockAreaStencilSubtract");

        /// <summary>显示材质（bit0 == 1 处画红）。</summary>
        public static Material Fill =>
            _fill ??= Load("BlockAreaFill", "PtyOS/BlockAreaFill");

        /// <summary>stencil 清零材质（全屏，bit0 = 0）。</summary>
        public static Material StencilClear =>
            _stencilClear ??= Load("BlockAreaStencilClear", "PtyOS/BlockAreaStencilClear");

        /// <summary>四个着色器是否都成功取到。取不到时红区退化为基础矩形绘制。</summary>
        public static bool Ready => NormalWriter != null && SubtractWriter != null
                                    && Fill != null && StencilClear != null;

        private static Material Load(string resourceName, string shaderName)
        {
            var shader = Resources.Load<Shader>(ResourceDir + resourceName);
            if (shader == null) shader = Shader.Find(shaderName);
            if (shader == null)
            {
                if (!_loadFailed)
                {
                    _loadFailed = true;
                    Debug.LogError($"[BlockArea] 找不到着色器 {shaderName}，" +
                                   "红区将退化为不含挖洞的基础矩形。请确认 " +
                                   $"Assets/Resources/{ResourceDir}{resourceName}.shader 已导入。");
                }
                return null;
            }

            return new Material(shader) { hideFlags = HideFlags.DontSave };
        }
    }
}
