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

        private int _lastFrame = -1;
        private float _screenWidth;
        private float _screenHeight;
        private float _blockLocalZ;
        private float _syncedWidth = -1f;
        private float _syncedHeight = -1f;

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

            // ===== 决定块所在的 z 平面 =====
            // 判定线是用 Instantiate(prefab, GameRoot) 创建的（保留世界坐标），
            // 所以判定线和音符实际落在 **世界 z = 0**；而 Game Root 自身在 z = -10
            // （和正交相机同一个平面）。
            //
            // 块如果用 transform.SetParent(parent, false)（保持局部坐标），
            // local z 就会停在 0 → 世界 z = -10 → 正好掉进相机近裁剪面
            // （near = 0.3）内侧被整块裁掉。这就是「红区完全不显示」的原因。
            //
            // 最稳的取法：直接读一条已经创建好的判定线的世界 z，把块放到同一平面；
            // 实在没有判定线时退回 z = 0（由场景可知相机在 -10、Game Root 在 -10，
            // 这个平面上的一切都可见）。
            float blockLocalZ = ResolveBlockLocalZ(parent);
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
                if (_stencilClearRenderer != null) _stencilClearRenderer.enabled = false;
                if (_fillRenderer != null) _fillRenderer.enabled = false;
            }
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

        /// <summary>把「判定线/音符所在的平面」换算成块相对父物体的局部 z。</summary>
        private static float ResolveBlockLocalZ(Transform parent)
        {
            float worldZ = 0f;

            var lines = GlobalSetting.Lines;
            if (lines != null && lines.Count > 0 && lines[0] != null && lines[0].transform != null)
                worldZ = lines[0].transform.position.z;

            if (parent == null) return worldZ;
            return parent.InverseTransformPoint(new Vector3(0f, 0f, worldZ)).z;
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
            Debug.Log($"[BlockArea] 共 {_blocks.Count} 块（普通 {normal} / 减块 {subtract}），" +
                      $"第一块出现于 {first}，块局部 z={_blockLocalZ:F1}，" +
                      $"游戏区 {_screenWidth:F2}x{_screenHeight:F2}，" +
                      $"stencil 管线={(BlockAreaMaterials.Ready ? "就绪" : "着色器缺失，已退化为矩形绘制")}");
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
            SyncFullscreenLayers();

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
