// 红区 · 显示层（全屏 quad）
//
// 前两个 pass 把「普通块 XOR 减块」的结果写进 stencil 的 bit0，
// 这里用一个覆盖整屏的 quad 只在 bit0 == 1 的地方画红 ——
// 于是屏幕上红区画出来的形状，与手指被阻断的范围**逐像素一致**。
//
// 用全屏 quad 而不是「每块再画一遍」的原因：减块盖住普通块时，
// 普通块自身矩形内有一部分是被挖掉的，逐个矩形画无法表达这种挖空。
Shader "PtyOS/BlockAreaFill"
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
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGBA

            Stencil
            {
                Ref 1
                ReadMask 1
                WriteMask 0
                Comp Equal
                Pass Keep
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
                fixed4 color  : COLOR;
            };

            sampler2D _MainTex;
            fixed4 _Color;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                // SpriteRenderer.color 走顶点色，必须带上，否则改颜色无效。
                OUT.color = IN.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                return IN.color;
            }
            ENDCG
        }
    }
}
