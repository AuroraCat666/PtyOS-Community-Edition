using System.Collections.Generic;
using UnityEngine;

namespace MainCore
{
    /// <summary>一帧里的一个触摸点（屏幕 UV，原点左下，0..1）。</summary>
    public struct BlockTouch
    {
        /// <summary>稳定手指 ID。同一次按下期间不能变，否则 hover 会重新淡入。</summary>
        public int Id;
        /// <summary>屏幕 UV，原点左下。</summary>
        public Vector2 ScreenUV;
    }

    /// <summary>
    /// 红区（噪域）的全屏绘制层 —— 等价 Phira-Pro 的
    /// <c>prpr/src/core/block_shader.rs::draw_layer_at</c> + <c>block_touch.rs</c>。
    ///
    /// 两块全屏 quad：
    ///   1. Disabled  画在音符之下（BeneathNotes），Blend One One，不透明度预警
    ///   2. Active    画在最上（AboveNotes），自带 GrabPass 抓取它之前的画面做像素化背景
    ///
    /// 每帧流程：
    ///   1. 把本帧所有可见红区的 chart space 几何交给 <see cref="BlockAreaMaskBuilder"/>
    ///      在 CPU 上光栅化（Compose / Disabled / Ready / Edge / Glow / 位移）
    ///   2. 上传三张纹理：_Masks / _AuxMasks / _HoverTex
    ///   3. 下发官方全部 uniform（含手指位置与 _TouchPosShine 脉冲）
    ///
    /// 坐标系说明：
    ///   * mask 走「游戏区 UV」（fieldUV），chart space 的 y ∈ [-1/aspect, 1/aspect]
    ///     正好铺满 16:9 游戏区，因此 clipHalfWidth 恒为 0.5、永不 discard。
    ///   * hover 走「全屏 UV」（sceneUV），因为手指位置本来就是全屏坐标。
    ///     官方两者共用一套视口坐标，本项目宽屏时游戏区 != 全屏，所以拆成两张纹理。
    /// </summary>
    public sealed class BlockAreaNoiseField
    {
        private const int MaxTouches = 10;
        /// <summary>官方 TouchMask 的淡入淡出时长（秒）。</summary>
        private const float TouchFadeDuration = 0.1f;
        /// <summary>官方 Round10_Blur4 sprite：44px / PPU 100 × prefab 11.5 / 相机 10。</summary>
        private const float TouchSpriteHeight = 44f / 100f * 11.5f / 10f;
        /// <summary>hover 蒙版相对全屏的分辨率：与 mask 一样是 1/8 再 ×2 的等效 1/4… 这里是原生 1/8 网格。</summary>
        private const int HoverDownscale = 8;

        private readonly BlockAreaMaskBuilder _builder = new BlockAreaMaskBuilder();
        private readonly TouchSlot[] _slots = new TouchSlot[MaxTouches];
        private readonly List<BlockTouch> _touches = new List<BlockTouch>(MaxTouches);
        private readonly Vector4[] _touchPos = new Vector4[MaxTouches];

        private Transform _root;
        private SpriteRenderer _activeRenderer;
        private SpriteRenderer _disabledRenderer;

        private Texture2D _maskTexture;
        private Texture2D _auxTexture;
        private Texture2D _hoverTexture;
        private byte[] _hoverBytes;

        private int _maskWidth;
        private int _maskHeight;
        private int _hoverWidth;
        private int _hoverHeight;

        /// <summary>游戏区世界尺寸（= 判定线/音符所在的坐标口径）。</summary>
        private float _fieldWidth;
        private float _fieldHeight;
        /// <summary>游戏区宽高比（GlobalSetting.Aspect），也是 chart space 的 aspect。</summary>
        private float _fieldAspect;

        /// <summary>游戏区的像素尺寸，只用来决定 mask / hover 的分辨率。</summary>
        private int _gamePixelWidth;
        private int _gamePixelHeight;

        private float _lastTouchTime = float.NaN;
        private bool _hoverHasContent;

        private float _appliedLocalZ = float.NaN;
        private float _appliedFieldWidth = float.NaN;
        private float _appliedFieldHeight = float.NaN;

        public bool Ready => BlockAreaAssets.Ready;

        private struct TouchSlot
        {
            public bool Used;        // finger != None
            public int Finger;
            public bool Seen;        // 本帧出现过
            public Vector2 Center;   // 屏幕 UV
            public float StartTime;
            public float StartScale;
            public float EndScale;
            public float Scale;

            public void Advance(float time)
            {
                float t = Mathf.Clamp01((time - StartTime) / TouchFadeDuration);
                Scale = StartScale + (EndScale - StartScale) * t;
            }

            /// <summary>
            /// 官方 Show 从 0 重新放大，即使这个槽位是被一个「还没淡出完」的槽位复用。
            /// </summary>
            public void Show(int finger, Vector2 center, float time)
            {
                Used = true;
                Finger = finger;
                Center = center;
                Seen = true;
                StartTime = time;
                StartScale = 0f;
                EndScale = 1f;
                Scale = 0f;
            }

            public void Hide(float time)
            {
                Used = false;
                Finger = 0;
                StartTime = time;
                StartScale = Scale;
                EndScale = 0f;
            }
        }

        // ============ 创建 ============

        public void Setup(Transform parent, float localZ)
        {
            if (_root != null) return;

            var sprite = CreateUnitSprite();

            var go = new GameObject("[BlockAreaNoiseField]");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, localZ);
            _root = go.transform;

            _disabledRenderer = CreateQuad("Disabled", _root, sprite,
                BlockAreaAssets.DisabledMaterial, ResolveLayer("BeneathNotes"), -100);
            _activeRenderer = CreateQuad("Active", _root, sprite,
                BlockAreaAssets.ActiveMaterial, ResolveLayer("AboveNotes"), 100);

            _appliedLocalZ = localZ;
            ResetTouches();
        }

        private static SpriteRenderer CreateQuad(string name, Transform parent, Sprite sprite,
            Material material, string sortingLayer, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.drawMode = SpriteDrawMode.Simple;
            if (material != null) renderer.sharedMaterial = material;
            if (!string.IsNullOrEmpty(sortingLayer)) renderer.sortingLayerName = sortingLayer;
            renderer.sortingOrder = sortingOrder;
            renderer.color = Color.white;
            renderer.enabled = false;
            return renderer;
        }

        private static Sprite CreateUnitSprite()
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.DontSave,
                name = "BlockAreaUnitTex"
            };
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();

            // pixelsPerUnit = 1 → localScale 直接等于世界尺寸。
            var sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            sprite.name = "BlockAreaUnitQuad";
            sprite.hideFlags = HideFlags.DontSave;
            return sprite;
        }

        private static string ResolveLayer(string preferred)
        {
            var layers = SortingLayer.layers;
            if (layers == null || layers.Length == 0) return null;

            foreach (var l in layers)
                if (l.name == preferred) return l.name;

            if (preferred == "BeneathNotes")
            {
                // 没有 BeneathNotes 就退到 Default（仍然是「在 Notes 之下」）。
                foreach (var l in layers)
                    if (l.name == "Default") return l.name;
            }
            else
            {
                // Active 必须压住音符：取 value 最大的层。
                SortingLayer best = layers[0];
                foreach (var l in layers)
                    if (l.value > best.value) best = l;
                return best.name;
            }

            return layers[0].name;
        }

        // ============ 每帧 ============

        /// <summary>
        /// 由管理器每帧下发游戏区尺寸（世界单位）与像素尺寸。分辨率变化时重建 mask 纹理。
        /// </summary>
        public void SetField(float screenWidth, float screenHeight, float aspect)
        {
            _fieldWidth = screenWidth;
            _fieldHeight = screenHeight;
            _fieldAspect = (IsFinite(aspect) && aspect > 0f) ? aspect : 16f / 9f;

            // mask 分辨率跟游戏区走：官方是「视口 / 8 × 2」。
            int pixelHeight = Screen.height > 0 ? Screen.height : 1080;
            int pixelWidth = Mathf.Max(1, Mathf.RoundToInt(pixelHeight * _fieldAspect));
            _gamePixelWidth = pixelWidth;
            _gamePixelHeight = pixelHeight;

            // hover 走全屏 UV，所以分辨率跟真实屏幕走。
            int screenWidthPx = Screen.width > 0 ? Screen.width : pixelWidth;
            _hoverWidth = Mathf.Max(screenWidthPx / HoverDownscale, 1);
            _hoverHeight = Mathf.Max(pixelHeight / HoverDownscale, 1);
        }

        public void SetTouches(List<BlockTouch> touches)
        {
            _touches.Clear();
            if (touches == null) return;
            for (int i = 0; i < touches.Count && i < MaxTouches; i++) _touches.Add(touches[i]);
        }

        /// <summary>
        /// 生成并绘制一帧。
        /// </summary>
        /// <param name="zones">本帧所有可见红区的 chart space 几何。</param>
        /// <param name="count">有效数量。</param>
        /// <param name="chartTime">谱面时间（秒）。官方用它驱动 _Time 与位移噪声。</param>
        /// <param name="localZ">游戏内容所在的局部 z。</param>
        public void Render(BlockAreaZone[] zones, int count, float chartTime, float localZ)
        {
            if (!BlockAreaAssets.Ready || _root == null)
            {
                Disable();
                return;
            }

            float time = IsFinite(chartTime) ? chartTime : 0f;

            SyncTransform(localZ);
            EnsureTextures();

            // 手指先推进（淡入淡出是 0.1 秒，必须每帧都走），再决定要不要重画。
            UpdateTouches(time);
            bool touchVisible = TouchVisible();

            // 既没有红区、也没有手指压在屏幕上：整条管线（光栅化 + 位移 + Edge/Glow
            // + 两次纹理上传）都可以跳过。红区大半段时间都走这个分支。
            if (count == 0 && !touchVisible)
            {
                Disable();
                return;
            }

            _builder.Build(zones, count, _fieldAspect, time,
                BlockAreaAssets.DisplacePixels, BlockAreaAssets.DisplaceWidth, BlockAreaAssets.DisplaceHeight,
                _gamePixelWidth, _gamePixelHeight);

            UploadMasks();
            UpdateHoverTexture(touchVisible);

            bool showActive = _builder.NonZeroCompose > 0 || touchVisible;
            bool showDisabled = _builder.HasDisabled;

            ApplyUniforms(BlockAreaAssets.ActiveMaterial, time, touchVisible);
            ApplyUniforms(BlockAreaAssets.DisabledMaterial, time, touchVisible);

            if (_activeRenderer != null)
            {
                _activeRenderer.sharedMaterial = BlockAreaAssets.ActiveMaterial;
                _activeRenderer.enabled = showActive;
            }
            if (_disabledRenderer != null)
            {
                _disabledRenderer.sharedMaterial = BlockAreaAssets.DisabledMaterial;
                _disabledRenderer.enabled = showDisabled;
            }
        }

        private void Disable()
        {
            if (_activeRenderer != null) _activeRenderer.enabled = false;
            if (_disabledRenderer != null) _disabledRenderer.enabled = false;
        }

        private void SyncTransform(float localZ)
        {
            if (_root == null) return;

            bool sizeChanged = !Mathf.Approximately(_appliedFieldWidth, _fieldWidth) ||
                               !Mathf.Approximately(_appliedFieldHeight, _fieldHeight);
            bool zChanged = !Mathf.Approximately(_appliedLocalZ, localZ);
            if (!sizeChanged && !zChanged) return;

            _appliedFieldWidth = _fieldWidth;
            _appliedFieldHeight = _fieldHeight;
            _appliedLocalZ = localZ;

            _root.localPosition = new Vector3(0f, 0f, localZ);
            // 恰好铺满游戏区：fieldUV == quad UV，mask 与 chart space 一一对应。
            _root.localScale = new Vector3(Mathf.Max(_fieldWidth, 0.0001f), Mathf.Max(_fieldHeight, 0.0001f), 1f);
        }

        private void EnsureTextures()
        {
            if (_maskTexture == null)
                _maskTexture = BlockAreaAssets.CreateMaskTexture(2, 2, "BlockAreaMasks");
            if (_auxTexture == null)
                _auxTexture = BlockAreaAssets.CreateMaskTexture(2, 2, "BlockAreaAux");

            bool hoverResized = _hoverTexture == null ||
                                _hoverTexture.width != _hoverWidth ||
                                _hoverTexture.height != _hoverHeight;
            if (hoverResized)
            {
                if (_hoverTexture != null) Object.Destroy(_hoverTexture);
                _hoverTexture = new Texture2D(_hoverWidth, _hoverHeight, TextureFormat.R8, false, true)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                    name = "BlockAreaHover"
                };
                _hoverBytes = new byte[_hoverWidth * _hoverHeight];
                _hoverTexture.LoadRawTextureData(_hoverBytes);
                _hoverTexture.Apply(false, false);
                _hoverHasContent = false;
            }
        }

        private void UploadMasks()
        {
            int w = _builder.Width;
            int h = _builder.Height;
            if (w <= 0 || h <= 0) return;

            if (_maskWidth != w || _maskHeight != h)
            {
                _maskWidth = w;
                _maskHeight = h;
                ResizeTexture(ref _maskTexture, w, h, "BlockAreaMasks");
                ResizeTexture(ref _auxTexture, w, h, "BlockAreaAux");
            }

            _maskTexture.LoadRawTextureData(_builder.Rgba);
            _maskTexture.Apply(false, false);
            _auxTexture.LoadRawTextureData(_builder.Aux);
            _auxTexture.Apply(false, false);
        }

        private static void ResizeTexture(ref Texture2D texture, int width, int height, string name)
        {
            if (texture != null && texture.width == width && texture.height == height) return;
            if (texture != null) Object.Destroy(texture);
            texture = BlockAreaAssets.CreateMaskTexture(width, height, name);
        }

        private void ApplyUniforms(Material material, float time, bool touchVisible)
        {
            if (material == null) return;

            // 官方 _Time 的打包方式：(t/20, t, 2t, 3t)。
            material.SetVector("_UnityTime", new Vector4(time / 20f, time, time * 2f, time * 3f));

            // 只用于 clipHalfWidth = _View.y * 0.8889 / _View.x。
            // 取游戏区尺寸 → 16:9 时正好 0.5，永远不 discard。
            material.SetVector("_View", new Vector4(
                Mathf.Max(_fieldWidth, 1f), Mathf.Max(_fieldHeight, 1f), _fieldAspect, 0f));

            material.SetVector("_EffectRT_TexelSize", new Vector4(
                1f / Mathf.Max(_maskWidth, 1), 1f / Mathf.Max(_maskHeight, 1),
                _maskWidth, _maskHeight));

            material.SetVector("_HoverTex_TexelSize", new Vector4(
                1f / Mathf.Max(_hoverWidth, 1), 1f / Mathf.Max(_hoverHeight, 1),
                _hoverWidth, _hoverHeight));

            material.SetTexture("_Masks", _maskTexture);
            material.SetTexture("_AuxMasks", _auxTexture);
            material.SetTexture("_HoverTex", _hoverTexture);

            int touchCount = touchVisible ? _touches.Count : 0;
            float touchAspect = Screen.height > 0
                ? Screen.width / (float)Screen.height
                : Mathf.Max(_fieldAspect, 0.0001f);

            // 官方：_TouchPos[i] = uv * vec2(viewportWidth / viewportHeight, 1)。
            // uv 是屏幕 UV（原点左下），与着色器里 screenPos.xy / screenPos.w 同一套。
            for (int i = 0; i < touchCount; i++)
            {
                var uv = _touches[i].ScreenUV;
                _touchPos[i] = new Vector4(uv.x * touchAspect, uv.y, 0f, 0f);
            }
            for (int i = touchCount; i < MaxTouches; i++) _touchPos[i] = Vector4.zero;

            material.SetFloat("_TouchPosCount", touchCount);
            material.SetVectorArray("_TouchPos", _touchPos);
            material.SetFloat("_TouchPosShine", (0.63f + 0.37f * (Mathf.Sin(time * 43f) * 0.5f + 0.5f)) * 2f);
        }

        // ============ 手指 hover ============

        /// <summary>
        /// 官方 TouchMask：10 个槽位，按下 0.1 秒从 0 放大到 1，抬起 0.1 秒缩回 0。
        /// 位置用屏幕 UV，原点左下。
        /// </summary>
        private void UpdateTouches(float time)
        {
            if (!IsFinite(time)) return;

            // 时间回退（重开 / seek）：官方直接整体重置，不保留「未来的」缩放。
            if (!float.IsNaN(_lastTouchTime) && time < _lastTouchTime) ResetTouches();
            _lastTouchTime = time;

            for (int i = 0; i < MaxTouches; i++)
            {
                _slots[i].Advance(time);
                _slots[i].Seen = false;
            }

            for (int k = 0; k < _touches.Count; k++)
            {
                var touch = _touches[k];
                if (!IsFinite(touch.ScreenUV.x) || !IsFinite(touch.ScreenUV.y)) continue;

                int slot = FindSlot(touch.Id);
                if (slot >= 0)
                {
                    _slots[slot].Center = touch.ScreenUV;
                    _slots[slot].Seen = true;
                }
                else
                {
                    slot = FindFreeSlot();
                    if (slot >= 0) _slots[slot].Show(touch.Id, touch.ScreenUV, time);
                }
            }

            for (int i = 0; i < MaxTouches; i++)
                if (_slots[i].Used && !_slots[i].Seen) _slots[i].Hide(time);
        }

        private int FindSlot(int finger)
        {
            for (int i = 0; i < MaxTouches; i++)
                if (_slots[i].Used && _slots[i].Finger == finger) return i;
            return -1;
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < MaxTouches; i++)
                if (!_slots[i].Used) return i;
            return -1;
        }

        private bool TouchVisible()
        {
            for (int i = 0; i < MaxTouches; i++)
                if (_slots[i].Used || _slots[i].Scale > 0f) return true;
            return false;
        }

        private void ResetTouches()
        {
            for (int i = 0; i < MaxTouches; i++) _slots[i] = default;
            _lastTouchTime = float.NaN;
        }

        /// <summary>
        /// 把手指精灵光栅化进 _HoverTex（1/8 网格，全屏 UV）。
        /// 等价 Phira-Pro block_shader.rs 里那段「按 bounds 裁剪 + 2×2 复制」的写入。
        /// </summary>
        private void UpdateHoverTexture(bool touchVisible)
        {
            if (_hoverTexture == null || _hoverBytes == null) return;

            if (!touchVisible)
            {
                if (_hoverHasContent)
                {
                    System.Array.Clear(_hoverBytes, 0, _hoverBytes.Length);
                    _hoverTexture.LoadRawTextureData(_hoverBytes);
                    _hoverTexture.Apply(false, false);
                    _hoverHasContent = false;
                }
                return;
            }

            // hover 网格是「屏幕 UV 的正方形格子」，所以横轴要用屏幕宽高比换算，
            // 才能让手指精灵在屏幕上看起来是圆的（官方 TouchMask::sample 同款）。
            float screenAspect = Screen.height > 0
                ? Screen.width / (float)Screen.height
                : 16f / 9f;

            System.Array.Clear(_hoverBytes, 0, _hoverBytes.Length);

            GetTouchBounds(screenAspect, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y < y1; y++)
            {
                float v = (y + 0.5f) / _hoverHeight;
                int row = y * _hoverWidth;
                for (int x = x0; x < x1; x++)
                {
                    float u = (x + 0.5f) / _hoverWidth;
                    _hoverBytes[row + x] = SampleTouch(new Vector2(u, v), screenAspect);
                }
            }

            _hoverTexture.LoadRawTextureData(_hoverBytes);
            _hoverTexture.Apply(false, false);
            _hoverHasContent = true;
        }

        /// <summary>官方 TouchMask::bounds —— 手指精灵在屏幕 UV 里的包围盒。</summary>
        private void GetTouchBounds(float screenAspect, out int x0, out int y0, out int x1, out int y1)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            bool any = false;

            for (int i = 0; i < MaxTouches; i++)
            {
                var slot = _slots[i];
                if (slot.Scale <= 0f) continue;

                float size = TouchSpriteHeight * slot.Scale * 0.5f;
                float halfX = size / Mathf.Max(screenAspect, 0.0001f);
                float halfY = size;

                minX = Mathf.Min(minX, slot.Center.x - halfX);
                maxX = Mathf.Max(maxX, slot.Center.x + halfX);
                minY = Mathf.Min(minY, slot.Center.y - halfY);
                maxY = Mathf.Max(maxY, slot.Center.y + halfY);
                any = true;
            }

            if (!any)
            {
                x0 = y0 = x1 = y1 = 0;
                return;
            }

            minX = Mathf.Max(minX, 0f); maxX = Mathf.Min(maxX, 1f);
            minY = Mathf.Max(minY, 0f); maxY = Mathf.Min(maxY, 1f);

            x0 = Mathf.Clamp(Mathf.FloorToInt(minX * _hoverWidth), 0, _hoverWidth);
            y0 = Mathf.Clamp(Mathf.FloorToInt(minY * _hoverHeight), 0, _hoverHeight);
            x1 = Mathf.Clamp(Mathf.CeilToInt(maxX * _hoverWidth), 0, _hoverWidth);
            y1 = Mathf.Clamp(Mathf.CeilToInt(maxY * _hoverHeight), 0, _hoverHeight);
        }

        /// <summary>
        /// 官方 TouchMask::sample —— 一个 hover 网格单元中心处的 R8 值。
        /// 多个手指重叠时按 Sprites/Default（One, OneMinusSrcAlpha）叠加，每次混合后重新量化。
        /// </summary>
        private byte SampleTouch(Vector2 uv, float aspect)
        {
            float result = 0f;

            for (int i = 0; i < MaxTouches; i++)
            {
                var slot = _slots[i];
                if (slot.Scale <= 0f) continue;

                float size = TouchSpriteHeight * slot.Scale;
                float su = (uv.x - slot.Center.x) * aspect / size + 0.5f;
                float sv = (uv.y - slot.Center.y) / size + 0.5f;
                if (su < 0f || su > 1f || sv < 0f || sv > 1f) continue;

                float red = SampleChannel(su, sv, 0);
                float alpha = SampleChannel(su, sv, 3);

                float blended = red * alpha + result * (1f - alpha);
                result = RoundHalfUp(Mathf.Clamp01(blended) * 255f) / 255f;
            }

            return (byte)RoundHalfUp(result * 255f);
        }

        /// <summary>
        /// TouchHover 的双线性采样。
        /// <c>.bytes</c> 已经把 PNG 上下翻转成「行 0 = v=0」，所以 y 直接按 v 取，
        /// 不再做 Rust 那边的 <c>(1-v)</c>（那是从 top-down 的 PNG 解码数组出发的）。
        /// </summary>
        private float SampleChannel(float u, float v, int channel)
        {
            var px = BlockAreaAssets.TouchPixels;
            int w = BlockAreaAssets.TouchWidth;
            int h = BlockAreaAssets.TouchHeight;
            if (px == null || w <= 0 || h <= 0) return 0f;

            float fx = u * w - 0.5f;
            float fy = v * h - 0.5f;
            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0;
            float ty = fy - y0;

            float bottom = Raw(px, w, h, x0, y0, channel) * (1f - tx) + Raw(px, w, h, x0 + 1, y0, channel) * tx;
            float top = Raw(px, w, h, x0, y0 + 1, channel) * (1f - tx) + Raw(px, w, h, x0 + 1, y0 + 1, channel) * tx;
            return bottom * (1f - ty) + top * ty;
        }

        private static float Raw(byte[] px, int w, int h, int x, int y, int channel)
        {
            if (x < 0) x = 0; else if (x >= w) x = w - 1;
            if (y < 0) y = 0; else if (y >= h) y = h - 1;
            return px[(y * w + x) * 4 + channel] / 255f;
        }

        /// <summary>
        /// Rust 的 <c>f32::round</c> 是「四舍五入、.5 远离零」，而 <c>Mathf.Round</c>
        /// 是银行家舍入（.5 取偶）。数值要对齐就必须自己实现。
        /// </summary>
        private static float RoundHalfUp(float value)
        {
            return Mathf.Floor(value + 0.5f);
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        public void Dispose()
        {
            if (_maskTexture != null) Object.Destroy(_maskTexture);
            if (_auxTexture != null) Object.Destroy(_auxTexture);
            if (_hoverTexture != null) Object.Destroy(_hoverTexture);
            _maskTexture = null;
            _auxTexture = null;
            _hoverTexture = null;

            if (_root != null) Object.Destroy(_root.gameObject);
            _root = null;
            _activeRenderer = null;
            _disabledRenderer = null;
        }
    }
}
