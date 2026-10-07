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

        private readonly List<BlockAreaController> _blocks = new List<BlockAreaController>();
        private readonly HashSet<int> _blockedFingers = new HashSet<int>();

        private int _lastFrame = -1;
        private float _screenWidth;
        private float _screenHeight;

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

            foreach (var area in chart.blockAreaList)
            {
                var blockGo = new GameObject(area.isSubtract ? "SubtractBlock" : "Block");
                var controller = blockGo.AddComponent<BlockAreaController>();
                controller.Initialize(area, parent, manager._screenWidth, manager._screenHeight);
                manager._blocks.Add(controller);
            }

            Debug.Log($"[BlockArea] 已创建 {manager._blocks.Count} 块红区");
            return manager;
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
                if (TryGetBlockingBlock(fingers[i].newPosition, out _))
                    _blockedFingers.Add(i);
            }

            IsTouchingAnyBlock = _blockedFingers.Count > 0;
            UpdateLowPassFilterState(IsTouchingAnyBlock);
        }

        /// <summary>该手指是否被红区阻断（本帧）。</summary>
        public bool IsBlocked(int fingerIndex) => _blockedFingers.Contains(fingerIndex);

        /// <summary>
        /// 世界坐标是否被红区阻断。等价官方 TryGetBlockingBlock。
        ///
        /// 关键语义：减块（isSubtract）不阻断触摸，只影响渲染（把红区挖掉一块、
        /// 露出底下的画面）。触摸上直接跳过减块，只统计普通块。
        ///
        /// 不这样处理会直接毁掉游戏：实测谱面的块 0 就是一个覆盖
        /// 120% 屏宽 × 200% 屏高的减块，若让减块也参与阻断，整屏都会变成断触区。
        ///
        /// 已知取舍：块 22（300% 屏全屏红区）+ 块 23（100% 屏减块）这组
        /// 原意应是「全屏红区、中间挖出一个可操作的洞」，当前实现下洞内仍会被
        /// 块 22 阻断。要让洞真正可操作，需要改成
        /// 「命中普通块 && 未被任何减块覆盖」的判定，但那会让块 0 把全部
        /// 21 个小块一并挖空。两者只能取其一，待实机对照官方行为后再定。
        /// </summary>
        public bool TryGetBlockingBlock(Vector2 worldPosition, out BlockAreaController blockingBlock)
        {
            float now = CurrentTime;

            for (int i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block == null || !block.IsActive(now)) continue;

                // 减块自身不阻断触摸。它是「从红区中挖掉一块」的区域，
                // 只影响渲染（露出底下的画面），不产生断触。
                if (block.IsSubtract) continue;

                if (!block.IsPositionInside(worldPosition)) continue;

                // 命中即返回，与官方 TryGetBlockingBlock 一致（不取最优、只取首个）
                blockingBlock = block;
                return true;
            }

            blockingBlock = null;
            return false;
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
