// 红区（噪域）未生效 / 预警层 —— 官方 DisabledBlock 着色器的等价实现。
//
// 这一层画在音符与判定线**之下**（官方把 Disabled 放在 Background 层），
// 是一层偏暗的红色蒙版 + 火花，提示「这里马上要被封住」。
// 不需要 GrabPass。
//
// 来源：Phigros 4.0 官方着色器反编译结果，经 Phira-Pro（GPL-3.0）
//       prpr/src/core/block_shader_full.frag 交叉验证。

Shader "PtyOS/BlockAreaDisabled"
{
    Properties
    {
        _MainTex ("(unused)", 2D) = "white" {}
        _Masks ("Masks", 2D) = "black" {}
        _AuxMasks ("AuxMasks", 2D) = "black" {}
        _HoverTex ("Hover (1/8 grid, screen UV)", 2D) = "black" {}
        _DisplaceTex ("Displace", 2D) = "gray" {}
        _SparkTex ("Spark", 2D) = "gray" {}
        _NoiseTex ("Noise", 2D) = "gray" {}
        _Tint ("Tint", Color) = (1,1,1,1)

        _DisplaceDirection ("Displace Direction", Vector) = (1, 1, 0, 0)
        _DisabledSpeed ("Disabled Speed", Float) = 0.3
        _DisabledSparkIntensity ("Disabled Spark Intensity", Float) = 2.29

        _DisabledFillColor ("Disabled Fill Color", Color) = (0.497, 0.13766898, 0.13766898, 1)
        _DisabledFillOpacity ("Disabled Fill Opacity", Float) = 0.4
        _DisabledSparkTint ("Disabled Spark Tint", Color) = (0.31132078, 0.077830195, 0.077830195, 1)
        _DisabledSparkOpacity ("Disabled Spark Opacity", Float) = 3.5

        _View ("View (w,h,aspect)", Vector) = (16, 9, 1.7777778, 0)
        _EffectRT_TexelSize ("Effect RT Texel", Vector) = (0.00208, 0.0037, 480, 270)
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
            Name "Disabled"
            Cull Off
            Lighting Off
            ZWrite Off
            ZTest Always
            Blend One One
            ColorMask RGBA

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "BlockAreaCommon.cginc"

            fixed4 frag(v2f IN) : SV_Target
            {
                float mask = tex2D(_Masks, basePixelUV(IN.fieldUV)).a;
                return disabledColor(mask, IN.disabledDisplaceUV, IN.sparkUV) * IN.color;
            }
            ENDCG
        }
    }

    Fallback Off
}
