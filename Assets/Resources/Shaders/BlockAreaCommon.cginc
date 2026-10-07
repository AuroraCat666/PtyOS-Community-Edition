// 红区（噪域）着色器的公共部分。
//
// 这是 Phigros 官方 ActiveBlock / DisabledBlock 着色器反编译结果（GLSL ES）
// 的 HLSL 直译。中间变量故意保留官方的 u_xlat 命名与运算顺序 ——
// 它们是反编译产物，改名字很容易在插值/精度边界上引入偏差。
// 交叉验证来源：Phira-Pro（GPL-3.0）prpr/src/core/block_shader_full.frag。
//
// 使用本文件的着色器（每个各有一个 Pass，靠文件名区分，不再用 _Layer 分支）：
//   BlockAreaActive.shader    生效层   Blend One OneMinusSrcAlpha
//   BlockAreaDisabled.shader  未生效层 Blend One One
//
// uniform 一览（由 C# 每帧设置）：
//   _Masks        R=位移后 Compose  G=Edge  B=Glow  A=Disabled Compose
//   _AuxMasks     R=Ready Normal  G=Ready Subtract  B=Disabled  A=Hover
//   _DisplaceTex / _SparkTex / _NoiseTex   官方噪点纹理（Point 采样）
//   _SceneTex     GrabPass 抓到的当前画面（仅 Active 用）
//   _View         (fieldW, fieldH, aspect, unused)，只用于算 clipHalfWidth
//   _UnityTime    (t/20, t, t*2, t*3)，t 是谱面时间
//   _EffectRT_TexelSize (1/w, 1/h, w, h)  —— mask 分辨率
//
// 官方始终把 chart space 的 y 归一化到 [-1/aspect, 1/aspect]、x 归一化到 [-1,1]，
// 并用 clipHalfWidth 把画面裁到 16:9。本项目里 fieldUV 已经是对齐到游戏区
// （16:9）的 UV，所以 _View 取游戏区尺寸即可让 clipHalfWidth == 0.5，永不 discard。

#ifndef BLOCK_AREA_COMMON_INCLUDED
#define BLOCK_AREA_COMMON_INCLUDED

sampler2D _Masks;
sampler2D _AuxMasks;
sampler2D _HoverTex;
sampler2D _DisplaceTex;
sampler2D _SparkTex;
sampler2D _NoiseTex;
sampler2D _SceneTex;

float4 _UnityTime;
float4 _View;
float4 _EffectRT_TexelSize;
float4 _HoverTex_TexelSize;

fixed4 _Tint;

float _EdgeOpacity;
float _FillStrength;
float _FillOpacity;
float _GlowIntensity;
float _SparkMapOpacity;
float _SparkHueShiftAmount;
float _SparkDisplaceIntensity;
float _DisplaceBlendIntensity;
float _DisplaceSpeed;
float _DisplaceStrength;
float4 _DisplaceDirection;
float _TouchPosShine;
float _TouchPosRadius;
float _TouchPosSDFSmoothness;
float _TouchPosSDFFalloff;
float _BackgroundPixelScale;
float _ShineSpeed;
float _ShineBrightness;
float4 _ShineColor;
float _TouchDisplaceSpeed;
float _TouchDisplaceStrength;
float4 _TouchDisplaceDirection;
float4 _NoiseTint;
float _NoiseEvoSpeed;
float _NoiseDirChangeSpeed;
float _NoiseDisplaceStrength;
float _NoiseRadius;
float _NoiseSmoothness;
float _SDFCellSize;
float _SDFSmoothness;
float _SDFFalloff;
float _SDFMoveSpeed;
float4 _TouchGlowColor;
float _TouchBackgroundPixelScale;
float4 _EdgeColor;
float4 _FillColor;
float4 _GlowColor;
float4 _SparkTint;
float4 _DisabledFillColor;
float _DisabledFillOpacity;
float4 _DisabledSparkTint;
float _DisabledSparkOpacity;
float _DisabledSparkIntensity;
float _DisabledSpeed;
float4 _TouchPos[10];
float _TouchPosCount;

struct appdata_t
{
    float4 vertex : POSITION;
    float4 color  : COLOR;
    float2 uv     : TEXCOORD0;
};

struct v2f
{
    float4 pos        : SV_POSITION;
    fixed4 color      : COLOR;
    float2 fieldUV    : TEXCOORD0;
    float2 sceneUV    : TEXCOORD1;
    float2 displaceUV : TEXCOORD2;
    float2 sparkUV    : TEXCOORD3;
    float2 disabledDisplaceUV : TEXCOORD4;
    float2 touchDisplaceUV    : TEXCOORD5;
    float2 noiseUV    : TEXCOORD6;
    float4 screenPos  : TEXCOORD7;
    float  clipHalfWidth : TEXCOORD8;
};

v2f vert(appdata_t v)
{
    v2f o;
    o.pos = UnityObjectToClipPos(v.vertex);
    // SpriteRenderer / MeshRenderer 的颜色走顶点色或 _Tint，两者都带上。
    o.color = v.color * _Tint;

    o.fieldUV = v.uv;

    float4 sp = ComputeScreenPos(o.pos);
    o.screenPos = sp;
    o.sceneUV = sp.xy / sp.w;

    // 与官方顶点着色器一致：这些 UV 在顶点阶段算，尽量保持 ST 精度。
    o.displaceUV         = o.sceneUV * float2(0.8, 0.3);
    o.sparkUV            = o.sceneUV * float2(3.0, 1.2);
    o.disabledDisplaceUV = o.sceneUV * float2(0.5, 0.2);
    o.touchDisplaceUV    = o.sceneUV * float2(0.55, 0.3);
    o.noiseUV            = o.sceneUV * float2(1.5, 1.46);

    o.clipHalfWidth = _View.y * 0.888888896 / _View.x;
    return o;
}

// ============================================================ 采样器

float2 basePixelUV(float2 uv)
{
    float2 size = _EffectRT_TexelSize.zw;
    return (floor(uv * size) + 0.5) / size;
}

float composeSample(float2 uv) { return tex2D(_Masks, basePixelUV(uv)).r; }
float2 effectSample(float2 uv) { return tex2D(_Masks, uv).gb; }
float4 auxSample(float2 uv) { return tex2D(_AuxMasks, basePixelUV(uv)); }

float hoverSample(float2 uv)
{
    // 手指 hover 蒙版：原生 1/8 网格（官方也存在 _AuxMasks 的 A 通道里）。
    //
    // 这里单独开一张纹理，是因为官方 mask 与 hover 共用「视口 UV」这一套坐标，
    // 而本项目把 mask 画在游戏区（16:9）上、hover 要跟手指的全屏 UV 对齐，
    // 两者在非 16:9 屏上不是同一个区间 —— 共用一张纹理必然错位。
    // 采样方式与官方一致：先吸附到 1/8 网格中心再点采样。
    float2 size = _HoverTex_TexelSize.zw;
    return tex2D(_HoverTex, (floor(uv * size) + 0.5) / size).r;
}

float4 snapshotSample(float2 uv)
{
    // 官方 BlockRender.Start 用对 6 的带符号整数除法（0x2aaaaaab），
    // 即把画面降采样到 1/6 后再点采样。这里 GrabPass 拿到的是全分辨率画面，
    // 直接点采样每个 1/6 块的中心像素 —— 视觉上是同一档像素化，
    // 只少了降采样带来的轻微柔化。
    float2 size = max(floor(_ScreenParams.xy / 6.0), float2(1.0, 1.0));
    uv = (floor(clamp(uv, 0.0, 1.0) * size) + 0.5) / size;
    return tex2D(_SceneTex, uv);
}

// 官方的 smoothstep 手写展开：v*v*(3-2v)。
// 反编译里是三条独立语句（t = v*-2+3; v = v*v; v = v*t），
// 中间变量会被复用，翻译时不能合并成 v = (3-2v)^2。
float smooth01(float v)
{
    float t = v * -2.0 + 3.0;
    v = v * v;
    return v * t;
}

// ============================================================ Disabled 层

// 未生效 / 预警期的红色蒙版。mask = _Masks.a（Disabled Compose）。
fixed4 disabledColor(float mask, float2 disabledDisplaceUV, float2 sparkUV)
{
    float4 result;
    float u_xlat16_0;
    float u_xlat16_1x, u_xlat16_8, u_xlat16_9, u_xlat16_13, u_xlat16_3;
    float u_xlat4x, u_xlat16_4;
    float2 u_xlat16_1, u_xlat4xy;
    float4 u_xlat2;

    u_xlat16_0 = mask;
    if (u_xlat16_0 - 9.99999975e-05 < 0.0) discard;

    u_xlat4x = _UnityTime.x * _DisabledSpeed;
    u_xlat16_1x = dot(_DisplaceDirection.xy, _DisplaceDirection.xy);
    u_xlat16_1x = rsqrt(u_xlat16_1x);
    u_xlat16_1 = u_xlat16_1x.xx * _DisplaceDirection.xy;

    u_xlat2.xyw = u_xlat4x.xxx * u_xlat16_1.xyx;
    u_xlat2.z = u_xlat4x * (-u_xlat16_1.y);
    u_xlat2 = u_xlat2 + disabledDisplaceUV.xyxy;

    u_xlat16_4 = tex2D(_DisplaceTex, u_xlat2.zw).x;
    u_xlat16_8 = tex2D(_DisplaceTex, u_xlat2.xy).x;
    u_xlat16_9 = u_xlat16_4 + -0.5;
    u_xlat16_13 = u_xlat16_4 + u_xlat16_8;
    u_xlat16_3 = u_xlat16_8 + -0.5;
    u_xlat16_13 = u_xlat16_13 * 0.5;

    u_xlat2.x = u_xlat16_9 * (-u_xlat16_1.y);
    u_xlat2.y = u_xlat16_9 * u_xlat16_1.x;
    u_xlat4xy = u_xlat16_1.xy * float2(u_xlat16_3, u_xlat16_3) + u_xlat2.xy;
    u_xlat4xy = u_xlat4xy * float2(_DisabledSparkIntensity, _DisabledSparkIntensity) + sparkUV.xy;

    u_xlat16_4 = tex2D(_SparkTex, u_xlat4xy).x;
    float3 spark = u_xlat16_4 * _DisabledSparkTint.xyz;
    spark = u_xlat16_13 * spark;

    float3 fill = _DisabledFillColor.xyz * _DisabledFillOpacity;
    fill = spark * _DisabledSparkOpacity + fill;

    result.xyz = u_xlat16_0 * fill;
    result.w = 1.0;
    return result;
}

// ============================================================ Active 层

fixed4 fullActive(v2f IN)
{
    float4 result = float4(0, 0, 0, 0);
    float3 activeCol = float3(0, 0, 0);

    float u_xlat16_0, u_xlat16_16, u_xlat16_32, u_xlat16_48, u_xlat16_1x, u_xlat16_17;
    float u_xlat16_18, u_xlat16_50, u_xlat16_34, u_xlat16_35, u_xlat16_51, u_xlat16_33;
    float u_xlat16_37, u_xlat16_56, u_xlat48, u_xlat49, u_xlat52;
    float2 u_xlat16, u_xlat1xy, u_xlat16_5xy, u_xlat7, u_xlat16xy;
    float3 u_xlat16_3, u_xlat6, u_xlat1xyz, u_xlat16_8, u_xlat16_9, u_xlat16_10;
    float3 u_xlat16_13, u_xlat16_14, u_xlat16_15, u_xlat16_21;
    float4 u_xlat1, u_xlat4, u_xlat5;

    // ---- 官方 16:9 保护：裁掉超宽屏两侧 ----
    if (-abs(IN.fieldUV.x - 0.5) + IN.clipHalfWidth < 0.0) discard;

    u_xlat16_0  = composeSample(IN.fieldUV);
    u_xlat16.xy = IN.fieldUV.xy * _EffectRT_TexelSize.zw;
    u_xlat16.xy = floor(u_xlat16.xy);
    u_xlat16.xy = u_xlat16.xy + float2(0.5, 0.5);
    u_xlat16.xy = u_xlat16.xy * _EffectRT_TexelSize.xy;
    u_xlat16_16 = effectSample(u_xlat16.xy).x;      // Edge（点采样到 RT 纹素）
    u_xlat16_32 = effectSample(IN.fieldUV).y;       // Glow（线性）
    u_xlat16_48 = auxSample(IN.fieldUV).r;          // Ready Normal
    u_xlat16_1x = auxSample(IN.fieldUV).g;          // Ready Subtract
    u_xlat16_17 = auxSample(IN.fieldUV).b;          // Disabled
    u_xlat16_18 = u_xlat16_17 * abs(u_xlat16_1x - u_xlat16_48);
    u_xlat48    = hoverSample(IN.sceneUV);

    u_xlat16_34 = u_xlat16_16 + u_xlat16_0;
    u_xlat16_50 = u_xlat16_32 + u_xlat16_34;
    {
        float t = abs(u_xlat16_1x - u_xlat16_48) * u_xlat16_17 + u_xlat16_50;
        if (u_xlat48 + t - 9.99999975e-05 < 0.0) discard;
    }

    if (u_xlat16_50 > 9.99999975e-05)
    {
        float edgeTerm = u_xlat16_16 * _EdgeOpacity;
        float gl       = u_xlat16_34 * (-u_xlat16_32) + u_xlat16_32;   // Glow 去掉内部重叠
        float gi       = gl * _GlowIntensity;

        // ---- 位移方向 ----
        float invLen = rsqrt(dot(_DisplaceDirection.xy, _DisplaceDirection.xy));
        float2 d2 = invLen.xx * _DisplaceDirection.xy;   // normalize 后的方向
        float t1 = _UnityTime.x * _DisplaceSpeed;
        float pscale = max(_BackgroundPixelScale, 1.0);

        // 两个沿 ±垂直方向的像素化采样点
        float2 tA = float2(t1 * d2.x, t1 * d2.y) + IN.displaceUV;
        float2 tB = float2(t1 * (-d2.y), t1 * d2.x) + IN.displaceUV;
        tA = floor(tA * _ScreenParams.xy / pscale) * pscale + pscale * 0.5;
        tB = floor(tB * _ScreenParams.xy / pscale) * pscale + pscale * 0.5;
        tA /= _ScreenParams.xy;
        tB /= _ScreenParams.xy;

        float aDis = tex2D(_DisplaceTex, tA).x;
        float bDis = tex2D(_DisplaceTex, tB).x;
        float avg  = (aDis + bDis) * 0.5;
        float aC = aDis - 0.5;
        float bC = bDis - 0.5;

        // 位移向量（把 aC/bC 投回与方向垂直的两个分量）
        float2 disp;
        disp.x = (-d2.y) * bC;
        disp.y = d2.x * bC;
        disp.xy = d2.xy * aC + disp.xy;

        // ---- 背景像素化 + 位移，再取屏幕快照 ----
        float2 scenePix = floor(IN.sceneUV * _ScreenParams.xy / pscale) * pscale + pscale * 0.5;
        scenePix /= _ScreenParams.xy;
        float2 sceneUV2 = disp.xy * _DisplaceStrength + scenePix;

        float2 sparkUV2 = disp.xy * _SparkDisplaceIntensity + IN.sparkUV;
        float sparkSample = tex2D(_SparkTex, sparkUV2).x;
        float3 sparkCol = sparkSample * _SparkTint.xyz;
        u_xlat6 = snapshotSample(sceneUV2).xyz;

        // ---- 触摸 SDF（有手指按在屏上时的高光）----
        float touchShine;
        if (_TouchPosCount > 0.0)
        {
            float2 tuv = IN.screenPos.xy / IN.screenPos.w;
            tuv = disp.xy * _DisplaceStrength + tuv;
            tuv = tuv * _ScreenParams.xy / _ScreenParams.yy;

            float acc = 1.0;
            for (int i = 0; i < 10; i++)
            {
                if (i >= (int)_TouchPosCount) break;
                float2 diff = tuv - _TouchPos[i].xy;
                float dist = sqrt(dot(diff, diff));
                float s = (-dist) + acc;
                s = -abs(s) + _TouchPosSDFSmoothness;
                s = max(s, 0.0);
                s = s / _TouchPosSDFSmoothness;
                dist = min(dist, acc);
                s = s * s;
                s = s * _TouchPosSDFSmoothness;
                acc = (-s) * 0.25 + dist;
            }
            float v = acc - _TouchPosRadius;
            v = (1.0 / (-_TouchPosRadius)) * v;
            v = clamp(v, 0.0, 1.0);
            v = smooth01(v);
            v = log2(v);
            v = v * _TouchPosSDFFalloff;
            touchShine = exp2(v);
        }
        else
        {
            touchShine = 0.0;
        }

        // ---- 火花：HSV 色相旋转后与快照混合 ----
        float3 sparkPre = avg * sparkCol;
        float3 sparkMap = sparkPre * _SparkMapOpacity;

        bool b32 = u_xlat6.y >= u_xlat6.z;
        float sel = b32 ? 1.0 : 0.0;
        float2 m1 = (-u_xlat6.zy) + u_xlat6.yz;
        float4 p4;
        p4.xy = sel * m1 + u_xlat6.zy;
        p4.zw = sel * float2(1.0, -1.0) + float2(-1.0, 0.666666687);
        bool b32b = u_xlat6.x >= p4.x;
        float sel2 = b32b ? 1.0 : 0.0;
        float4 p7;
        p7.xyz = -p4.xyw;
        p7.w = -u_xlat6.x;
        float4 p8;
        p8.x = u_xlat6.x + p7.x;
        p8.yzw = p4.yzx + p7.yzw;
        float4 p21;
        p21.xyz = sel2.xxx * p8.xyz + p4.xyw;
        float selv = sel2 * p8.w + u_xlat6.x;
        float mn = min(p21.y, selv);
        float d = p21.x - mn;
        float q = (-p21.y) + selv;
        float dd = d * 6.0 + 1.00000001e-10;
        float hue = q / dd + p21.z;
        float sat = p21.x + 1.00000001e-10;
        sat = d / sat;

        float3 hsvShift;
        hsvShift.x = sparkMap.x * _SparkHueShiftAmount + abs(hue);
        hsvShift.y = sparkMap.y * _SparkHueShiftAmount + sat;
        hsvShift.z = sparkMap.z * _SparkHueShiftAmount + p21.x;

        float3 rgbv = hsvShift + float3(1.0, 0.666666687, 0.333333343);
        rgbv = frac(rgbv);
        rgbv = rgbv * 6.0 + float3(-3.0, -3.0, -3.0);
        rgbv = abs(rgbv) + float3(-1.0, -1.0, -1.0);
        rgbv = clamp(rgbv, 0.0, 1.0);
        rgbv = rgbv + float3(-1.0, -1.0, -1.0);

        float3 c5 = hsvShift.y * rgbv + float3(1.0, 1.0, 1.0);
        float3 c8 = c5 * hsvShift.x;
        float3 c9 = c8 + c8;
        float3 c10 = sparkMap * c9;
        float3 backPre = (-hsvShift.x) * c5 + float3(1.0, 1.0, 1.0);
        backPre = backPre + backPre;
        float3 basePre = (-sparkPre) * _SparkMapOpacity + float3(1.0, 1.0, 1.0);
        basePre = (-backPre) * basePre + float3(1.0, 1.0, 1.0);

        float3 selCol;
        selCol.x = rgbv.x >= 0.5 ? 1.0 : 0.0;
        selCol.y = rgbv.y >= 0.5 ? 1.0 : 0.0;
        selCol.z = rgbv.z >= 0.5 ? 1.0 : 0.0;

        float3 sparkMix = (-c9) * sparkMap + basePre;
        sparkMix = selCol * sparkMix + c10;
        sparkMix = clamp(sparkMix, 0.0, 1.0);

        float3 fillBase = (-avg) * _DisplaceBlendIntensity + _FillColor.xyz;
        float3 fillMix = _FillStrength * (sparkMix - fillBase) + fillBase;

        float3 glowCol = gi * _GlowColor.xyz;
        glowCol = _EdgeColor.xyz * edgeTerm + glowCol;
        float3 col = fillMix * u_xlat16_0 + glowCol;
        col = (touchShine * _TouchPosShine + 1.0) * col;

        float alpha = u_xlat16_0 * _FillOpacity + edgeTerm;
        result.w = gl * _GlowIntensity + alpha;
        activeCol = col;
    }

    // ---- Shine：Ready/Disabled 叠加的微弱呼吸白 ----
    float3 shine = float3(0, 0, 0);
    if (u_xlat16_18 > 9.99999975e-05)
    {
        shine = _ShineBrightness * _ShineColor.xyz;
        float s = sin(_UnityTime.y * _ShineSpeed);
        s = s * 0.5 + 1.0;
        shine = s * shine;
        shine = u_xlat16_18 * shine;
    }
    else
    {
        u_xlat16_18 = 0.0;
    }

    // ---- Hover：手指按住时的噪声扰动 + 触摸辉光 ----
    float3 hoverCol = float3(0, 0, 0);
    if (u_xlat48 > 9.99999975e-05)
    {
        float invLen = rsqrt(dot(_TouchDisplaceDirection.xy, _TouchDisplaceDirection.xy));
        float2 td = invLen.xx * _TouchDisplaceDirection.xy;
        float tt = _UnityTime.x * _TouchDisplaceSpeed;

        float tp = max(_TouchBackgroundPixelScale, 1.0);
        float2 u1 = floor((td * tt + IN.touchDisplaceUV) * _ScreenParams.xy / tp) * tp + tp * 0.5;
        float2 u2 = floor((float2(-td.y, td.x) * tt + IN.touchDisplaceUV) * _ScreenParams.xy / tp) * tp + tp * 0.5;
        u1 /= _ScreenParams.xy;
        u2 /= _ScreenParams.xy;

        float da = tex2D(_DisplaceTex, u1).x;
        float db = tex2D(_DisplaceTex, u2).x;
        float ha = da - 0.5, hb = db - 0.5;
        float2 hv;
        hv.x = (-td.y) * hb;
        hv.y = td.x * hb;
        hv = td * ha + hv;
        hv = hv * _TouchDisplaceStrength + IN.sceneUV;

        float u_xlat16_0b = hoverSample(hv);
        float2 screenUV = IN.screenPos.xy / IN.screenPos.w;

        // 噪声方向：两个 hash 之间做时间平滑插值。
        // 官方把 u_xlat48 原地覆盖成了 smoothstep 结果，插值用的是它而不是原始 fract。
        float u48 = _UnityTime.y * _NoiseDirChangeSpeed;
        float seedA = floor(u48);
        float fr = frac(u48);
        float dirBlend = smooth01(fr);

        float4 h4;
        h4.xz = seedA * 0.103100002;
        h4.y = 0.381470025;
        h4.w = 0.93821007;
        h4 = frac(h4);
        float3 h17 = h4.yxx + 33.3300018;
        float h17x = dot(h4.xyx, h17);
        float2 h17xy = h17x.xx + h4.xy;
        float h33 = h17xy.y + h17xy.x;
        h17x = h17x * h33;
        float h6x = frac(h17x);
        float3 h17b = h4.wzz + 33.3300018;
        h17x = dot(h4.zwz, h17b);
        float2 h17bxy = h17x.xx + h4.zw;
        float h33b = h17bxy.y + h17bxy.x;
        h17x = h17x * h33b;
        float h6y = frac(h17x);
        float2 dirA = float2(h6x, h6y) * 2.0 + float2(-1.0, -1.0);

        float seedB = seedA + 1.0;
        float4 g4;
        g4.xz = seedB * 0.103100002;
        g4.y = 0.381470025;
        g4.w = 0.93821007;
        g4 = frac(g4);
        float3 g6 = g4.yxx + 33.3300018;
        float g1 = dot(g4.xyx, g6);
        float2 g1w = g1.xx + g4.xy;
        float g49 = g1w.y + g1w.x;
        g1 = g1 * g49;
        float gc6x = frac(g1);
        float3 g11 = g4.wzz + 33.3300018;
        g1 = dot(g4.zwz, g11);
        float2 g1w2 = g1.xx + g4.zw;
        g49 = g1w2.y + g1w2.x;
        g1 = g1 * g49;
        float gc6y = frac(g1);
        float2 dirB = float2(gc6x, gc6y) * 2.0 + float2(-1.0, -1.0);

        float2 dirMix = dirB - dirA;
        dirMix = dirBlend * dirMix + dirA;
        dirMix = dirMix * _NoiseEvoSpeed + IN.noiseUV;
        float2 nxy = tex2D(_NoiseTex, dirMix).xy + float2(-0.5, -0.5);

        // hover mask 的 smoothstep 重映射（半径 ± 平滑度）
        float lo = _NoiseRadius - _NoiseSmoothness;
        float hi = _NoiseRadius + _NoiseSmoothness;
        float hm = (u_xlat16_0b - lo) / (hi - lo);
        hm = clamp(hm, 0.0, 1.0);
        hm = smooth01(hm);

        // SDF 网格
        float cell = _ScreenParams.y * _SDFCellSize;
        float2 dxy = nxy * 2.0;
        dxy = dxy * _NoiseDisplaceStrength;
        dxy = cell * dxy;
        dxy = dxy / _ScreenParams.xy;
        float2 cellUV = screenUV + dxy;
        cellUV = cellUV * _ScreenParams.xy / cell;
        float2 cellBase = floor(cellUV);
        float2 cellFrac = frac(cellUV);

        float mvt = _UnityTime.y * _SDFMoveSpeed;
        float mvSeed = floor(mvt);
        float mvBlend = smooth01(frac(mvt));

        float2 cellIdx = mvSeed + cellBase;

        float2 c6 = frac(cellIdx * 0.103100002);
        float3 c11 = c6.yxx + 33.3300018;
        float c49 = dot(c6.xyx, c11);
        float2 c6b = c49.xx + c6.xy;
        float c49b = c6b.y + c6b.x;
        c49b = c6b.x * c49b;
        float cCenterX = frac(c49b);

        float4 c4 = float4(cellIdx.y, cellIdx.x, cellIdx.y, cellIdx.x) + float4(17.1700001, 17.1700001, 1.0, 1.0);
        c4 = c4 * 0.103100002;
        c4 = frac(c4);
        float4 c5v = c4 + 33.3300018;
        float c49c = dot(c4.yxy, c5v.xyy);
        float2 c38 = c49c.xx + c4.yx;
        float c49d = c38.y + c38.x;
        c49d = c38.x * c49d;
        float cCenterY = frac(c49d);

        c49c = dot(c4.wzw, c5v.zww);
        float2 c38b = c49c.xx + c4.wz;
        float c49e = c38b.y + c38b.x;
        c49e = c38b.x * c49e;
        float cThird = frac(c49e);

        float2 idxOff = cellIdx + float2(18.1700001, 18.1700001);
        idxOff = idxOff * 0.103100002;
        idxOff = frac(idxOff);
        float3 c12 = idxOff.yxx + 33.3300018;
        float c49f = dot(idxOff.xyx, c12);
        float2 c1xy = c49f.xx + idxOff.xy;
        float c17 = c1xy.y + c1xy.x;
        c49f = c1xy.x * c17;
        float cCenterZ = frac(c49f);

        // 官方：(u_xlat11.xy - u_xlat6.xy) —— 即 (第三, 第四) - (第一, 第二)
        float2 centers = float2(cCenterX, cCenterY);
        float2 delta = float2(cThird, cCenterZ) - centers;
        centers = mvBlend * delta + centers;
        float2 diff = cellFrac - centers;
        float dist = length(diff);
        dist = dist - _SDFFalloff;
        dist = (1.0 / _SDFSmoothness) * dist;
        dist = clamp(dist, 0.0, 1.0);
        float sdfMask = smooth01(dist);

        float3 noiseCol = sdfMask * _NoiseTint.xyz + sdfMask;
        noiseCol = clamp(noiseCol, 0.0, 1.0);
        float3 noiseMix = hm * noiseCol;

        float hoverS = clamp(u_xlat16_0b, 0.0, 1.0);
        hoverS = hoverS * hoverS * (hoverS * -2.0 + 3.0);
        float3 touchGlow = hoverS * _TouchGlowColor.xyz;
        float3 glowProd = noiseMix * touchGlow;
        float3 glowDoubled = glowProd + glowProd;

        float3 invNoise = (-noiseCol) * hm + float3(1.0, 1.0, 1.0);
        invNoise = invNoise + invNoise;
        float3 invGlow = (-hoverS) * _TouchGlowColor.xyz + float3(1.0, 1.0, 1.0);
        invNoise = (-invNoise) * invGlow + float3(1.0, 1.0, 1.0);

        float3 selC;
        selC.x = noiseMix.x >= 0.5 ? 1.0 : 0.0;
        selC.y = noiseMix.y >= 0.5 ? 1.0 : 0.0;
        selC.z = noiseMix.z >= 0.5 ? 1.0 : 0.0;

        float3 mixIn = (-glowProd) * 2.0 + invNoise;
        float3 mixed = selC * mixIn + glowDoubled;
        mixed = clamp(mixed, 0.0, 1.0);
        hoverCol = touchGlow * 0.5 + mixed;
    }

    // 官方：result.xyz = hover + shine * (ready/disabled 可见度) + active 颜色
    result.xyz = hoverCol + shine * u_xlat16_18 + activeCol;
    return result;
}

#endif
