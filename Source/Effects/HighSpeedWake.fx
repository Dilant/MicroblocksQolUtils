// High-speed wake post process, applied to the composed screen at render
// resolution. One pixel shader pass does everything, driven by a small field
// texture (R/G = displacement vector around neutral 0.5, B = wake strength):
//   1. warp the sample position by the field vector,
//   2. lerp sharp -> half-res -> quarter-res blur by wake strength, giving a
//      continuous blur-radius gradient along the wake,
//   3. wake-local chromatic aberration and a brightness lift.
// Compiled with fxc /T fx_2_0 (MojoShader/FNA compatible).

texture ScreenTex;
texture FieldTex;
texture BlurHalfTex;
texture BlurQuarterTex;

sampler2D screenS = sampler_state {
    Texture = (ScreenTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};
sampler2D fieldS = sampler_state {
    Texture = (FieldTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};
sampler2D halfS = sampler_state {
    Texture = (BlurHalfTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};
sampler2D quarterS = sampler_state {
    Texture = (BlurQuarterTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};

float WarpScale;
float2 CaShift;
float BrightnessLift;
float BlurAmount;

float Smoothstep01(float x) { return x * x * (3.0 - 2.0 * x); }

float4 WakePixel(float2 uv : TEXCOORD0) : COLOR0 {
    float4 field = tex2D(fieldS, uv);
    float2 vec = field.rg - 0.5;
    float strength = field.b;
    float2 wuv = uv + vec * WarpScale;
    float3 col = tex2D(screenS, wuv);
    // Graded blur: radius follows wake strength continuously.
    float nearT = saturate((strength - 0.06) / 0.26);
    float farT = saturate((strength - 0.28) / 0.34);
    nearT = Smoothstep01(nearT) * BlurAmount;
    farT = Smoothstep01(farT) * BlurAmount;
    col = lerp(col, tex2D(halfS, wuv), nearT);
    col = lerp(col, tex2D(quarterS, wuv), farT);
    // Wake-local chromatic aberration.
    float fringe = saturate(strength * 1.3);
    float3 shifted = float3(
        tex2D(screenS, wuv + CaShift).r,
        col.g,
        tex2D(screenS, wuv - CaShift).b);
    col = lerp(col, shifted, fringe);
    col += BrightnessLift * strength;
    // Premultiplied coverage: solid inside the wake, fading to zero at the rim
    // so untouched pixels keep the vanilla pixel-perfect upscale underneath.
    float coverage = saturate((strength - 0.05) / 0.20);
    coverage = Smoothstep01(coverage);
    return float4(col * coverage, coverage);
}

technique WakeTechnique {
    pass P0 { PixelShader = compile ps_2_0 WakePixel(); }
}
