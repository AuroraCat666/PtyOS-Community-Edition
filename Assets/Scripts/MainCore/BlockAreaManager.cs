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

            float orthoSize = Camera.main != null ? Camera.main.orthographicSize : 5f;
            manager._screenHeight = 2f * orthoSize;
            // 与判定线同一套坐标口径：用 GlobalSetting.Aspect（已按宽屏遮罩修正），
            // 不能直接用 Camera.main.aspect，否则在 21:9 等宽屏上块会横向错位。
            manager._screenWidth = manager._screenHeight * GlobalSetting.Aspect;

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
                _blocks[i].UpdateBlock(now);
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

        /// <summary>世界坐标是否落在任一 Active 块内。等价官方 TryGetBlockingBlock。</summary>
        public bool TryGetBlockingBlock(Vector2 worldPosition, out BlockAreaController blockingBlock)
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block != null && block.IsActive(CurrentTime) && block.IsPositionInside(worldPosition))
                {
                    blockingBlock = block;
                    return true;
                }
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
