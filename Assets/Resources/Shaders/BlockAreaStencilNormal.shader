// 红区 · 普通块 stencil 写入层
//
// 只写 stencil 的 bit0 为 1，不输出任何颜色（ColorMask 0）。
// 官方视觉规则（Phira-Pro prpr/src/core/block.rs::enabled_compose）：
//
//     visible = 「落在任意普通块内」 XOR 「落在减块内」
//
// 用 stencil 低位实现：
//   普通块   Replace  bit0 = 1
//   减块     Invert   翻转 bit0（多个减块叠加即奇偶）
//   显示层   Comp Equal Ref 1 处才画红
//
// ★ 顺序要求：本 shader 必须比 BlockAreaStencilSubtract 先渲染，
//   否则 Replace 会把减块的翻转结果覆盖掉（用 sortingOrder 保证）。
Shader "PtyOS/BlockAreaStencilNormal"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Pass
        {
            Cull Off
            Lighting Off
            ZWrite Off
            ZTest Always
            Blend One Zero
            ColorMask 0

            Stencil
            {
                Ref 1
                ReadMask 1
                WriteMask 1
                Comp Always
                Pass Replace
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            fixed4 _Color;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                return OUT;
            }

            // 颜色被 ColorMask 全部丢弃，这里只需返回任意值。
            fixed4 frag(v2f IN) : SV_Target
            {
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}
