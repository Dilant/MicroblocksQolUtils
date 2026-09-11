// High-speed wake post process, applied to the composed screen at render
// resolution. One pixel shader pass does everything, driven by a small field
// texture (R/G = displacement vector around neutral 0.5, B = wake strength):
//   1. warp the sample position by the field vector,
//   2. lerp sharp -> half -> quarter -> eighth resolution blur by wake
//      strength with rounded cross-tap kernels, giving a continuous blur-radius
//      gradient along the wake,
//   3. wake-local chromatic aberration and a brightness lift.
// All sampling avoids the player (an ellipse in UV space) so the sprite is
// never dragged into the wake. Compiled with fxc /T fx_2_0 (MojoShader/FNA
// compatible).

texture ScreenTex;
texture FieldTex;
texture BlurHalfTex;
texture BlurQuarterTex;
texture BlurEighthTex;

sampler2D screenS : register(s0) = sampler_state {
    Texture = (ScreenTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};
sampler2D fieldS : register(s1) = sampler_state {
    Texture = (FieldTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};
sampler2D halfS : register(s2) = sampler_state {
    Texture = (BlurHalfTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};
sampler2D quarterS : register(s3) = sampler_state {
    Texture = (BlurQuarterTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};
sampler2D eighthS : register(s4) = sampler_state {
    Texture = (BlurEighthTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};

float WarpScale;
float2 CaShift;
float BrightnessLift;
float BlurAmount;
float2 PlayerUV;
float PlayerRadiusUV;

float Smoothstep01(float x) { return x * x * (3.0 - 2.0 * x); }

// Pushes a sample position out of the player's ellipse so the sprite is never
// smeared into the wake by displaced sampling. Branch-free.
float2 AvoidPlayer(float2 uv) {
    float2 delta = uv - PlayerUV;
    float len = max(length(delta), 1e-5);
    float scale = min(len, PlayerRadiusUV) / len;
    return PlayerUV + delta * scale;
}

// Rounded cross-tap sample for the blur pyramid levels (fx_2_0 cannot pass
// samplers to functions, so this is a macro).
#define BLUR_TAP(s, uv, tx, ty) \
    ((tex2D(s, uv).rgb \
    + tex2D(s, uv + float2(tx, 0)).rgb \
    + tex2D(s, uv - float2(tx, 0)).rgb \
    + tex2D(s, uv + float2(0, ty)).rgb \
    + tex2D(s, uv - float2(0, ty)).rgb) * 0.2)

float4 WakePixel(float2 uv : TEXCOORD0) : COLOR0 {
    float4 field = tex2D(fieldS, uv);
    float2 vec = field.rg - 0.5;
    float strength = field.b;
    float2 wuv = AvoidPlayer(uv + vec * WarpScale);
    float3 col = tex2D(screenS, wuv);
    // Graded blur: radius follows wake strength continuously across three
    // pyramid levels, each sampled with a rounded cross-tap kernel.
    float nearT = saturate((strength - 0.06) / 0.22);
    float farT = saturate((strength - 0.26) / 0.26);
    float deepT = saturate((strength - 0.52) / 0.30);
    nearT = Smoothstep01(nearT) * BlurAmount;
    farT = Smoothstep01(farT) * BlurAmount;
    deepT = Smoothstep01(deepT) * BlurAmount;
    col = lerp(col, BLUR_TAP(halfS, wuv, 0.011, 0.011), nearT);
    col = lerp(col, BLUR_TAP(quarterS, wuv, 0.022, 0.022), farT);
    col = lerp(col, BLUR_TAP(eighthS, wuv, 0.044, 0.044), deepT);
    // Wake-local chromatic aberration.
    float fringe = saturate(strength * 1.3);
    float3 shifted = float3(
        tex2D(screenS, AvoidPlayer(wuv + CaShift)).r,
        col.g,
        tex2D(screenS, AvoidPlayer(wuv - CaShift)).b);
    col = lerp(col, shifted, fringe);
    col += BrightnessLift * strength;
    // Compose inside the shader: outside the wake, emit the untouched frame
    // with nearest-neighbour sampling so the pixel-perfect upscale survives;
    // the result is written back with opaque blending, no dst dependency.
    float2 nnUv = (floor(uv * float2(320.0, 180.0)) + 0.5) / float2(320.0, 180.0);
    float3 base = tex2D(screenS, nnUv);
    float coverage = saturate((strength - 0.05) / 0.20);
    coverage = Smoothstep01(coverage);
    col = lerp(base, col, coverage);
    return float4(col, 1);
}

technique WakeTechnique {
    pass P0 { PixelShader = compile ps_3_0 WakePixel(); }
}
