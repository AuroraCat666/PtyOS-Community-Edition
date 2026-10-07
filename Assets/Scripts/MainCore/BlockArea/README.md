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
