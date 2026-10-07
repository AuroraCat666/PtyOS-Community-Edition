using System.Collections;
using System.Collections.Generic;
using MainCore.Data;
using UnityEngine;

namespace MainCore
{
    /// <summary>
    /// 红区（BlockArea）管理器 —— 对应官方 JudgeControl 中与块相关的一半职责。
    ///
    /// 官方流程（JudgeControl.Update）：
    ///   CheckBlocks()            → 扫描所有手指，命中块的 fingerId 进 blockedFingerIds
    ///   CheckNote / CheckFlick   → 跳过 blockedFingerIds 里的手指（这就是「断触」）
    ///   ProcessBlockedTouches()  → 收集被阻断的触摸位置，送着色器
    ///   UpdateLowPassFilterState → 按住任意 Active 块时压低 BGM
    ///
    /// 本类负责创建块、每帧驱动变换、对外提供命中查询与断触状态。
    /// </summary>
    public class BlockAreaManager : MonoBehaviour
    {
        public static BlockAreaManager Instance { get; private set; }

        // 低通滤波参数（官方 ProgressControl 常量）
        private const float UnfilteredCutoffFrequency = 22000f;
        private const float LowPassCutoffFrequency = 1500f;
        private const float LowPassLerpDuration = 0.25f;

        /// <summary>
        /// 官方 JudgeControl.maxBlockTouchInsetLocal。
        /// 乘屏幕高得到世界空间内缩量；局部空间的上限见 BlockAreaController。
        /// </summary>
        private const float TouchInsetScreenHeightRatio = 0.05f;

        private readonly List<BlockAreaController> _blocks = new List<BlockAreaController>();
        private readonly HashSet<int> _blockedFingers = new HashSet<int>();

        /// <summary>全屏层相对游戏区的外扩，避免边缘留出 1 像素缝。</summary>
        private const float FullscreenMargin = 1.05f;

        /// <summary>
        /// 游戏内容（判定线 / 音符 / 红区）所在的世界 z 平面 —— 硬事实，不是拍脑袋取的。
        ///
        /// 相机 Main Camera 在世界 **z = -10**（父 [Cameras] 在 0，自身 localZ = -10），
        /// near = 0.3、far = 1000；Game Root 也在世界 **z = -10**。
        /// 所以「世界 z = -10」的物体会正好落在相机平面上，被近裁剪面整块裁掉，
        /// 一个像素都不渲染 —— 而这正是块原来的 z（SetParent(parent, false) 后
        /// local z 停在 0 → 世界 z = -10）。红区「完全不显示」就是这么来的。
        ///
        /// 判定线把 JudgeLineTopTransform.position 直接写成 GameUtils.GetTransformedXY(...)
        /// 的结果，而 MoveEventValue 是 **Vector2**（赋给 Vector3 时 z 隐式为 0）；
        /// 音符又是 JudgeLineTopTransform 的子物体。所以游戏内容全都在 **世界 z = 0**。
        /// </summary>
        public const float ContentWorldZ = 0f;

        private int _lastFrame = -1;
        private float _screenWidth;
        private float _screenHeight;
        private float _blockLocalZ;
        private float _syncedWidth = -1f;
        private float _syncedHeight = -1f;

        /// <summary>块的父物体（Game Root）。每帧要把世界 z 换算成它的局部 z。</summary>
        private Transform _parent;

        /// <summary>stencil 清零层（全屏，bit0 = 0）。</summary>
        private SpriteRenderer _stencilClearRenderer;
        /// <summary>显示层（全屏，bit0 == 1 处画红）。</summary>
        private SpriteRenderer _fillRenderer;

        private AudioSource _musicSource;
        private AudioLowPassFilter _lowPass;
        private bool _wasTouchingAnyBlock;
        private Coroutine _sweep;

        public bool HasBlocks => _blocks.Count > 0;

        /// <summary>本帧是否有任一手指正按在 Active 块上。</summary>
        public bool IsTouchingAnyBlock { get; private set; }

        // ============ 创建 ============

        /// <summary>按谱面创建红区。没有 blockAreaList 时不做任何事。</summary>
        public static BlockAreaManager Create(Chart chart, Transform parent)
        {
            if (chart == null || !chart.HasBlockArea) return null;

            var go = new GameObject("[BlockArea]");
            var manager = go.AddComponent<BlockAreaManager>();

            manager.RefreshScreenSize();
            manager._parent = parent;

            // ===== 决定块所在的 z 平面 =====
            // 不能直接沿用父物体的局部 z（那样块会停在世界 z = -10，
            // 与相机同平面、被近裁剪面整块裁掉）。见 ContentWorldZ 的说明。
            float worldZ = ResolveWorldZ();
            float blockLocalZ = ResolveBlockLocalZ(parent, worldZ);
            manager._blockLocalZ = blockLocalZ;

            for (int i = 0; i < chart.blockAreaList.Count; i++)
            {
                var area = chart.blockAreaList[i];
                var blockGo = new GameObject(area.isSubtract ? $"SubtractBlock{i}" : $"Block{i}");
                var controller = blockGo.AddComponent<BlockAreaController>();
                controller.Index = i;
                controller.Initialize(area, parent, manager._screenWidth, manager._screenHeight, blockLocalZ);
                manager._blocks.Add(controller);
            }

            manager.CreateFullscreenLayers(parent, blockLocalZ);
            manager.SyncFullscreenLayers();
            manager.LogSchedule();

            return manager;
        }

        // ============ 全屏层 ============
        //
        // 红区的形状是「普通块 XOR 减块」，没办法用一串矩形直接画出来
        // （减块盖住普通块时，普通块自己矩形里有一块要被挖空）。
        // 所以走 stencil：
        //   1. StencilClear  全屏，把 bit0 抹成 0
        //   2. 每个 Active 块写 bit0（普通块 Replace 1 / 减块 Invert）
        //   3. Fill          全屏，只在 bit0 == 1 处画红
        // 三步都在同一个 sorting layer 内、靠 sortingOrder 定先后。

        private void CreateFullscreenLayers(Transform parent, float localZ)
        {
            _stencilClearRenderer = CreateFullscreenRenderer(
                "BlockAreaStencilClear", parent, localZ,
                BlockAreaMaterials.StencilClear,
                BlockAreaMaterials.OrderStencilClear,
                Color.white);

            _fillRenderer = CreateFullscreenRenderer(
                "BlockAreaFill", parent, localZ,
                BlockAreaMaterials.Fill,
                BlockAreaMaterials.OrderFill,
                BlockAreaMaterials.ActiveFill);

            if (!BlockAreaMaterials.Ready)
            {
                // 着色器没取到就整条 stencil 管线都不可用：
                // 关掉两个全屏层，让每一块退回「直接画淡红」，
                // 至少红区还能看见，而不是整块消失。
                // 注意这里只是「当前」状态，真正的开关每帧由
                // SyncFullscreenLayersEnabled 同步，避免创建时判一次就定终身。
                if (_stencilClearRenderer != null) _stencilClearRenderer.enabled = false;
                if (_fillRenderer != null) _fillRenderer.enabled = false;
            }
        }

        /// <summary>
        /// 每帧同步全屏 stencil 层的开关。
        /// 原来只在 Create 时判一次 BlockAreaMaterials.Ready，一旦那一刻取不到
        /// 着色器，全屏层就被永久关掉；但块自己的渲染器是每帧重新判断的，
        /// 于是会出现「块已经切成只写 stencil 的材质（ColorMask 0，本来就看不见），
        /// 全屏显示层却还是关的」—— 屏幕上一个像素都不亮。
        /// </summary>
        private void SyncFullscreenLayersEnabled()
        {
            bool ready = BlockAreaMaterials.Ready;
            if (_stencilClearRenderer != null && _stencilClearRenderer.enabled != ready)
                _stencilClearRenderer.enabled = ready;
            if (_fillRenderer != null && _fillRenderer.enabled != ready)
                _fillRenderer.enabled = ready;
        }

        private static SpriteRenderer CreateFullscreenRenderer(string name, Transform parent, float localZ,
            Material material, int order, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, localZ);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = BlockAreaController.UnitSprite;
            renderer.drawMode = SpriteDrawMode.Simple;
            renderer.sharedMaterial = material != null ? material : renderer.sharedMaterial;

            string layer = ResolveTopSortingLayer();
            if (!string.IsNullOrEmpty(layer)) renderer.sortingLayerName = layer;

            renderer.sortingOrder = order;
            renderer.color = color;
            return renderer;
        }

        /// <summary>把两个全屏层铺满整个游戏区。分辨率变化时重算。</summary>
        private void SyncFullscreenLayers()
        {
            if (Mathf.Approximately(_syncedWidth, _screenWidth) &&
                Mathf.Approximately(_syncedHeight, _screenHeight))
                return;

            _syncedWidth = _screenWidth;
            _syncedHeight = _screenHeight;

            var scale = new Vector3(_screenWidth * FullscreenMargin, _screenHeight * FullscreenMargin, 1f);
            var position = new Vector3(0f, 0f, _blockLocalZ);

            if (_stencilClearRenderer != null)
            {
                _stencilClearRenderer.transform.localScale = scale;
                _stencilClearRenderer.transform.localPosition = position;
            }

            if (_fillRenderer != null)
            {
                _fillRenderer.transform.localScale = scale;
                _fillRenderer.transform.localPosition = position;
            }
        }

        /// <summary>取项目里最靠上的排序层（AboveNotes 优先），红区要盖住音符。</summary>
        private static string ResolveTopSortingLayer()
        {
            var layers = SortingLayer.layers;
            if (layers == null || layers.Length == 0) return null;

            foreach (var l in layers)
                if (l.name == "AboveNotes") return l.name;

            SortingLayer best = layers[0];
            foreach (var l in layers)
                if (l.value > best.value) best = l;

            return best.name;
        }

        /// <summary>
        /// 求「游戏内容所在的世界 z 平面」。
        ///
        /// 主源是判定线的实际平面（音符是它的子物体，跟着它走）；
        /// 但必须用相机视锥验一下：Create 发生在谱面刚加载完、判定线还没跑过
        /// 第一帧的时候，那时 JudgeLineTopTransform 还停在预制体的 z 上，
        /// 直接采信会把块也放到相机平面上（就是这次「红区不显示」的原因）。
        /// </summary>
        private static float ResolveWorldZ()
        {
            float worldZ = ContentWorldZ;

            var lines = GlobalSetting.Lines;
            if (lines != null && lines.Count > 0)
            {
                var line = lines[0];
                if (line != null && line.JudgeLineTopTransform != null)
                    worldZ = line.JudgeLineTopTransform.position.z;
            }

            return SanitizeWorldZ(worldZ);
        }

        /// <summary>
        /// 把 z 拉回相机看得见的范围。
        /// 用 WorldToViewportPoint 的 z（沿相机朝向的深度）判断 —— 正交与透视都适用，
        /// 也不用假设相机朝 +z 还是 -z。够不着就退回相机正前方 5 个单位处，
        /// 那里必定落在 near 与 far 之间。
        /// </summary>
        private static float SanitizeWorldZ(float worldZ)
        {
            var cam = Camera.main;
            if (cam == null) return worldZ;

            float depth = cam.WorldToViewportPoint(new Vector3(0f, 0f, worldZ)).z;
            if (depth > cam.nearClipPlane && depth < cam.farClipPlane) return worldZ;

            var safe = cam.transform.position + cam.transform.forward * (cam.nearClipPlane + 5f);
            return safe.z;
        }

        /// <summary>把「世界 z 平面」换算成块相对父物体的局部 z。</summary>
        private static float ResolveBlockLocalZ(Transform parent, float worldZ)
        {
            if (parent == null) return worldZ;
            return parent.InverseTransformPoint(new Vector3(0f, 0f, worldZ)).z;
        }

        /// <summary>当前的局部 z 对应的世界 z，仅用于日志核对。</summary>
        private float WorldZOf(float localZ)
        {
            if (_parent == null) return localZ;
            return _parent.TransformPoint(new Vector3(0f, 0f, localZ)).z;
        }

        /// <summary>
        /// 打一条可对照的日志：本谱面共多少块、第一块在第几秒出现。
        /// 红区整段前奏都可能不出现，玩家从头播会觉得「红区根本没做」，
        /// 这条日志能直接排除这种误判。
        /// </summary>
        private void LogSchedule()
        {
            int normal = 0, subtract = 0;
            float firstAppear = float.MaxValue;
            foreach (var b in _blocks)
            {
                if (b == null || b.Info == null) continue;
                if (b.IsSubtract) subtract++;
                else normal++;
                if (b.Info.appearTime < firstAppear) firstAppear = b.Info.appearTime;
            }

            string first = _blocks.Count > 0 ? firstAppear.ToString("F2") + "s" : "无";

            var cam = Camera.main;
            string camInfo = cam == null
                ? "无 Main Camera"
                : $"相机 世界z={cam.transform.position.z:F1} near={cam.nearClipPlane:F2} " +
                  $"far={cam.farClipPlane:F0} 正交={cam.orthographic}";

            string fillInfo = _fillRenderer == null
                ? "无显示层"
                : $"显示层 世界z={_fillRenderer.transform.position.z:F1} " +
                  $"尺寸={_fillRenderer.transform.localScale.x:F1}x{_fillRenderer.transform.localScale.y:F1} " +
                  $"材质={(_fillRenderer.sharedMaterial != null ? _fillRenderer.sharedMaterial.shader.name : "无")}";

            Debug.Log($"[BlockArea] 共 {_blocks.Count} 块（普通 {normal} / 减块 {subtract}），" +
                      $"第一块出现于 {first}，块局部 z={_blockLocalZ:F1}（世界 z={WorldZOf(_blockLocalZ):F1}），" +
                      $"游戏区 {_screenWidth:F2}x{_screenHeight:F2}，{camInfo}，" +
                      $"stencil 管线={(BlockAreaMaterials.Ready ? "就绪" : "着色器缺失，已退化为矩形绘制")}，" +
                      fillInfo);
        }

        private void Awake()
        {
            Instance = this;
        }

        /// <summary>
        /// 重算游戏区尺寸。分辨率会变、且 GlobalSetting 平时在 Update 里刷新，
        /// 所以每帧都要重取一次，不能只在 Create 时算一次。
        /// </summary>
        private void RefreshScreenSize()
        {
            if (_screenHeight <= 0f)
                _screenHeight = 2f * (Camera.main != null ? Camera.main.orthographicSize : 5f);

            float h = _screenHeight;

            // 与判定线同一套坐标口径：用 GlobalSetting.Aspect（已按宽屏遮罩修正），
            // 不能直接用 Camera.main.aspect，否则在 21:9 等宽屏上块会横向错位。
            float aspect = GlobalSetting.Aspect;

            // 兜底：GlobalSetting 还没初始化时 Aspect 是 0/0 = NaN。
            // 一旦用到 NaN，块的变换会全部变成 NaN 并每帧刷屏报错。
            if (!IsFinite(aspect) || aspect <= 0f)
            {
                aspect = Screen.height > 0 ? Screen.width * 1f / Screen.height : 16f / 9f;
                if (!IsFinite(aspect) || aspect <= 0f) aspect = 16f / 9f;
            }

            _screenHeight = h;
            _screenWidth = h * aspect;
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_sweep != null) StopCoroutine(_sweep);
        }

        // ============ 每帧驱动 ============

        private void Update()
        {
            EnsureUpdated(CurrentTime);
        }

        private static float CurrentTime =>
            Main.Instance != null && Main.Instance.progressManager != null
                ? Main.Instance.progressManager.NowTime
                : 0f;

        /// <summary>
        /// 幂等更新：同一帧只更新一次。
        /// 判定核心会在判定前调用它，确保「先算变换、再判触摸」，不受脚本执行顺序影响。
        /// </summary>
        public void EnsureUpdated(float now)
        {
            if (Time.frameCount == _lastFrame) return;
            _lastFrame = Time.frameCount;

            // 分辨率会变（窗口缩放、宽屏遮罩），每帧重算一次游戏区尺寸，
            // 否则块会按旧的分辨率算位置，在窗口化时整体错位。
            RefreshScreenSize();

            // z 平面也每帧重算：判定线第一帧跑完才落到世界 z = 0，
            // 在那之前块会临时待在相机正前方的安全平面上（可见但位置不精确），
            // 一旦判定线落位，块立刻跟过去。
            float localZ = ResolveBlockLocalZ(_parent, ResolveWorldZ());
            if (!Mathf.Approximately(localZ, _blockLocalZ))
            {
                _blockLocalZ = localZ;
                for (int i = 0; i < _blocks.Count; i++) _blocks[i].SetLocalZ(localZ);
                // 全屏层也要跟着挪，强制重铺一次
                _syncedWidth = -1f;
                _syncedHeight = -1f;
            }

            SyncFullscreenLayers();
            SyncFullscreenLayersEnabled();

            for (int i = 0; i < _blocks.Count; i++)
            {
                _blocks[i].SetScreenSize(_screenWidth, _screenHeight);
                _blocks[i].UpdateBlock(now);
            }
        }

        // ============ 断触 ============

        /// <summary>
        /// 扫描所有手指，标记落在 Active 块上的那些。等价官方 CheckBlocks。
        /// 应在音符判定之前调用。
        /// </summary>
        public void UpdateBlocking(Finger[] fingers, int count, float now)
        {
            EnsureUpdated(now);

            _blockedFingers.Clear();
            for (int i = 0; i < count && i < fingers.Length; i++)
            {
                if (fingers[i] == null) continue;
                if (IsBlockedAt(fingers[i].newPosition))
                    _blockedFingers.Add(i);
            }

            IsTouchingAnyBlock = _blockedFingers.Count > 0;
            UpdateLowPassFilterState(IsTouchingAnyBlock);
        }

        /// <summary>该手指是否被红区阻断（本帧）。</summary>
        public bool IsBlocked(int fingerIndex) => _blockedFingers.Contains(fingerIndex);

        /// <summary>本帧的世界空间内缩量。等价官方 TryGetBlockTouchHalfSize 的输入。</summary>
        private float TouchInsetWorld() => TouchInsetScreenHeightRatio * Mathf.Max(_screenHeight, 0f);

        /// <summary>
        /// 世界坐标是否被红区阻断。等价官方 JudgeControl.TryGetBlockingBlock —— **奇偶规则**：
        ///
        ///     被阻断 &lt;=&gt; 「落在任意普通块内」 XOR 「落在奇数个减块内」
        ///
        /// 并且**整块矩形**与**内缩矩形**两次测试都必须成立（两套计数各算各的）。
        /// 参考实现：Phira-Pro `prpr/src/core/block.rs::block_touch_blocked`。
        ///
        /// 这条规则同时解释了两件反直觉的事：
        ///   * 单独一个减块是**实心断触区**（命中奇数个减块 → 阻断），不是「洞」；
        ///   * 减块盖住普通块时，普通块变成**可操作窗口**（两者都命中 → 抵消）。
        ///
        /// Ametrine 谱面前半段正是后者：块 0 是一条 20% 宽 × 200% 高的旋转竖带（减块），
        /// 块 1~21 都是落在带里的普通小块 —— 它们是可操作窗口。
        ///
        /// ⚠️ 上一版「减块直接跳过」正好把语义做反了：
        /// 竖带不断触、21 个窗口反而全断触。用真实谱面网格采样对比过：
        /// 66 秒时旧实现 17.2% 断触、正确实现 75.5%。
        /// </summary>
        public bool IsBlockedAt(Vector2 worldPosition)
        {
            float now = CurrentTime;
            float inset = TouchInsetWorld();

            bool fullNormal = false;
            int fullSubtract = 0;
            bool insetNormal = false;
            int insetSubtract = 0;

            for (int i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block == null || !block.IsActive(now)) continue;

                if (block.Contains(worldPosition, now, 0f))
                {
                    if (block.IsSubtract) fullSubtract++;
                    else fullNormal = true;
                }

                if (block.Contains(worldPosition, now, inset))
                {
                    if (block.IsSubtract) insetSubtract++;
                    else insetNormal = true;
                }
            }

            // 命中即返回的旧语义已废弃：现在必须扫完所有块才能得出奇偶结论。
            return (fullNormal ? 1 : 0) != (fullSubtract & 1)
                && (insetNormal ? 1 : 0) != (insetSubtract & 1);
        }

        // ============ 低通滤波 ============

        /// <summary>绑定游戏音乐源（由 Main 注入），用于按住块时的闷音效果。</summary>
        public void AttachMusicSource(AudioSource source)
        {
            _musicSource = source;
            if (source == null) return;

            _lowPass = source.gameObject.GetComponent<AudioLowPassFilter>();
            if (_lowPass == null) _lowPass = source.gameObject.AddComponent<AudioLowPassFilter>();
            _lowPass.cutoffFrequency = UnfilteredCutoffFrequency;
            _lowPass.enabled = false;
        }

        /// <summary>等价官方 ProgressControl.UpdateLowPassFilterState：带去抖。</summary>
        private void UpdateLowPassFilterState(bool isTouchingAnyBlock)
        {
            if (_wasTouchingAnyBlock == isTouchingAnyBlock) return;
            _wasTouchingAnyBlock = isTouchingAnyBlock;
            if (_lowPass == null) return;

            _lowPass.enabled = isTouchingAnyBlock;
            if (_sweep != null) StopCoroutine(_sweep);
            _sweep = StartCoroutine(LerpLowPassFilter(isTouchingAnyBlock));
        }

        private IEnumerator LerpLowPassFilter(bool enableFilter)
        {
            float from = _lowPass.cutoffFrequency;
            float to = enableFilter ? LowPassCutoffFrequency : UnfilteredCutoffFrequency;
            float t = 0f;
            while (t < LowPassLerpDuration)
            {
                t += Time.deltaTime;
                _lowPass.cutoffFrequency = Mathf.Lerp(from, to, Mathf.Clamp01(t / LowPassLerpDuration));
                yield return null;
            }

            _lowPass.cutoffFrequency = to;
            _sweep = null;
        }
    }
}
