using UnityEngine;

namespace MainCore
{
    /// <summary>
    /// 一个红区在某一时刻解算出的几何与相位，坐标系与官方一致：
    ///
    ///   chart space —— x 向右、y 向上，整屏 = [-1, 1] × [-1/aspect, 1/aspect]
    ///
    /// 对应 Phira-Pro 的 <c>prpr/src/core/Zone</c>（block_shader.rs 里定义）。
    /// mask 光栅化、Edge/Glow 膨胀、着色器采样全部使用这套坐标。
    /// </summary>
    public struct BlockAreaZone
    {
        /// <summary>矩形中心（chart space）。</summary>
        public Vector2 Center;

        /// <summary>矩形半宽 / 半高（chart space，已含动画缩放）。</summary>
        public Vector2 Half;

        /// <summary>绕 Z 的旋转，**弧度**，逆时针为正（与官方 rotation 同向）。</summary>
        public float Angle;

        /// <summary>是否为减块。</summary>
        public bool Invert;

        /// <summary>是否处于生效期（会阻断触摸）。</summary>
        public bool Active;

        /// <summary>是否处于「生效前 0.5 秒」的预警期。</summary>
        public bool Ready;

        /// <summary>出现后的淡入进度（0..1），仅 Disabled 期参与。</summary>
        public float Opacity;

        /// <summary>本帧是否有效（可见且尺寸非退化）。无效的块直接跳过。</summary>
        public bool Valid;

        public float HalfWidth => Half.x;
        public float HalfHeight => Half.y;
    }
}
