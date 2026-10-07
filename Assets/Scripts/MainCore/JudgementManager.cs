using System;
using System.Collections.Generic;
using System.Linq;
using MainCore.Common;
using UnityEngine;

namespace MainCore
{
    public class Finger
    {
        /// <summary>
        /// 跨帧稳定的手指标识（同一根手指从按到抬之间不变）。
        ///
        /// 红区的「感染」状态（官方 <c>infected</c>）必须挂在**手指**上而不是数组下标上：
        /// Unity 的 <c>Input.GetTouch(i)</c> 的 i 在一根手指抬起后会让后面的手指前移，
        /// 用下标存感染会导致状态串到别的手指上。取值见
        /// <see cref="JudgementManager.MouseFingerId"/> / <see cref="JudgementManager.KeyboardFingerId"/>。
        /// </summary>
        public int Id;

        private int flickCnt = 0;
        Vector2 flickDirection;
        public bool isNewFlick;
        private Vector2 lastDirection;
        private TouchPhase lastPhase = TouchPhase.Canceled;
        public Queue<Vector2> lastPositions = new Queue<Vector2>();

        private int listLimit = Application.targetFrameRate / 5; //  1/5 seconds
        public Vector2 newPosition;
        int oldPosCounter;
        public TouchPhase phase;
        Vector3 screenPosition;
        Vector3 worldPosition;
        public bool IsFirstClick => lastPhase == TouchPhase.Began || phase == TouchPhase.Began;
        public bool IsKeyboard { private set; get; } = false;

        public void ClearOldPoss()
        {
            oldPosCounter = 0;
        }

        public void CheckInput(bool isKey = false)
        {
            IsKeyboard = isKey;
            screenPosition.x = newPosition.x;
            screenPosition.y = newPosition.y;
            screenPosition.z = 8f;
            worldPosition = Camera.main.ScreenToWorldPoint(screenPosition);
            newPosition.x = worldPosition.x;
            newPosition.y = worldPosition.y;

            //CheckFlick
            isNewFlick = false;
            for (int i = 0; i < lastPositions.Count; i++)
            {
                var dir = (lastPositions.ElementAt(i) - newPosition).normalized;
                if (Vector2.Distance(lastPositions.ElementAt(i), newPosition) > 0.0075f)
                {
                    flickDirection = dir;
                    isNewFlick = true;
                    lastPositions.Clear();
                    break;
                }
            }

            //RecordTouchPosition
            if (phase == TouchPhase.Moved)
            {
                lastPositions.Enqueue(newPosition);
                if (lastPositions.Count > listLimit - 1) lastPositions.Dequeue();
            }
            else if (phase == TouchPhase.Ended || phase == TouchPhase.Canceled)
            {
                flickCnt = 0;
            }
        }

        public void UpdatePhase(TouchPhase touchPhase)
        {
            lastPhase = phase;
            phase = touchPhase;
        }

        public void ClearTapFlag()
        {
            lastPhase = TouchPhase.Canceled;
            phase = TouchPhase.Stationary;
        }

        public bool IsFlick()
        {
            if (IsKeyboard)
            {
                return true;
            }

            if (((Vector2.Dot(lastDirection, flickDirection) < 0 && flickCnt > 0) || flickCnt == 0) && isNewFlick)
            {
                isNewFlick = false;
                flickCnt++;
                lastDirection = flickDirection;
                flickDirection = Vector2.zero;
                return true;
            }

            return false;
        }
    }

    public class JudgementManager : MonoSingleton<JudgementManager>
    {
        public int numOfFingers;
        public Finger[] fingers = new Finger[20];
        private Array keys;

        /// <summary>
        /// 鼠标的稳定手指 id。触摸用 <c>Touch.fingerId</c>（恒 ≥ 0），
        /// 鼠标与键盘取负值，天然不会冲突。
        /// </summary>
        public const int MouseFingerId = -1;

        /// <summary>键盘按键的稳定手指 id —— 每个 KeyCode 一个，与按下的先后顺序无关。</summary>
        public static int KeyboardFingerId(KeyCode key) => -(int)key - 2;

        /// <summary>
        /// 指针（触摸/鼠标）占用的手指数。键盘手指从它之后开始排，
        /// 这样 numOfFingers 永远等于「真实有效的手指数」，
        /// 而不是旧实现里的 t+1（没按键时也至少留 1 个手指）。
        /// </summary>
        private int _pointerCount;
        private List<float> notesDistances = new List<float>();

        private List<NoteMovement> notesInJudge = new List<NoteMovement>();

        // Start is called before the first frame update
        void Start()
        {
            fingers.Initialize();
            for (int i = 0; i < 20; i++)
                fingers[i] = new Finger();
            keys = Enum.GetValues(typeof(KeyCode));
        }

        // Update is called once per frame
        void Update()
        {
            if (GlobalSetting.IsAutoPlayEnabled)
                return;

#if UNITY_EDITOR
            UpdateMouseInput(); //Editor (for test).
#else
            UpdateTouchInput(); //Touchscreen support.
#endif

#if UNITY_STANDALONE || UNITY_EDITOR
            UpdateKeyBoardInput(); //Keyboard(?) support.
#endif

            // 红区断触：先把落在 Active 块上的手指标记出来，再做音符判定。
            // 顺序不能反 —— 被块吃掉的手指必须在本帧判定之前就被排除。
            var blockArea = BlockAreaManager.Instance;
            if (blockArea != null && blockArea.HasBlocks)
            {
                blockArea.UpdateBlocking(fingers, numOfFingers,
                    Main.Instance != null ? Main.Instance.progressManager.NowTime : 0f);
            }

            UpdateJudge();
        }

        public void UpdateTouchInput()
        {
            _pointerCount = Mathf.Min(Input.touchCount, fingers.Length);
            for (int i = 0; i < _pointerCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                fingers[i].Id = touch.fingerId;
                fingers[i].UpdatePhase(touch.phase);
                fingers[i].newPosition = touch.position;
                fingers[i].CheckInput();
            }

            numOfFingers = _pointerCount;
        }

        /// <summary>
        /// 键盘（桌面）输入。
        ///
        /// 关键：键盘手指会跳过位置窗口（NoteInJudgeArea 对 IsKeyboard 恒真），
        /// 所以「谁是键盘手指」必须严格。旧实现有两个致命问题：
        ///
        /// 1) 用 `Input.GetKey` 遍历整个 KeyCode 枚举，鼠标键（Mouse0..Mouse6）和
        ///    摇杆键（JoystickButton*）都会被命中并被当成键盘手指。结果：桌面上
        ///    点一下鼠标，就等于多出一根「位置在屏幕左下角、且无视位置窗口」的
        ///    手指，屏幕上任何音符都会被判定 —— 即「不点在音符上也会判定」。
        ///
        /// 2) `numOfFingers = t + 1` 在没有按键时也至少是 1；再加上开头的
        ///    `Input.anyKey` 提前 return 会沿用上一帧的数值，松开按键后那根
        ///    键盘手指仍然留在 numOfFingers 里继续判定，且它的位置停在
        ///    (0,0) 换算出的屏幕左下角、又跳过位置窗口 —— 于是「什么都不点，
        ///    音符也在自动判定」。
        ///
        /// 现在：只认真正的键盘按键，且键盘手指占用的索引永远紧跟在指针之后，
        /// 松手当帧就会从 numOfFingers 里消失。
        /// </summary>
        public void UpdateKeyBoardInput()
        {
            if (!Input.anyKey)
            {
                numOfFingers = _pointerCount;
                return;
            }

            int count = 0;
            foreach (KeyCode key in keys)
            {
                if (count >= 20)
                    break;
                if (!IsKeyboardPlayKey(key))
                    continue;
                if (!Input.GetKey(key))
                    continue;

                int index = _pointerCount + count;
                if (index >= fingers.Length)
                    break;

                fingers[index].Id = KeyboardFingerId(key);
                fingers[index].UpdatePhase(Input.GetKeyDown(key) ? TouchPhase.Began : TouchPhase.Moved);
                fingers[index].newPosition = new Vector2(0f, 0f);
                fingers[index].CheckInput(isKey: true);
                count++;
            }

            numOfFingers = _pointerCount + count;
        }

        /// <summary>真正的「键盘打歌手势」。鼠标、手柄摇杆、编辑器快进方向键都不算。</summary>
        private static bool IsKeyboardPlayKey(KeyCode key)
        {
            // Mouse0(323) .. Joystick8Button19(499) 覆盖鼠标键与全部摇杆键，
            // 它们会被 Input.GetKey 命中，但属于点击/手柄输入，不是键盘。
            if (key >= KeyCode.Mouse0 && key <= KeyCode.Joystick8Button19)
                return false;
            // 方向键留给编辑器的快进/快退（Main.Update），不作为打歌手势。
            if (key == KeyCode.RightArrow || key == KeyCode.LeftArrow)
                return false;
            return true;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR
        public void UpdateMouseInput()
        {
            if (Input.GetMouseButtonDown(0)) fingers[0].phase = TouchPhase.Began;
            else if (Input.GetMouseButton(0)) fingers[0].phase = TouchPhase.Moved;
            else if (Input.GetMouseButtonUp(0)) fingers[0].phase = TouchPhase.Ended;
            fingers[0].Id = MouseFingerId;
            fingers[0].newPosition = Input.mousePosition;
            fingers[0].CheckInput();
            _pointerCount = Input.GetMouseButton(0) ? 1 : 0;
            numOfFingers = _pointerCount;
        }
#endif

        public void UpdateJudge()
        {
            // numOfFingers 在任何写入路径上都不会超过 20，这里再夹一次，
            // 防止越界写 line.PositionX（它固定只有 20 个元素）。
            int fingerCount = Mathf.Clamp(numOfFingers, 0, fingers.Length);
            if (fingerCount == 0)
                return;
            foreach (var i in GlobalSetting.Lines)
            {
                for (int k = 0; k < fingerCount; k++)
                {
                    i.PositionX[k] = GetLocalPosition(fingers[k].newPosition, i.transform.parent).x;
                }
            }

            float pTime = Main.Instance.progressManager.NowTime;
            var blockArea = BlockAreaManager.Instance;

            for (int i = 0; i < fingerCount; i++)
            {
                // 红区断触：该手指按在块上，触摸被块消费，不参与任何音符判定。
                if (blockArea != null && blockArea.IsBlocked(i))
                    continue;

                var judgedFlick = false;
                var judgedFlickTime = 9999f;
                notesInJudge.Clear();
                notesDistances.Clear();
                foreach (var line in GlobalSetting.Lines)
                {
                    var (n, flickTime, absDistance) = line.GetNearestNote(fingers[i], line.PositionX[i]);
                    if (n != null)
                    {
                        notesInJudge.Add(n);
                        notesDistances.Add(absDistance);
                    }

                    if (flickTime < 9990f)
                    {
                        judgedFlick = true;
                        judgedFlickTime = Math.Min(flickTime, judgedFlickTime);
                    }
                }

                if (notesInJudge.Count == 0)
                    continue;

                NoteMovement note = notesInJudge[0];
                float time = notesInJudge[0].Note.time;
                float distance = notesDistances[0];
                for (var index = 0; index < notesInJudge.Count; index++)
                {
                    var t = notesInJudge[index];
                    if (note.Note.time > t.Note.time)
                    {
                        time = t.Note.time;
                        note = t;
                        distance = notesDistances[index];
                    }
                }

                for (var index = 0; index < notesInJudge.Count; index++)
                {
                    var t = notesInJudge[index];
                    if (Math.Abs(t.Note.time - time) < 0.0001f)
                    {
                        if (notesDistances[index] < distance)
                        {
                            distance = notesDistances[index];
                            note = t;
                        }
                    }
                }

                if (judgedFlick && note.Note.time > judgedFlickTime) //如果判定了flick且flick在tap前面 // TODO: 为啥这个有bug
                {
                    fingers[i].ClearTapFlag();
                    continue;
                }

                note.Judge(pTime, fingers[i]);
            }
        }

        private static Vector3 GetLocalPosition(Vector3 worldPosition, Transform parent) =>
            parent.InverseTransformPoint(worldPosition);

        public static bool NoteInJudgeArea(float fingerX, float noteX, bool isKeyboard = false)
        {
            if (isKeyboard)
            {
                return true;
            }

            return (fingerX > noteX - 2.2f && fingerX < noteX + 2.2f);
        }
    }
}
