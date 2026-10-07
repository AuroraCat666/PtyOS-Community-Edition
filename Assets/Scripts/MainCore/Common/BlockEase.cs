using UnityEngine;

namespace MainCore.Common
{
    /// <summary>
    /// 红区缓动查表 —— 等价于官方 block easing 枚举（Phigros 4.0 噪域）。
    ///
    /// 官方在启动时构造 15 张 101 点的表，运行时只查表 + 线性插值。
    /// 枚举语义（0..=14）：
    ///   0        Linear
    ///   1..=12   In / Out / InOut 的 Quad / Cubic / Quart / Quint
    ///            power = (type - 1) / 3 + 2      （Quad=2, Cubic=3, Quart=4, Quint=5）
    ///            m     = (type - 1) % 3
    ///              m=0 → In    : u^power
    ///              m=1 → Out   : 1 - (1-u)^power
    ///              m=2 → InOut : u < 0.5 ? 2^(power-1)*u^power
    ///                                     : 1 - (2-2u)^power / 2
    ///   13       HoldStart（恒 0）
    ///   14       JumpToEnd（恒 1）
    ///
    /// 即：1=InQuad 2=OutQuad 3=InOutQuad 4=InCubic 5=OutCubic 6=InOutCubic
    ///     7=InQuart 8=OutQuart 9=InOutQuart 10=InQuint 11=OutQuint 12=InOutQuint
    ///
    /// ⚠️ 别再把 3/6/9/12 当「死表」
    /// ------------------------------------------------------------------
    /// 曾经有一版实现把 3/6/9 当作「构造循环没写到的死表」恒 0、把 12 当作
    /// 「隔点降采样、中间 50..57 留 0」，理由是这么推的：循环 idx ∈ {1,4,7,10}
    /// 步长 3、每轮只写 E[idx] 和 E[idx+1]，所以 3/6/9/12 永远写不到。
    ///
    /// 这个推导有两个硬伤：
    ///   1. `n = idx / 3 + 2` 只在 idx ∈ {1,4,7,10} 时才是整数（2/3/4/5）。
    ///      这恰恰说明每轮写的是 **In、Out、InOut 三条**（idx、idx+1、idx+2），
    ///      于是 3/6/9/12 都被写成了 InOut 曲线 —— 与"InOut 是分段函数"
    ///      这个已知事实吻合（旧实现自己也给 12 写了 40 行分段代码，自相矛盾）。
    ///   2. 实测反证：ハテ AT 的块 20 有一串 `moveEvents` 把 x 在 0.25 ↔ 0.025
    ///      之间往复摆 5 个来回，`easeTypeX` 全是 3。若 3 恒 0，这些事件完全不生效、
    ///      方块会卡死在原地 —— 与官方实机（方块平滑来回摆）不符。
    ///
    /// 症状对照：用错时表现为「噪域在变换时不再平滑移动，而是一格格跳 / 卡成
    /// 一堆互不相连的竖矩形」。
    /// </summary>
    public static class BlockEase
    {
        public const int TypeCount = 15;
        public const int SampleCount = 101;

        private static float[][] tables;

        /// <summary>查表 + 线性插值。progress 会被钳制到 [0,1] 对应的表内区间。</summary>
        public static float GetEaseWithProgress(float progress, int type)
        {
            if (tables == null) Instantiation();

            if ((uint)type >= TypeCount)
            {
                // 官方行为是抛 IndexOutOfRangeException。社区版要能跑各种自制谱，
                // 这里降级为 Linear，避免一张谱面直接让游戏崩掉。
                Debug.LogWarning($"[BlockEase] easeType {type} 越界（应 0~14），按 Linear 处理");
                type = 0;
            }

            // 官方在 NaN 时经 ARM64 饱和转换得到 INT_MIN，最终走 i < 0 分支返回 table[0]。
            if (float.IsNaN(progress)) return tables[type][0];

            float s = progress * 100f;
            int i = (int)s;
            if (i >= 100) return tables[type][100];
            if (i < 0) return tables[type][0];

            float b = tables[type][i];
            float a = tables[type][i + 1];
            return b + (s - i) * (a - b);
        }

        private static void Instantiation()
        {
            tables = new float[TypeCount][];
            for (int t = 0; t < TypeCount; t++)
                tables[t] = new float[SampleCount];

            for (int i = 0; i < SampleCount; i++)
            {
                float u = i / 100f;
                tables[0][i] = u;   // Linear
                tables[13][i] = 0f; // HoldStart
                tables[14][i] = 1f; // JumpToEnd
            }

            // 1..12：In / Out / InOut × Quad / Cubic / Quart / Quint
            for (int t = 1; t <= 12; t++)
            {
                int power = (t - 1) / 3 + 2;
                int m = (t - 1) % 3;
                float[] row = tables[t];
                for (int i = 0; i < SampleCount; i++)
                {
                    float u = i / 100f;
                    switch (m)
                    {
                        case 0:
                            row[i] = Mathf.Pow(u, power);
                            break;
                        case 1:
                            row[i] = 1f - Mathf.Pow(1f - u, power);
                            break;
                        default:
                            row[i] = u < 0.5f
                                ? (1 << (power - 1)) * Mathf.Pow(u, power)
                                : 1f - Mathf.Pow(2f - 2f * u, power) * 0.5f;
                            break;
                    }
                }
            }
        }
    }
}
