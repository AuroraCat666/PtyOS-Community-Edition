// 红区 · stencil 清零层（全屏 quad）
//
// 为什么需要它：本工程的 Main Camera clearFlags = Depth（只清深度），
// 上一帧 residue 的 stencil 值不保证被清掉。若不显式清零，
// 上一帧还是红区、这一帧已经不是的像素会残留 bit0 = 1，
// 全屏显示层就会在那里错误地画出一块红。
//
// 因此每帧在写 stencil 之前，先用这个全屏 quad 把 bit0 抹成 0。
Shader "PtyOS/BlockAreaStencilClear"
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
                ReadMask 0
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

            fixed4 frag(v2f IN) : SV_Target
            {
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}
