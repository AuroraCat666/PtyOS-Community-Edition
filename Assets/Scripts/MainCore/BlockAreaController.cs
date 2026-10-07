using MainCore.Common;
using MainCore.Data;
using UnityEngine;

namespace MainCore
{
    /// <summary>
    /// 单块红区的运行时解算 —— 等价于官方 PreviewBlockControl 的位姿部分
    /// （逆向自 Phigros 4.0.1 libil2cpp.so，见 code/PreviewBlockControl.decompiled.cs）。
    ///
    /// 职责：
    ///   1. 每帧按 nowTime 重算变换（缩放 → 旋转 → 移动），产出 chart space 的
    ///      <see cref="Zone"/> 交给噪域绘制层；
    ///   2. 对外提供命中测试（判定层「断触」要用）。
    ///
    /// 由 <see cref="BlockAreaManager"/> 统一驱动，自身不使用 Update，以保证
    /// 「先更新块变换、再判定触摸」的顺序。
    ///
    /// 视觉不再由本类负责 —— 红区形状是「普通块 XOR 减块」，一串矩形画不出来，
    /// 现在由 <see cref="BlockAreaMaskBuilder"/> 在 CPU 上光栅化成 mask，
    /// 再由 BlockAreaActive / BlockAreaDisabled 两个全屏 Pass 合成。
    /// 本类仍然把结果写进 Transform，因为 Contains 的命中测试复用它
    /// （用 lossyScale 检查退化、用 InverseTransformPoint 做逆变换）。
    /// </summary>
    public class BlockAreaController : MonoBehaviour
    {
        public BlockArea Info { get; private set; }

        /// <summary>在谱面 blockAreaList 中的下标，仅用于日志。</summary>
        public int Index { get; set; }

        /// <summary>本帧解算出的 chart space 几何与相位。调用 <see cref="UpdateBlock"/> 后有效。</summary>
        public BlockAreaZone Zone { get; private set; }

        private float _screenWidth;
        private float _screenHeight;
        private float _localZ;
        private bool _loggedFirstVisible;

        // 官方调参。取值来自 Phira-Pro（prpr/src/core/block.rs）对官方
        // JudgeControl 常量的交叉标注：
        //   maxBlockTouchInsetLocal          = 0.05 → 乘屏幕高，得到世界空间内缩量
        //     （在 BlockAreaManager.TouchInsetWorld() 里换算）
        //   blockTouchInsetScreenHeightRatio = 0.25 → 局部空间内缩量的上限（本文件用）
        private const float MaxTouchInsetLocal = 0.25f;
        private const float Deg2Rad = 0.017453292f;

        private static readonly Vector2 Half = new Vector2(0.5f, 0.5f);

        /// <summary>块所在的局部 z（父物体为 Game Root）。等于判定线/音符所在的平面。</summary>
        public float LocalZ => _localZ;

        public void Initialize(BlockArea info, Transform parent, float screenWidth, float screenHeight, float localZ)
        {
            Info = info;
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;
            _localZ = localZ;

            // worldPositionStays = false：块的局部坐标由我们完全接管，
            // z 用调用方算好的 localZ（见 BlockAreaManager.Create 的说明）。
            transform.SetParent(parent, false);
            transform.localScale = Vector3.zero;
            transform.localPosition = new Vector3(0f, 0f, _localZ);
        }

        /// <summary>
        /// 每块只打一次「首次可见」日志。
        /// 红区最容易出现的误判是「红区根本没做」——有这条日志就能一眼分清
        /// 「代码没跑」还是「跑了但被别的东西盖住 / 位置算错了」。
        /// </summary>
        private void LogFirstVisible(float now, Vector2 pos, Vector2 size)
        {
            if (_loggedFirstVisible) return;
            _loggedFirstVisible = true;
            Debug.Log($"[BlockArea] 块 #{Index}{(Info.isSubtract ? "（减块）" : string.Empty)} " +
                      $"首次可见 @{now:F2}s 中心=({pos.x:F2},{pos.y:F2}) " +
                      $"尺寸=({Mathf.Abs(size.x):F2},{Mathf.Abs(size.y):F2}) " +
                      $"旋转={transform.localEulerAngles.z:F1}° localZ={_localZ:F1} 世界z={transform.position.z:F1} " +
                      $"chart 中心=({Zone.Center.x:F3},{Zone.Center.y:F3}) " +
                      $"半宽高=({Zone.Half.x:F3},{Zone.Half.y:F3}) 生效={Zone.Active} 预警={Zone.Ready}");
        }

        // ============ 每帧变换 ============

        /// <summary>由管理器每帧下发最新游戏区尺寸（分辨率可能变化）。</summary>
        public void SetScreenSize(float screenWidth, float screenHeight)
        {
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;
        }

        /// <summary>
        /// 由管理器下发最新的局部 z。
        ///
        /// 必须支持中途变更：判定线第一帧跑完才把 JudgeLineTopTransform 挪到
        /// 世界 z = 0，在那之前管理器只能用相机正前方的安全平面兜底。
        /// 隐藏态下也要顺手把 Transform 挪过去，否则它会一直停在旧的 z 上。
        /// </summary>
        public void SetLocalZ(float localZ)
        {
            if (Mathf.Approximately(_localZ, localZ)) return;
            _localZ = localZ;

            var p = transform.localPosition;
            transform.localPosition = new Vector3(p.x, p.y, localZ);
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <param name="now">谱面时间（秒），对应官方 progressControl.nowTime。</param>
        public void UpdateBlock(float now)
        {
            if (Info == null) return;

            if (!Info.IsVisible(now))
            {
                // 隐藏：Transform 退化，Contains 的 lossyScale 检查直接失败，
                // 即使有别的代码绕过 IsVisible 分支也不会误判命中。
                transform.localScale = Vector3.zero;
                transform.localPosition = new Vector3(0f, 0f, _localZ);
                Zone = default;
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

            // 最后一道兜底：任一步算出 NaN/Inf 会让 Transform 每帧刷屏报错。
            // 这里直接跳过本帧赋值、保留上一帧姿态，宁可静止也不污染场景。
            if (!IsFinite(pos.x) || !IsFinite(pos.y) ||
                !IsFinite(scaled.size.x) || !IsFinite(scaled.size.y) ||
                !IsFinite(rotated.rotation))
                return;

            // z 用 _localZ：世界 z = 0，与判定线/音符同一平面。
            // 正交相机下 z 不改变屏幕位置，只决定是否落在视锥内 ——
            // 用 0 会掉进相机近裁剪面（near=0.3）内侧被整块裁掉。
            transform.localPosition = new Vector3(pos.x, pos.y, _localZ);
            transform.localScale = new Vector3(Mathf.Abs(scaled.size.x), Mathf.Abs(scaled.size.y), 1f);
            transform.localEulerAngles = new Vector3(0f, 0f, rotated.rotation);

            Zone = MakeZone(pos, scaled.size, rotated.rotation, now);
            LogFirstVisible(now, pos, scaled.size);
        }

        /// <summary>
        /// 世界空间 → chart space。
        ///
        /// chart space（官方 / Phira-Pro 的口径）：
        ///   x ∈ [-1, 1]，y ∈ [-1/aspect, 1/aspect]，以游戏区中心为原点、y 向上。
        /// 由于游戏区高 = 世界高、宽 = 高 × aspect，两个轴其实是同一个缩放：
        ///   chart = world * 2 / screenWidth
        /// 校验：world.x = ±screenWidth/2 → chart.x = ±1 ✓
        ///       world.y = ±screenHeight/2 → chart.y = ±screenHeight/screenWidth = ±1/aspect ✓
        /// </summary>
        private BlockAreaZone MakeZone(Vector2 worldCenter, Vector2 worldSize, float rotationDeg, float now)
        {
            float chartScale = _screenWidth > 0f ? 2f / _screenWidth : 0f;

            bool active = Info.IsActive(now);
            // Ready：生效前 0.5 秒的预警。官方 Zone::from_area 同款判定。
            bool ready = !active && now < Info.enableTime && now >= Info.enableTime - 0.5f;
            // Opacity：官方 Initial DisabledBlockShow 用 0.5 秒把 SpriteMask 淡入。
            // 只有在「未生效」且「出现时刻本身就不是生效时刻」时才需要淡入。
            bool fadesIn = !Info.IsActive(Info.appearTime);
            float opacity = (!active && fadesIn)
                ? Mathf.Clamp01((now - Info.appearTime) / 0.5f)
                : 1f;

            Vector2 half = new Vector2(Mathf.Abs(worldSize.x), Mathf.Abs(worldSize.y)) * chartScale * 0.5f;

            return new BlockAreaZone
            {
                Center = worldCenter * chartScale,
                Half = half,
                Angle = rotationDeg * Deg2Rad,
                Invert = Info.isSubtract,
                Active = active,
                Ready = ready,
                Opacity = opacity,
                // 官方 Zone::from_area 在任一半轴为 0 时直接返回 None。
                Valid = half.x > 0f && half.y > 0f,
            };
        }

        // ============ 命中测试 ============

        /// <summary>是否可被触摸：仅 Active 阶段参与判定，Disabled / Ready 纯为视觉预警。</summary>
        public bool IsActive(float now) => Info != null && Info.IsActive(now);

        /// <summary>
        /// 是否为减块。注意语义：减块**不是**「永不阻断」，
        /// 它是奇偶规则里的「取反区」—— 单独存在时阻断，盖住普通块时把普通块变成洞。
        /// 详见 <see cref="BlockAreaManager.IsBlockedAt"/>。
        /// </summary>
        public bool IsSubtract => Info != null && Info.isSubtract;

        /// <summary>
        /// 世界坐标是否落在本块（含内缩）内。等价官方 JudgeControl 的 contains：
        /// 把点逆变换到以块中心为原点、边长为 1 的局部空间做轴对齐 AABB，
        /// 所以旋转与缩放自动生效。
        /// </summary>
        /// <param name="insetWorld">世界空间内缩量；0 表示用整块矩形。</param>
        public bool Contains(Vector2 worldPosition, float now, float insetWorld)
        {
            if (Info == null || !Info.IsVisible(now)) return false;
            if (!TryGetTouchHalfSize(insetWorld, out var half)) return false;

            // 触摸点用与块同平面的 z，保证局部 z 恒为 0，只在 XY 上做 AABB。
            Vector3 local = transform.InverseTransformPoint(
                new Vector3(worldPosition.x, worldPosition.y, transform.position.z));
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y;
        }

        /// <summary>
        /// 等价官方 TryGetBlockTouchHalfSize。
        /// 关键：sign = isSubtract ? +1 : -1 —— 普通块**内缩**、减块**外扩**，
        /// 两者都朝「洞」的方向偏，边界上的触摸判定才一致。
        /// </summary>
        private bool TryGetTouchHalfSize(float insetWorld, out Vector2 halfSize)
        {
            halfSize = Vector2.zero;
            Vector3 lossy = transform.lossyScale;
            // 退化矩阵保护：任一分量的绝对值小于 1e-4 直接判定失败
            if (Mathf.Abs(lossy.x) < 1e-4f || Mathf.Abs(lossy.y) < 1e-4f) return false;

            float sign = Info.isSubtract ? 1f : -1f;
            halfSize = new Vector2(
                0.5f + sign * InsetLocal(Mathf.Abs(lossy.x), insetWorld),
                0.5f + sign * InsetLocal(Mathf.Abs(lossy.y), insetWorld));
            return halfSize.x > 0f && halfSize.y > 0f;
        }

        /// <summary>把一个轴上的世界内缩量换算成局部半宽/半高的缩减量（带上限）。</summary>
        private static float InsetLocal(float size, float insetWorld)
        {
            // 尺寸退化时官方直接取上限（Phira-Pro inset_local 同款处理）
            if (size < 1e-6f) return MaxTouchInsetLocal;
            return Mathf.Clamp(Mathf.Abs(insetWorld / size), 0f, MaxTouchInsetLocal);
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
