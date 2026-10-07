using System;
using System.Collections.Generic;
using UnityEngine;

namespace MainCore
{
    /// <summary>
    /// 红区 mask 的 CPU 光栅化 —— 等价 Phira-Pro 的
    /// <c>prpr/src/core/block_mask.rs</c>（它本身是官方多相机 RT 管线的 CPU 复刻）。
    ///
    /// 每帧产出两张 RGBA32 位图（分辨率 = 视口 / 4，与官方 EffectRT 一致）：
    ///
    ///   <c>Rgba</c>  R = 位移后的 Compose（手指被阻断的区域）
    ///                G = Edge（1 像素宽的边）
    ///                B = Glow（5 层加权膨胀）
    ///                A = Disabled Compose（未生效期的红色蒙版）
    ///   <c>Aux</c>   R = Ready Normal   G = Ready Subtract   B = Disabled   A = Hover（另填）
    ///
    /// 六个「相机层」的语义（官方用六台相机分别拍，这里用六张 u8 平面代替）：
    ///   0 Active Normal      1 Active Subtract
    ///   2 Disabled Normal    3 Disabled Subtract
    ///   4 Ready Normal       5 Ready Subtract
    ///   6 Disabled Subtract 绿通道（给 subtract_disabled 用）
    ///   7 Ready Subtract 绿通道
    ///
    /// 注意：**视觉**用的是 <c>subtract_enabled</c> 阈值窗口（0.09..0.12），
    /// 与**输入判定**用的奇偶规则是两套不同的语义 —— 官方就是这样分开的。
    /// </summary>
    public sealed class BlockAreaMaskBuilder
    {
        public const int LayerCount = 8;
        public const int MaxGlowPasses = 6;

        /// <summary>mask 相对视口的分辨率：与官方一致，视口 / 4。</summary>
        private const int MaskDownscale = 8;   // 先 /8 再 *2 == /4

        private readonly byte[][] _layers = new byte[LayerCount][];
        private byte[] _rgba;
        private byte[] _aux;
        private byte[] _enabled;

        private int _bw;
        private int _bh;
        private int _capacity;

        private readonly List<int> _rowY = new List<int>(1024);
        private readonly List<int> _rowFirst = new List<int>(1024);
        private readonly List<int> _rowLast = new List<int>(1024);

        private int[] _ping;
        private int[] _pong;
        private int[] _tmp;

        private int[] _warpXa;
        private int[] _warpXb;
        private int[] _warpRow;
        private float[] _warpU;
        private float[] _warpV;

        private byte[] _displace;
        private int _displaceW;
        private int _displaceH;

        private readonly float[] _centered = new float[256];
        private static readonly float[] GlowWeights = BuildGlowWeights();

        private static readonly byte[] OpacityLut = new byte[256];

        /// <summary>输出位图（RGBA32，行序 v=0 在前，与 Texture2D 一致）。</summary>
        public byte[] Rgba => _rgba;
        public byte[] Aux => _aux;
        public int Width => _bw;
        public int Height => _bh;

        /// <summary>有效像素数；为 0 时调用方应跳过整个绘制。</summary>
        public int NonZeroCompose { get; private set; }

        /// <summary>是否含有未生效（Disabled）内容。</summary>
        public bool HasDisabled { get; private set; }

        /// <summary>本帧是否有任何 Active / Ready 内容。</summary>
        public bool HasActiveOrReady { get; private set; }

        public BlockAreaMaskBuilder()
        {
            for (int v = 0; v < 256; v++)
                _centered[v] = Medium(Medium(v / 255f) - 0.5f);
        }

        // ============ 尺寸 ============

        private void EnsureCapacity(int bw, int bh)
        {
            if (_bw == bw && _bh == bh && _rgba != null) return;

            _bw = bw;
            _bh = bh;
            int n = bw * bh;
            _capacity = n;

            for (int i = 0; i < LayerCount; i++)
            {
                if (_layers[i] == null || _layers[i].Length != n) _layers[i] = new byte[n];
            }

            if (_rgba == null || _rgba.Length != n * 4) _rgba = new byte[n * 4];
            if (_aux == null || _aux.Length != n * 4) _aux = new byte[n * 4];
            if (_enabled == null || _enabled.Length != n) _enabled = new byte[n];

            if (_ping == null || _ping.Length != n) _ping = new int[n];
            if (_pong == null || _pong.Length != n) _pong = new int[n];
            if (_tmp == null || _tmp.Length != n) _tmp = new int[n];

            if (_warpXa == null || _warpXa.Length != bw) _warpXa = new int[bw];
            if (_warpXb == null || _warpXb.Length != bw) _warpXb = new int[bw];
            if (_warpU == null || _warpU.Length != bw) _warpU = new float[bw];
            if (_warpRow == null || _warpRow.Length != bh) _warpRow = new int[bh];
            if (_warpV == null || _warpV.Length != bh) _warpV = new float[bh];
        }

        // ============ 主入口 ============

        /// <summary>
        /// 生成一帧的 mask。
        /// </summary>
        /// <param name="zones">所有可见红区的几何（chart space）。</param>
        /// <param name="count">有效数量。</param>
        /// <param name="aspect">屏幕宽高比。</param>
        /// <param name="time">着色器时间（秒），驱动位移噪声。</param>
        /// <param name="displace">BlockNoise1 的 RGBA 原始字节。</param>
        /// <param name="viewportWidth">视口宽度（像素）。</param>
        /// <param name="viewportHeight">视口高度（像素）。</param>
        public void Build(BlockAreaZone[] zones, int count, float aspect, float time,
                          byte[] displace, int displaceW, int displaceH,
                          int viewportWidth, int viewportHeight)
        {
            if (viewportWidth <= 0 || viewportHeight <= 0) return;

            _displace = displace;
            _displaceW = displaceW;
            _displaceH = displaceH;

            int bw = Mathf.Max(viewportWidth / MaskDownscale, 1) * 2;
            int bh = Mathf.Max(viewportHeight / MaskDownscale, 1) * 2;
            EnsureCapacity(bw, bh);

            Array.Clear(_rgba, 0, _capacity * 4);
            Array.Clear(_aux, 0, _capacity * 4);
            for (int i = 0; i < LayerCount; i++) Array.Clear(_layers[i], 0, _capacity);

            HasDisabled = false;
            HasActiveOrReady = false;

            // ---- 1. 光栅化各层 ----
            int rasterized = 0;
            for (int zi = 0; zi < count; zi++)
            {
                var z = zones[zi];
                if (!z.Valid) continue;

                if (z.Active || z.Ready) HasActiveOrReady = true;
                if (!z.Active && z.Opacity > 0f) HasDisabled = true;

                Rasterize(z, aspect);
                if (_rowY.Count == 0) continue;
                ApplyContributions(z);
                rasterized++;
            }

            // 本帧没有任何块覆盖到像素：后面所有步骤（Compose 扫描、位移、Edge/Glow）
            // 都会得到空结果，直接返回。红区大半段时间都是这个分支。
            if (rasterized == 0)
            {
                NonZeroCompose = 0;
                return;
            }

            // ---- 2. Compose / Disabled / Ready ----
            int n = _capacity;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            int nonZero = 0;
            var L = _layers;
            for (int y = 0; y < _bh; y++)
            {
                int row = y * _bw;
                for (int x = 0; x < _bw; x++)
                {
                    int i = row + x;
                    bool sub = SubtractEnabled(L[1][i]);
                    byte compose = (byte)Mathf.Abs(L[0][i] - (sub ? 255 : 0));
                    _enabled[i] = compose;
                    if (compose != 0)
                    {
                        nonZero++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }

                    byte disabled = 0;
                    if (L[2][i] != 0 || L[3][i] != 0 || L[6][i] != 0)
                    {
                        SubtractDisabled(L[3][i], L[6][i], out float sr, out float sg);
                        disabled = Unorm(Mathf.Abs(sr * sg - L[2][i] / 255f));
                    }

                    byte readyS = 0;
                    if (L[7][i] != 0)
                    {
                        SubtractDisabled(L[5][i], L[7][i], out _, out float sg7);
                        readyS = Unorm(sg7);
                    }

                    _rgba[i * 4 + 3] = disabled;
                    _aux[i * 4] = L[4][i];
                    _aux[i * 4 + 1] = readyS;
                    _aux[i * 4 + 2] = disabled;
                }
            }

            NonZeroCompose = nonZero;

            bool anyCompose = maxX >= minX && maxY >= minY;
            if (!anyCompose)
            {
                // 没有任何 Compose 内容：Edge/Glow 也一定为空。
                return;
            }

            // ---- 3. 位移 Compose ----
            // 注意要按「最大位移量」外扩：位移是把目标像素反向映射到源像素，
            // 所以包围盒外的像素也可能采到盒内的内容。官方用 0.072 的包络
            // （方向归一化后每轴最多偏移 0.070704）。不外扩会出现硬切边。
            int padX = Mathf.CeilToInt(_bw * 0.072f) + 1;
            int padY = Mathf.CeilToInt(_bh * 0.072f) + 1;
            int dx0 = Mathf.Max(minX - padX, 0);
            int dy0 = Mathf.Max(minY - padY, 0);
            int dx1 = Mathf.Min(maxX + padX, _bw - 1);
            int dy1 = Mathf.Min(maxY + padY, _bh - 1);
            DisplaceCompose(dx0, dy0, dx1, dy1, time);

            // ---- 4. Edge / Glow 膨胀 ----
            RenderRings();

            // 统计位移后仍非零的像素数（仅用于诊断 / 决定是否跳过绘制）
            int after = 0;
            for (int i = 0; i < n; i++) if (_rgba[i * 4] != 0) after++;
            NonZeroCompose = after;
        }

        // ============ 光栅化 ============

        /// <summary>
        /// 把一个旋转矩形按扫描线求每行的 x 区间。与官方 <c>raster_rows</c> 逐句对应：
        /// 先把两条边投到「以 y 行为自变量」的直线上，取交集得到 [lo, hi]。
        /// </summary>
        private void Rasterize(BlockAreaZone z, float aspect)
        {
            _rowY.Clear();
            _rowFirst.Clear();
            _rowLast.Clear();
            if (z.Half.x <= 0f || z.Half.y <= 0f) return;

            float sin = Mathf.Sin(z.Angle);
            float cos = Mathf.Cos(z.Angle);
            float yHalf = Mathf.Abs(sin) * z.Half.x + Mathf.Abs(cos) * z.Half.y;

            float w = _bw, h = _bh;
            int start = Mathf.Clamp(Mathf.CeilToInt(((z.Center.y - yHalf) * aspect + 1f) * h * 0.5f - 0.5f), 0, _bh);
            int end = Mathf.Clamp(Mathf.FloorToInt(((z.Center.y + yHalf) * aspect + 1f) * h * 0.5f - 0.5f) + 1, 0, _bh);

            for (int y = start; y < end; y++)
            {
                float dy = ((y + 0.5f) * 2f / h - 1f) / aspect - z.Center.y;
                float lo = -1f, hi = 1f;
                bool ok = true;

                for (int k = 0; k < 2; k++)
                {
                    float a = k == 0 ? cos : -sin;
                    float b = k == 0 ? sin * dy : cos * dy;
                    float half = k == 0 ? z.Half.x : z.Half.y;

                    if (Mathf.Abs(a) < 1e-7f)
                    {
                        if (Mathf.Abs(b) > half) { ok = false; break; }
                    }
                    else
                    {
                        float a0 = (-half - b) / a + z.Center.x;
                        float a1 = (half - b) / a + z.Center.x;
                        lo = Mathf.Max(lo, Mathf.Min(a0, a1));
                        hi = Mathf.Min(hi, Mathf.Max(a0, a1));
                    }
                }

                if (!ok || lo > hi) continue;

                int first = Mathf.Clamp(Mathf.CeilToInt((lo + 1f) * w * 0.5f - 0.5f), 0, _bw);
                int last = Mathf.Clamp(Mathf.FloorToInt((hi + 1f) * w * 0.5f - 0.5f) + 1, 0, _bw);
                if (last <= first) continue;

                _rowY.Add(y);
                _rowFirst.Add(first);
                _rowLast.Add(last);
            }
        }

        /// <summary>
        /// 把本块写进它该去的层。等价 Phira-Pro 的 <c>contributions()</c>：
        ///   Active    → 层 0/1（减块固定 alpha 0.1）
        ///   Disabled  → 层 2/3，减块额外进层 6（绿通道）
        ///   Ready     → 层 4/5，减块额外进层 7
        /// </summary>
        private void ApplyContributions(BlockAreaZone z)
        {
            bool invert = z.Invert;
            bool active = z.Active;
            float opacity = invert ? 0.1f : z.Opacity;

            if (active)
            {
                AddLayer(invert ? 1 : 0, opacity);
            }
            else
            {
                AddLayer(2 + (invert ? 1 : 0), opacity);
                if (invert) AddLayer(6, 0.1f * z.Opacity);
            }

            if (z.Ready)
            {
                AddLayer(4 + (invert ? 1 : 0), opacity);
                if (invert) AddLayer(7, 0.1f * z.Opacity);
            }
        }

        /// <summary>
        /// 官方 Sprite 混合是 <c>SrcAlpha, One</c>（预乘加法），且每个 R8 目标都重新量化：
        /// <c>unorm(prev/255 + opacity)</c>。用 256 项查表把这一步压成一次索引。
        /// </summary>
        private void AddLayer(int layer, float opacity)
        {
            if (opacity <= 0f) return;

            for (int v = 0; v < 256; v++) OpacityLut[v] = Unorm(v / 255f + opacity);

            var plane = _layers[layer];
            for (int i = 0; i < _rowY.Count; i++)
            {
                int off = _rowY[i] * _bw;
                int first = _rowFirst[i];
                int last = _rowLast[i];
                for (int x = first; x < last; x++)
                {
                    int idx = off + x;
                    plane[idx] = OpacityLut[plane[idx]];
                }
            }
        }

        // ============ 位移 ============

        private void DisplaceCompose(int bx0, int by0, int bx1, int by1, float time)
        {
            if (_displace == null) return;

            float d = 0.70703125f;
            float dt = d * (time / 20f * 2.59f);
            int bw = _bw, bh = _bh;
            int tw = _displaceW, th = _displaceH;

            for (int x = 0; x < bw; x++)
            {
                float u = (x + 0.5f) / bw;
                _warpU[x] = u;
                _warpXa[x] = NoiseIndex(dt + u * 2.13f, tw) * 4;
                _warpXb[x] = NoiseIndex(-dt + u * 2.13f, tw) * 4;
            }
            for (int y = 0; y < bh; y++)
            {
                float v = (y + 0.5f) / bh;
                _warpV[y] = v;
                _warpRow[y] = NoiseIndex(dt + v * 1.02f, th) * tw * 4;
            }

            var dis = _displace;
            var tgt = _rgba;
            var enabled = _enabled;
            var centered = _centered;

            for (int y = by0; y <= by1; y++)
            {
                int row = _warpRow[y];
                float v = _warpV[y];
                int yBase = y * bw;
                for (int x = bx0; x <= bx1; x++)
                {
                    float a = centered[dis[row + _warpXa[x]]];
                    float b = centered[dis[row + _warpXb[x]]];
                    float u = _warpU[x];
                    float du = (d * a + b * -d) * 0.1f + u;
                    float dv = (d * a + b * d) * 0.1f + v;

                    int sx = Mathf.Clamp((int)Mathf.Floor(du * bw), 0, bw - 1);
                    int sy = Mathf.Clamp((int)Mathf.Floor(dv * bh), 0, bh - 1);
                    tgt[(yBase + x) * 4] = enabled[sy * bw + sx];
                }
            }
        }

        /// <summary>镜像（三角波）取纹理索引，等价 GL 的 GL_MIRRORED_REPEAT。</summary>
        private static int NoiseIndex(float v, int size)
        {
            v %= 2f;
            if (v < 0f) v += 2f;
            if (v > 1f) v = 2f - v;
            int i = (int)Mathf.Floor(v * size);
            return Mathf.Clamp(i, 0, size - 1);
        }

        // ============ Edge / Glow ============

        /// <summary>
        /// 对 Compose 位图做 6 次 3x3 膨胀，逐层加权累加出 Glow（B 通道），
        /// 第一层同时给出 1 像素宽的 Edge（G 通道）。等价官方 <c>render_gray_rings</c>。
        ///
        /// 权重 (6-pass)^2.65 / sum，最后一层 0.00397 &lt; 0.01 会被跳过，实际只做 5 层。
        /// </summary>
        private void RenderRings()
        {
            int n = _capacity;
            int bw = _bw, bh = _bh;

            var ping = _ping;
            var pong = _pong;
            var tmp = _tmp;
            var rgba = _rgba;

            for (int i = 0; i < n; i++) ping[i] = rgba[i * 4];

            for (int pass = 0; pass < MaxGlowPasses; pass++)
            {
                float weight = GlowWeights[pass];
                if (weight < 0.01f) break;

                Max3Horizontal(ping, tmp, bw, bh);
                Max3Vertical(tmp, pong, bw, bh);

                for (int i = 0; i < n; i++)
                {
                    int mx = pong[i];
                    int delta = mx - ping[i];
                    if (pass == 0) rgba[i * 4 + 1] = (byte)delta;

                    if (delta != 0)
                    {
                        float outside = 1f - rgba[i * 4] / 255f;
                        float v = weight * (outside * (delta / 255f)) + rgba[i * 4 + 2] / 255f;
                        rgba[i * 4 + 2] = Unorm(v);
                    }
                }

                var swap = _ping; _ping = _pong; _pong = swap;
                ping = _ping;
                pong = _pong;
            }
        }

        private static void Max3Horizontal(int[] src, int[] dst, int w, int h)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                dst[row] = Math.Max(src[row], src[row + 1]);
                for (int x = 1; x < w - 1; x++)
                {
                    int i = row + x;
                    dst[i] = Math.Max(src[i - 1], Math.Max(src[i], src[i + 1]));
                }
                dst[row + w - 1] = Math.Max(src[row + w - 2], src[row + w - 1]);
            }
        }

        private static void Max3Vertical(int[] src, int[] dst, int w, int h)
        {
            for (int x = 0; x < w; x++) dst[x] = Math.Max(src[x], src[x + w]);
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    dst[i] = Math.Max(src[i - w], Math.Max(src[i], src[i + w]));
                }
            }
            int last = (h - 1) * w;
            for (int x = 0; x < w; x++) dst[last + x] = Math.Max(src[last - w + x], src[last + x]);
        }

        // ============ 数值工具（与官方/Rust 逐位对齐）============

        /// <summary>Rust: <c>(v.clamp(0,1) * 255).round() as u8</c>。输入是 0..1 的归一化值。</summary>
        private static byte Unorm(float v)
        {
            // 定点快路径：这里 v 恒为非负，截断等价于 floor，
            // 比 Mathf.FloorToInt（带 checked 转换）快不少，而这个函数在热循环里。
            int i = (int)(v * 255f + 0.5f);
            return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i));
        }

        /// <summary>Native SubtractBlockBlender pass 0：0.09..0.12 的阈值窗口，不是奇偶。</summary>
        private static bool SubtractEnabled(byte red)
        {
            float r = red / 255f;
            return r >= 0.09f && r < 0.12f;
        }

        /// <summary>未生效路径下的减块混合，含 RG16 的中间量化。</summary>
        private static void SubtractDisabled(byte red, byte green, out float sr, out float sg)
        {
            float g = green / 255f;
            float t = Mathf.Clamp((g - 0.2f) * -10f, 0f, 1f);
            float r = (SubtractEnabled(red) ? 1f : 0f) + t * t * (3f - 2f * t);
            sr = Unorm(r) / 255f;
            sg = Unorm(g * r * 10f) / 255f;
        }

        /// <summary>
        /// f16 (binary16) 的 ties-to-even 舍入。
        /// 官方的 noise / 方向中间量是 mediump，CPU 复刻必须显式对齐这一步。
        /// </summary>
        private static float Medium(float value)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(value);
            bits = (bits + 0xFFFu + ((bits >> 13) & 1u)) & ~0x1FFFu;
            return BitConverter.Int32BitsToSingle((int)bits);
        }

        private static float[] BuildGlowWeights()
        {
            var w = new float[MaxGlowPasses];
            float sum = 0f;
            for (int k = 1; k <= MaxGlowPasses; k++) sum += Mathf.Pow(k, 2.65f);
            for (int pass = 0; pass < MaxGlowPasses; pass++)
                w[pass] = Mathf.Pow(MaxGlowPasses - pass, 2.65f) / sum;
            return w;
        }
    }
}
