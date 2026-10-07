# 红区 / 噪域（BlockArea）

Phigros 4.0 第九章引入的「红区」机制：谱面里会出现一块（或多块）会把该区域内的
触摸整体吃掉的矩形，被吃掉的手指不参与任何音符判定 —— 也就是玩家说的「断触」。

本目录的代码是把官方实现完整复刻到 Unity 的那一部分。

## 文件分工

| 文件 | 职责 |
|---|---|
| `BlockAreaZone.cs` | 一块红区在某一时刻解算出的几何与相位（chart space） |
| `BlockAreaMaskBuilder.cs` | CPU 光栅化：Compose / Edge / Glow / Disabled / Ready / 位移噪声 |
| `BlockAreaAssets.cs` | 官方纹理（`.bytes`）与两个材质的加载缓存 |
| `BlockAreaNoiseField.cs` | 两块全屏 quad 的驱动、纹理上传、uniform 下发、手指 hover 光栅化 |
| `../BlockAreaController.cs` | 单块红区的位姿解算（缩放→旋转→移动）与命中测试 |
| `../BlockAreaManager.cs` | 谱面级驱动、断触奇偶规则、按住时的低音滤波 |

着色器在 `Assets/Resources/Shaders/`：

* `BlockAreaActive.shader` —— 生效层，后处理（带 `GrabPass` 抓背景）
* `BlockAreaDisabled.shader` —— 未生效 / 预警层，画在音符之下
* `BlockAreaCommon.cginc` —— 官方 fragment 的 HLSL 直译，两个 Pass 共用

官方纹理导出在 `Assets/Resources/BlockArea/`（`.bytes` = `BAX1` + 宽高 + RGBA32）。

## 坐标系

* **世界空间**：游戏区高 10（正交相机 size 5），宽 = 高 × `GlobalSetting.Aspect`。
* **chart space**：`x ∈ [-1, 1]`、`y ∈ [-1/aspect, 1/aspect]`，原点在游戏区中心、y 向上。
  换算就是 `chart = world * 2 / screenWidth`。
* **mask UV（fieldUV）**：quad 恰好铺满游戏区，所以 `fieldUV == chart 归一化`。
* **屏幕 UV（sceneUV）**：`ComputeScreenPos` 出来的全屏 UV，手指位置与
  GrabPass 快照都用这套（宽屏时游戏区只是屏幕中间那块 16:9）。

## 两套「减块」语义（别搞混）

官方的视觉与输入**有意**用了不同规则：

* **输入（断触）**：奇偶规则 ——
  `被阻断 ⟺ (落在普通块内) XOR (落在奇数个减块内)`，且整块矩形与内缩矩形两次测试都要成立。
* **视觉（mask）**：`subtract_enabled(red) = 0.09 ≤ red/255 < 0.12` 的阈值窗口。

所以「画出来的地方」与「手指被拦住的地方」在边缘上会有细微差别，这是官方的行为，不是 bug。

## 移植要点 / 踩过的坑

1. `Unorm` 必须是 `floor(v * 255 + 0.5)`（Rust 的 `round`），`Mathf.Round` 是银行家舍入，数值对不上。
2. 位移后的 Compose 必须按最大位移量（约 `0.072 × 边长`）**外扩包围盒**，否则会出现硬切边。
3. 手指 hover 的 `TouchHover` 精灵在 `.bytes` 里已经上下翻转成「行 0 = v=0」，
   采样时**不要**再套 Rust 那边的 `(1 - v)`（那是从 top-down 的 PNG 解码数组出发的）。
4. 着色器里 `v*v*(3-2v)` 这类 smoothstep 手写展开，反编译是三条独立语句，
   中间变量会被复用 —— 不能合并成 `(3-2v)²`。
5. mask 覆盖的是游戏区（16:9），而 hover 必须跟全屏手指坐标对齐，
   所以在非 16:9 屏幕上两者不是同一个 UV 区间，hover 单独用了一张纹理。
6. **别把 GLSL 的函数名抄进 HLSL**。`BlockAreaCommon.cginc` 是官方 GLSL ES
   反编译结果的「逐行直译」，改它的时候很容易顺手把 `inversesqrt` 这类
   GLSL 专有名字带进来 —— **HLSL 没有 `inversesqrt`，对应的是 `rsqrt`**。
   这一处曾经让整个红区变成洋红（见下）。
7. **hover 的触摸源是「被阻断的手指」，不是全部触摸**。官方传给噪域渲染的是
   `blocked_touches` —— 只有被奇偶规则吃掉的手指才会进这个列表
   （Phira-Pro `chart.rs::render_block_overlay` 传给 `draw_zones_with_touches`
   的就是它）。所以**谱面这一段没有噪域、或者手指按在噪域之外时，不应该有任何
   触摸特效**。早期实现把全部手指都喂了进去，表现为「屏幕随便一点就冒出噪域特效」。
   因此 `BlockAreaManager.UpdateBlocking` 的顺序是：
   `EnsureUpdated`（解算几何）→ 算 `_blockedFingers` → `CollectTouches`（只收这些），
   而绘制收口在 `LateUpdate`，保证用的就是本帧的断触结果。
8. **阻断是「手指生命周期」，不是「区域生命周期」**。官方称这个状态为 `infected`，
   源码注释原话是 *"Infection is a finger lifetime, not a field lifetime. Quiet frames
   must retain it until an explicit Ended/Cancelled event."*

   手指在按下的这段时间里**只要曾经**落进活跃红区，就一直保持阻断 ——
   之后滑出红区、甚至红区自己已经消失，阻断和 hover 特效都**不会**恢复，
   直到这根手指抬起。实现见 `BlockAreaManager._infected`：

   ```csharp
   // 官方 judge.rs（注意传给 finger_blocked 的是硬编码的 Stationary）
   if matches!(phase, Started | Ended | Cancelled) { infected.remove(&id); }
   if matches!(phase, Ended | Cancelled) { return false; }
   if inside { infected.insert(id); }
   return infected.contains(&id)
   // 调用侧：let inside = !infected.contains(&id) && touch_blocked(p, t, aspect);
   ```

   两个容易踩的点：
   - **要用稳定 id，不能用数组下标**。`Input.GetTouch(i)` 的 i 在一根手指抬起后
     会让后面的手指前移，用下标存感染会把状态串到别的手指上。所以 `Finger.Id`
     在输入层被赋成 `Touch.fingerId`（≥0）、鼠标 `-1`、键盘 `-(int)KeyCode - 2`。
   - **`!infected.contains(id) &&` 这个短路不是优化，是语义本身** —— 已感染的手指
     不再重算几何，所以红区消失后它仍然保持。

9. **红区缓动是独立的 15 种枚举：`1..=12` 是 In/Out/InOut 的 Quad/Cubic/Quart/Quint，
   只有 `13` 是 HoldStart（恒 0）、`14` 是 JumpToEnd（恒 1）。**
   （判定线事件走的是另一套 `EaseUtils`（RPE 的 30 种缓动），两者不是同一个枚举，
   千万不要互相套用。）

   ```
   type: 0  1    2     3      4   5    6      7    8     9      10   11    12     13 14
   曲线: Lin InQ  OutQ  InOutQ InC OutC InOutC InQt OutQt InOutQt InQi OutQi InOutQi 0  1
   ```

   公式（`power = (type - 1) / 3 + 2`，`m = (type - 1) % 3`）：

   ```csharp
   m == 0 → u^power                                      // In
   m == 1 → 1 - (1 - u)^power                            // Out
   m == 2 → u < 0.5 ? 2^(power-1) * u^power              // InOut（分段函数）
                    : 1 - (2 - 2u)^power / 2
   ```

   ⚠️ **别再把 `3 / 6 / 9 / 12` 当「死表」。** 曾经有一版实现把 3/6/9 当成「构造循环
   没写到的死表」恒 0、把 12 当成「隔点降采样、中间 50..57 留洞」。它源自一个半对的
   推导：循环 `idx ∈ {1,4,7,10}` 步长 3 → 断定每轮只写 `E[idx]` 和 `E[idx+1]` 两条。
   **但 `n = idx / 3 + 2` 只在 idx ∈ {1,4,7,10} 时才是整数（2/3/4/5）—— 这恰恰说明
   每轮写的是 `idx`、`idx+1`、`idx+2` 三条**（In / Out / InOut），于是 3/6/9/12 都被
   写成了 InOut 曲线。旧实现给 12 写的那 40 行「分段」代码本身就是这个结构的残影：
   既然 12 有分段构造，3/6/9 就不可能没有。

   **实证反证（2026-10-07，ハテ AT）**：块 20 有一串 `moveEvents` 把 x 在
   `0.25 ↔ 0.025` 之间往复摆 5 个来回（左边缘），块 21 在 `0.75 ↔ 0.975` 镜像摆动，
   `easeTypeX` 全是 **3**。若 3 恒 0，这些事件完全不生效、方块会卡死在原地 ——
   与官方实机（方块平滑来回摆）不符。影响面很大：ハテ AT 一个谱面就有
   `move.X` 332 次、`scale.X` 1141 次用 ease 3。

   **症状对照**：用错时表现为「噪域在变换时不平滑移动，而是一格格跳 /
   卡成一堆互不相连的竖矩形」；修好后是连续的平滑摆动。

   自检：`GetEaseWithProgress` 在 `u = 0.25 / 0.5 / 0.75` 处应为
   `3 → 0.1250 / 0.5000 / 0.8750`、`6 → 0.0625 / 0.5000 / 0.9375`、
   `9 → 0.0312 / 0.5000 / 0.9688`、`12 → 0.0156 / 0.5000 / 0.9844`；
   `13` 必须全 0、`14` 必须全 1。

10. **块「消失」绝大多数是谱面数据，不是渲染 bug —— 先用数据判据排除。**

    能丢块的地方**只有一处**：`BlockAreaZone.Valid`（`MakeZone` 里 `half.x > 0 && half.y > 0`）。
    `BlockAreaManager` 收集时写的是 `if (block.Zone.Valid) _zoneBuffer[count++] = block.Zone;`，
    而 `_zoneBuffer` 按 `_blocks.Count` 分配、**没有任何数量上限**，所以不存在
    「块太多被截断」这种可能。也就是说：**只要块没被丢掉，它一定进了遮罩**。

    于是「某一帧少了几块」永远等价于「那几块的 `scale` 算出来是 0」。
    Ametrine 的实测（60 Hz 扫描，半轴 < 0.03 视为看不见）：

    | 块 | 时段 | 时长 | 原因 |
    |---|---|---|---|
    | 10 | 72.717 → 72.767 | 50 ms | 谱面 `scaleEvents` 在 72.750 写了 `(0,0)` |
    | 1  | 72.800 → 72.850 | 50 ms | 谱面 `scaleEvents` 在 72.833 写了 `(0,0)` |
    | 1  | 75.350 → 75.983 | 633 ms | 最后一个事件写了 `scale = (1, 0.0)`，高度为 0 |
    | 0  | 54.683 → 56.383 | 1700 ms | 减块出现时 `scale = (1, 0)`，还没长开 |
    | 2/3/4/5, 11~15, 20 | 各自的出现瞬间 | 2~4 帧 | 出现时 `scale = (0,0)`，是正常的放大入场 |

    **看到「块变少」先查这个表，再去怀疑代码。**

## 出问题时的第一反应：整屏洋红

红区的绘制 quad 恰好**铺满整个游戏区**，所以只要它的着色器编译失败，
Unity 就会把整块游戏区渲染成洋红（magenta）。中文里常被描述成「屏幕变粉」。

典型症状：**不按屏幕没事，一按就整屏变粉，松开又恢复** —— 因为按下才让
`touchVisible` 成立、从而启用 Active quad（`showActive = NonZeroCompose > 0 || touchVisible`）。

先看 Console / `Logs/Editor.log` 里的 `Shader error in 'PtyOS/BlockAreaXxx'`，
它会直接给出行号。改完着色器后建议跑一遍这个自检（在
`Assets/Resources/Shaders/` 下执行）：

```bash
grep -nE '\b(inversesqrt|mix|mod|texture2D|texture|vec[234]|mat[234]|fract|dFdx|dFdy|fwidth)\s*\(' \
     BlockAreaCommon.cginc BlockAreaActive.shader BlockAreaDisabled.shader
```

没有输出就说明没有 GLSL 残留。（`frac` / `exp2` / `log2` / `smoothstep` /
`lerp` / `dot` / `rsqrt` 等都是 HLSL 合法函数，不在检查范围内。）

## 来源与许可

算法与着色器均来自 Phigros 4.0 官方实现的逆向结果，交叉验证参考
[Phira-Pro](https://github.com/Phira-Pro/Phira-Pro)（GPL-3.0）的
`prpr/src/core/block*.rs` 与 `block_shader_full.frag/.vert`。
纹理是官方资源的导出。本项目同样是 GPL-3.0。
