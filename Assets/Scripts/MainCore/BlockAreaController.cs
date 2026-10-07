using MainCore.Common;
using MainCore.Data;
using UnityEngine;

namespace MainCore
{
    /// <summary>
    /// 单块红区的运行时行为 —— 等价于官方 PreviewBlockControl
    /// （逆向自 Phigros 4.0.1 libil2cpp.so，见 code/PreviewBlockControl.decompiled.cs）。
    ///
    /// 职责：每帧按 nowTime 重算变换（缩放 → 旋转 → 移动），并对外提供命中测试。
    /// 由 <see cref="BlockAreaManager"/> 统一驱动，自身不使用 Update，以保证
    /// 「先更新块变换、再判定触摸」的顺序。
    /// </summary>
    public class BlockAreaController : MonoBehaviour
    {
        public BlockArea Info { get; private set; }

        private SpriteRenderer _renderer;
        private float _screenWidth;
        private float _screenHeight;

        // 官方调参（block-params.json / data.md）
        private const float TouchInsetScreenHeightRatio = 0.03f;
        private const float MaxTouchInsetLocal = 0.25f;
        private const float Deg2Rad = 0.017453292f;

        private static readonly Vector2 Half = new Vector2(0.5f, 0.5f);

        private static Sprite _unitSprite;

        /// <summary>单位 quad：1×1 白色贴图，pixelsPerUnit = 1，使 localScale 直接等于世界尺寸。</summary>
        private static Sprite UnitSprite
        {
            get
            {
                if (_unitSprite != null) return _unitSprite;
                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point
                };
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _unitSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), Half, 1f);
                _unitSprite.name = "BlockUnitQuad";
                return _unitSprite;
            }
        }

        public void Initialize(BlockArea info, Transform parent, float screenWidth, float screenHeight)
        {
            Info = info;
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;

            _renderer = gameObject.AddComponent<SpriteRenderer>();
            _renderer.sprite = UnitSprite;
            _renderer.drawMode = SpriteDrawMode.Simple;
            // 盖在所有判定线与音符之上
            _renderer.sortingOrder = 30000;
            _renderer.color = Color.clear;

            transform.SetParent(parent, false);
        }

        // ============ 每帧变换 ============

        /// <param name="now">谱面时间（秒），对应官方 progressControl.nowTime。</param>
        public void UpdateBlock(float now)
        {
            if (Info == null) return;

            if (!Info.IsVisible(now))
            {
                // 隐藏态：官方不切 layer，而是把块移出画面
                transform.localPosition = new Vector3(1000f, 0f, 0f);
                _renderer.color = Color.clear;
                return;
            }

            Vector2 screen = new Vector2(_screenWidth, _screenHeight);

            // ---- UpdateBlocksTransform：基础矩形 ----
            Vector2 trW = (Info.topRightPercentage - Half) * screen;
            Vector2 blW = (Info.bottomLeftPercentage - Half) * screen;
            Vector2 baseCenter = (trW + blW) * 0.5f;
            // 注意：官方此处是有符号差（size = trW - blW），绝对值只用于缩放显示。
            // topRight / bottomLeft 的 y 允许反向（实测谱面里就有），不能假设方向。
            Vector2 baseSize = trW - blW;

            // ---- UpdateBlockAnimations：缩放 → 旋转 → 移动 ----
            var scaled = UpdateScale(baseSize, baseCenter, now);
            var rotated = UpdateRotation(scaled.center, now);
            Vector2 pos = UpdateMovement(baseCenter, rotated.center, now);

            transform.localPosition = new Vector3(pos.x, pos.y, 0f);
            transform.localScale = new Vector3(Mathf.Abs(scaled.size.x), Mathf.Abs(scaled.size.y), 1f);
            transform.localEulerAngles = new Vector3(0f, 0f, rotated.rotation);

            // 官方 anchor 恒为 (0.5, 0.5)，这里同样固定，故不需要额外锚点世界坐标。
            _renderer.color = GetPhaseColor(now);
        }

        private Color GetPhaseColor(float now)
        {
            // 官方的 Disabled / Ready 由协程 + 着色器表现；这里用透明度近似，够用且无副作用。
            // 减块官方恒为 0.1 alpha（SubtractAlpha，.rodata 0xC261F0）。
            if (Info.isSubtract)
                return new Color(1f, 0.35f, 0.9f, 0.1f);

            bool active = Info.IsActive(now);
            if (active) return new Color(1f, 0.18f, 0.28f, 0.55f);

            // 生效前的预警期：半天
            float readyWindow = 0.5f;
            bool ready = now >= Info.enableTime - readyWindow && now < Info.enableTime;
            return ready
                ? new Color(1f, 0.18f, 0.28f, 0.3f)
                : new Color(0.55f, 0.1f, 0.16f, 0.18f);
        }

        // ============ 命中测试 ============

        /// <summary>是否可被触摸：仅 Active 阶段参与判定，Disabled / Ready 纯为视觉预警。</summary>
        public bool IsActive(float now) => Info != null && Info.IsActive(now);

        /// <summary>
        /// 世界坐标是否落在本块触摸区内。等价于官方 JudgeControl.IsPositionInsideBlock。
        /// 在局部空间做轴对齐包围盒测试，因此旋转与缩放自动生效。
        /// </summary>
        public bool IsPositionInside(Vector2 worldPosition)
        {
            if (!TryGetTouchHalfSize(out var half)) return false;
            Vector3 local = transform.InverseTransformPoint(new Vector3(worldPosition.x, worldPosition.y, 0f));
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y;
        }

        /// <summary>等价官方 TryGetBlockTouchHalfSize：普通块外扩、减块内缩。</summary>
        private bool TryGetTouchHalfSize(out Vector2 halfSize)
        {
            halfSize = Vector2.zero;
            Vector3 lossy = transform.lossyScale;
            // 退化矩阵保护：任一分量的绝对值小于 1e-4 直接判定失败
            if (Mathf.Abs(lossy.x) < 1e-4f || Mathf.Abs(lossy.y) < 1e-4f) return false;

            float insetWorld = TouchInsetScreenHeightRatio * Mathf.Max(_screenHeight, 0f);
            float ix = Mathf.Min(Mathf.Max(insetWorld / lossy.x, 0f), MaxTouchInsetLocal);
            float iy = Mathf.Min(Mathf.Max(insetWorld / lossy.y, 0f), MaxTouchInsetLocal);

            float sgn = Info.isSubtract ? -1f : 1f;
            halfSize = new Vector2(ix * sgn + 0.5f, iy * sgn + 0.5f);
            return true;
        }

        // ============ 缩放 / 旋转 / 移动 ============

        private (Vector2 size, Vector2 center) UpdateScale(Vector2 originalSize, Vector2 startCenter, float now)
        {
            var ev = Info.scaleEvents;
            if (ev == null || ev.Count == 0) return (originalSize, startCenter);

            int index = FindCurrentEventIndex(ev, e => e.time, now);
            if (index == -1) return (originalSize, startCenter);

            Vector2 screen = new Vector2(_screenWidth, _screenHeight);
            Vector2 p = startCenter;

            // 累积 index 之前每一对事件的缩放比（绕各自的锚点）
            for (int i = 0; i < index; i++)
            {
                BlockScaleEvent e0 = ev[i], e1 = ev[i + 1];
                Vector2 a = (e0.anchor - Half) * screen;
                p = ScaleAroundAnchor(p, a, SafeDiv(e1.scale.x, e0.scale.x), SafeDiv(e1.scale.y, e0.scale.y));
            }

            Vector2 size;
            if (index >= ev.Count - 1)
            {
                size = new Vector2(ev[index].scale.x * originalSize.x, ev[index].scale.y * originalSize.y);
            }
            else
            {
                BlockScaleEvent cur = ev[index], next = ev[index + 1];
                float tx = Mathf.Clamp01(EasedProgress(cur.time, next.time, cur.easeTypeX, now));
                float ty = Mathf.Clamp01(EasedProgress(cur.time, next.time, cur.easeTypeY, now));
                Vector2 interp = new Vector2(
                    Mathf.Lerp(cur.scale.x, next.scale.x, tx),
                    Mathf.Lerp(cur.scale.y, next.scale.y, ty));
                Vector2 a = (cur.anchor - Half) * screen;
                p = ScaleAroundAnchor(p, a, SafeDiv(interp.x, cur.scale.x), SafeDiv(interp.y, cur.scale.y));
                size = new Vector2(interp.x * originalSize.x, interp.y * originalSize.y);
            }

            return (size, p);
        }

        private (float rotation, Vector2 center) UpdateRotation(Vector2 startCenter, float now)
        {
            var ev = Info.rotateEvents;
            if (ev == null || ev.Count == 0) return (0f, startCenter);

            int index = FindCurrentEventIndex(ev, e => e.time, now);
            if (index == -1) return (0f, startCenter);

            Vector2 screen = new Vector2(_screenWidth, _screenHeight);
            Vector2 p = startCenter;

            // 累积 index 之前每个事件的 Δrotation
            for (int i = 0; i < index; i++)
            {
                BlockRotateEvent e0 = ev[i], e1 = ev[i + 1];
                Vector2 a = (e0.anchor - Half) * screen;
                p = RotateAroundAnchor(p, a, e1.rotation - e0.rotation);
            }

            float rotation;
            if (index >= ev.Count - 1)
            {
                rotation = ev[index].rotation;
            }
            else
            {
                BlockRotateEvent cur = ev[index], next = ev[index + 1];
                float t = Mathf.Clamp01(EasedProgress(cur.time, next.time, cur.easeType, now));
                rotation = Mathf.Lerp(cur.rotation, next.rotation, t);
                Vector2 a = (cur.anchor - Half) * screen;
                p = RotateAroundAnchor(p, a, rotation - cur.rotation);
            }

            return (rotation, p);
        }

        private Vector2 UpdateMovement(Vector2 originalCenter, Vector2 currentCenter, float now)
        {
            var ev = Info.moveEvents;
            if (ev == null || ev.Count < 1) return currentCenter;

            int i = FindCurrentEventIndex(ev, e => e.time, now);
            if (i == -1) return currentCenter;

            // 移动是「增量偏移」：当前中心 += 目标中心 − 原始中心
            currentCenter += InterpolateMoveEvent(i, now) - originalCenter;
            return currentCenter;
        }

        private Vector2 InterpolateMoveEvent(int index, float now)
        {
            var ev = Info.moveEvents;
            if (ev.Count - 1 <= index) return AnchorToWorld(ev[index].endPosition);

            BlockMoveEvent cur = ev[index], next = ev[index + 1];
            float tx = Mathf.Clamp01(EasedProgress(cur.time, next.time, cur.easeTypeX, now));
            float ty = Mathf.Clamp01(EasedProgress(cur.time, next.time, cur.easeTypeY, now));
            return AnchorToWorld(new Vector2(
                Mathf.Lerp(cur.endPosition.x, next.endPosition.x, tx),
                Mathf.Lerp(cur.endPosition.y, next.endPosition.y, ty)));
        }

        // ============ 原语 ============

        private Vector2 AnchorToWorld(Vector2 anchor) =>
            new Vector2((anchor.x - 0.5f) * _screenWidth, (anchor.y - 0.5f) * _screenHeight);

        private static Vector2 ScaleAroundAnchor(Vector2 point, Vector2 anchor, float stepX, float stepY) =>
            new Vector2(anchor.x + (point.x - anchor.x) * stepX,
                        anchor.y + (point.y - anchor.y) * stepY);

        /// <summary>标准逆时针旋转（逆时针为正，与 RotateEvent.rotation 约定一致）。δ=90° 时 (1,0)→(0,1)。</summary>
        private static Vector2 RotateAroundAnchor(Vector2 point, Vector2 anchor, float deltaDeg)
        {
            float rad = deltaDeg * Deg2Rad;
            float sin = Mathf.Sin(rad), cos = Mathf.Cos(rad);
            float dx = point.x - anchor.x;
            float dy = point.y - anchor.y;
            return new Vector2(anchor.x + (dx * cos - dy * sin),
                               anchor.y + (dx * sin + dy * cos));
        }

        /// <summary>分母趋零时返回 1（不做缩放），而非 0 —— 关键帧重合处才不会跳变。</summary>
        private static float SafeDiv(float numerator, float denominator)
        {
            float threshold = Mathf.Max(Mathf.Abs(denominator) * 1e-6f, 8f * Mathf.Epsilon);
            return Mathf.Abs(denominator) < threshold ? 1f : numerator / denominator;
        }

        private float EasedProgress(float currentTime, float nextTime, int easeType, float now)
        {
            float span = nextTime - currentTime;
            // 官方此处是裸除法，相邻事件时间相等会算出 Inf/NaN。
            // 文档建议显式处理：相等时直接取 next 的值（等价于 progress = 1）。
            if (Mathf.Abs(span) < 1e-6f) return BlockEase.GetEaseWithProgress(1f, easeType);
            return BlockEase.GetEaseWithProgress((now - currentTime) / span, easeType);
        }

        /// <summary>
        /// 等价官方 FindCurrentEventIndex：返回「首个 time &gt; now 的事件索引 − 1」，范围 [-1, Count-2]。
        /// 时间戳相等时选最靠后的那个（循环用严格大于）。
        /// </summary>
        private static int FindCurrentEventIndex<T>(System.Collections.Generic.List<T> events,
            System.Func<T, float> getTime, float now)
        {
            if (events == null || events.Count < 1) return -1;

            int i = -1;
            while (true)
            {
                if (getTime(events[i + 1]) > now) return i;
                i++;
                if (i + 2 >= events.Count) return i;
            }
        }
    }
}
