// 红区 · 减块 stencil 翻转层
//
// 与 BlockAreaStencilNormal 唯一的区别是 Pass 操作从 Replace 换成 Invert：
// bit0 取反，于是「普通块 XOR 减块」正好落在 bit0 上：
//
//   只有普通块  → 1
//   只有减块    → ~0 = 1     （单独一个减块是实心区，官方 block_touch 同此）
//   两者都有    → ~1 = 0     （减块盖住普通块 ⇒ 挖出可操作窗口）
//   都没有      → 0
//
// 多个减块重叠时 Invert 天然是奇偶，与官方 block_touch 的 parity 一致。
// 官方的着色器在 ≥3 块重叠时用的是「恰好 1 个」的阈值窗口，与奇偶略有差异，
// 但实际谱面（Ametrine）中减块时间不重叠，两者结果相同。
//
// ★ 必须比 BlockAreaStencilNormal 后渲染（用 sortingOrder 保证）。
Shader "PtyOS/BlockAreaStencilSubtract"
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
                Ref 0
                ReadMask 1
                WriteMask 1
                Comp Always
                Pass Invert
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

            fixed4 frag(v2f IN) : SV_Target
            {
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}
