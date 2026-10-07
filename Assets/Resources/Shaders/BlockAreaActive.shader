// 红区（噪域）生效层 —— 官方 ActiveBlock 着色器的等价实现。
//
// 这一层是「后处理」：在精灵、音符、判定线之后绘制，所以自带 GrabPass
// 抓取它之前的所有画面，用作噪声场里的像素化背景。
// 详见 BlockAreaCommon.cginc 头部说明。
//
// 来源：Phigros 4.0 官方着色器反编译结果，经 Phira-Pro（GPL-3.0）
//       prpr/src/core/block_shader_full.frag 交叉验证。

Shader "PtyOS/BlockAreaActive"
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

        // ---- 官方参数（block_shader.rs 的 FLOATS / COLORS）----
        _EdgeOpacity ("Edge Opacity", Float) = 0.8
        _FillStrength ("Fill Strength", Float) = 0.667
        _FillOpacity ("Fill Opacity", Float) = 0.667
        _GlowIntensity ("Glow Intensity", Float) = 0.8
        _SparkMapOpacity ("Spark Map Opacity", Float) = 5.69
        _SparkHueShiftAmount ("Spark Hue Shift", Float) = 0.2
        _SparkDisplaceIntensity ("Spark Displace", Float) = 2.39
        _DisplaceBlendIntensity ("Displace Blend", Float) = 0.411
        _DisplaceSpeed ("Displace Speed", Float) = 1.5
        _DisplaceStrength ("Displace Strength", Float) = 0.15
        _TouchPosShine ("Touch Pos Shine", Float) = 0
        _TouchPosRadius ("Touch Radius", Float) = 0.5
        _TouchPosSDFSmoothness ("Touch SDF Smooth", Float) = 0.47
        _TouchPosSDFFalloff ("Touch SDF Falloff", Float) = 0.41
        _BackgroundPixelScale ("Background Pixel Scale", Float) = 6.0
        _ShineSpeed ("Shine Speed", Float) = 37.9
        _ShineBrightness ("Shine Brightness", Float) = 0.12
        _TouchDisplaceSpeed ("Touch Displace Speed", Float) = 2.9
        _TouchDisplaceStrength ("Touch Displace Strength", Float) = 0.08
        _NoiseEvoSpeed ("Noise Evo Speed", Float) = 0.03
        _NoiseDirChangeSpeed ("Noise Dir Change Speed", Float) = 60.0
        _NoiseDisplaceStrength ("Noise Displace Strength", Float) = 1.0
        _NoiseRadius ("Noise Radius", Float) = 0.48
        _NoiseSmoothness ("Noise Smoothness", Float) = 1.0
        _SDFCellSize ("SDF Cell Size", Float) = 0.11
        _SDFSmoothness ("SDF Smoothness", Float) = 0.63
        _SDFFalloff ("SDF Falloff", Float) = 0.34
        _SDFMoveSpeed ("SDF Move Speed", Float) = 9.3
        _TouchBackgroundPixelScale ("Touch BG Pixel Scale", Float) = 8.0
        _TouchPosCount ("Touch Pos Count", Float) = 0

        _EdgeColor ("Edge Color", Color) = (1.0, 0.33018857, 0.33018857, 1)
        _FillColor ("Fill Color", Color) = (0.7132075, 0.23549296, 0.23549296, 1)
        _GlowColor ("Glow Color", Color) = (1.0, 0.17924517, 0.17924517, 1)
        _DisplaceDirection ("Displace Direction", Vector) = (1, 1, 0, 0)
        _ShineColor ("Shine Color", Color) = (1,1,1,1)
        _TouchDisplaceDirection ("Touch Displace Dir", Vector) = (1, 1, 0, 0)
        _NoiseTint ("Noise Tint", Color) = (1, 0, 0, 1)
        _TouchGlowColor ("Touch Glow Color", Color) = (1, 0, 0, 1)
        _SparkTint ("Spark Tint", Color) = (1.0, 0.28490567, 0.28490567, 1)

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

        // 红区要看到「自己之前的画面」：判定线、音符、背景，以及 HUD。
        GrabPass { "_SceneTex" }

        Pass
        {
            Name "Active"
            Cull Off
            Lighting Off
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha
            ColorMask RGBA

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "BlockAreaCommon.cginc"

            fixed4 frag(v2f IN) : SV_Target
            {
                return fullActive(IN) * IN.color;
            }
            ENDCG
        }
    }

    Fallback Off
}
