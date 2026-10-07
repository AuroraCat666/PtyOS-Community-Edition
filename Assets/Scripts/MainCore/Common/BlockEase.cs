using UnityEngine;

namespace MainCore.Common
{
    /// <summary>
    /// 红区缓动查表 —— 等价于官方 GetEase（逆向自 Phigros 4.0.1 libil2cpp.so）。
    ///
    /// 官方在启动时构造 15 张 101 点的表，运行时只查表 + 线性插值，没有任何超越函数调用。
    /// 这里完整照抄构造过程，以保证与原版逐点一致。
    ///
    /// 三处必须照抄的行为（做错会让谱面完全错乱）：
    ///   1. easeType 3 / 6 / 9 / 13 是「死表」——构造循环从未写入，恒为 0。
    ///      语义是 Lerp(cur, next, 0) == cur，即「保持当前值，到下一个事件才跳变」的阶梯。
    ///      这不是 bug，是谱面的正常写法（实测本仓库谱面用了约 148 次）。
    ///   2. easeType 14 恒为 1，等价于瞬间跳到终点。
    ///   3. easeType 12 是分段降采样，中间 50..57 为未写入的 0，存在真实跳变。
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
                // 这里降级为钳制并告警，避免一张谱面直接让游戏崩掉。
                Debug.LogWarning($"[BlockEase] easeType {type} 越界（应 0~14），已钳制");
                type = Mathf.Clamp(type, 0, TypeCount - 1);
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
                tables[t] = new float[SampleCount]; // 零初始化，这是 3/6/9/13 成为死表的前提

            // easeType 0：线性
            for (int i = 0; i < SampleCount; i++)
                tables[0][i] = i / 100f;

            // 主循环：idx 取 1, 4, 7, 10（步进 3），指数 n = idx / 3 + 2
            // idx=1→n=2（二次）  idx=4→n=3（三次）  idx=7→n=4（四次）  idx=10→n=5（五次）
            for (int idx = 1; idx <= 10; idx += 3)
            {
                int n = idx / 3 + 2;
                var inCurve = tables[idx];
                var outCurve = tables[idx + 1];
                for (int i = 0; i < SampleCount; i++)
                {
                    float u = i / 100f;
                    inCurve[i] = Mathf.Pow(u, n);
                    outCurve[i] = 1f - Mathf.Pow(1f - u, n);
                }
            }

            // easeType 12：两段隔点降采样
            //   pass1  E[12][j]      = E[10][8 + 2j] * 0.5            (j = 0…49)
            //   pass2  E[12][58 + j] = E[11][8 + 2j] * 0.5 + 0.5      (j = 0…41，写满到 99)
            // 结果 E[12][50..57] 未被任何语句写入，保持 0 —— 这是真实的跳变，不是笔误。
            var e10 = tables[10];
            var e11 = tables[11];
            var e12 = tables[12];

            for (int j = 0; j <= 49; j++)
            {
                int src = 8 + 2 * j;
                // 源索引 102/104/106 在原版越界读到堆垃圾，无法复现；
                // 这里按公式外推，保证曲线连续可预测。
                e12[j] = (src < SampleCount ? e10[src] : Mathf.Pow(src / 100f, 5f)) * 0.5f;
            }

            for (int j = 0; j <= 49; j++)
            {
                int dst = 58 + j;
                if (dst >= SampleCount - 1) break; // 末点单独归一，不要覆盖
                int src = 8 + 2 * j;
                e12[dst] = (src < SampleCount ? e11[src] : 1f - Mathf.Pow(1f - src / 100f, 5f)) * 0.5f + 0.5f;
            }

            e12[100] = 1f;

            // easeType 13：恒为 0（已由零初始化保证）
            // easeType 14：恒为 1
            for (int i = 0; i < SampleCount; i++)
                tables[14][i] = 1f;
        }
    }
}
