// High-speed wake post process, applied inside the level buffer (high
// resolution under MotionSmoothing). One pixel shader pass does everything,
// driven by a small field texture (R/G = displacement vector around neutral
// 0.5, B = wake strength):
//   1. warp the sample position by the field vector,
//   2. continuous disk blur whose radius follows wake strength (0 at the rim,
//      full in the core),
//   3. wake-local chromatic aberration and a multiplicative brightness lift.
// Pixels on the player and samples landing on the sprite (exact animation
// mask) fall back to the untouched frame. Compiled with fxc /T fx_2_0.

texture ScreenTex;
texture FieldTex;
texture PlayerMaskTex;

sampler2D screenS : register(s0) = sampler_state {
    Texture = (ScreenTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};
sampler2D fieldS : register(s1) = sampler_state {
    Texture = (FieldTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};
sampler2D playerMaskS : register(s2) = sampler_state {
    Texture = (PlayerMaskTex); MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};

float WarpScale;
float2 CaShift;
float BrightnessLift;
float BlurAmount;
float BlurRadiusUV;
float2 PlayerUV;
float2 PlayerMaskSpan;
float2 ScreenTexel;

float Smoothstep01(float x) { return x * x * (3.0 - 2.0 * x); }

// The player rendered alone into transparency: its alpha is the exact
// avoidance mask, pixel-accurate for the current animation frame.
float PlayerMask(float2 uv) {
    return tex2D(playerMaskS, (uv - PlayerUV) / PlayerMaskSpan + 0.5).a;
}

// Two rings of taps on the unit disk (inner 4 at r=0.5, outer 8).
static const float2 DiskTaps[12] = {
    float2(0.5, 0.0), float2(-0.5, 0.0), float2(0.0, 0.5), float2(0.0, -0.5),
    float2(1.0, 0.0), float2(-1.0, 0.0), float2(0.0, 1.0), float2(0.0, -1.0),
    float2(0.7071, 0.7071), float2(-0.7071, 0.7071),
    float2(0.7071, -0.7071), float2(-0.7071, -0.7071)
};

float4 WakePixel(float2 uv : TEXCOORD0) : COLOR0 {
    float4 field = tex2D(fieldS, uv);
    float2 vec = field.rg - 0.5;
    // No effect on the player itself.
    float strength = field.b * (1.0 - PlayerMask(uv));
    float2 wuv = uv + vec * WarpScale;
    // Continuous disk blur: the radius grows linearly with wake strength, so
    // the rim is sharp and the core is fully blurred — no discrete tiers.
    float radius = strength * BlurRadiusUV * BlurAmount;
    float3 col = tex2D(screenS, wuv).rgb;
    if (radius > 0.0001) {
        float3 sum = col;
        for (int i = 0; i < 12; i++) {
            sum += tex2D(screenS, wuv + DiskTaps[i] * radius).rgb;
        }
        col = sum / 13.0;
    }
    // Wake-local chromatic aberration.
    float fringe = saturate(strength / 0.75);
    float3 shifted = float3(
        tex2D(screenS, wuv + CaShift).r,
        col.g,
        tex2D(screenS, wuv - CaShift).b);
    col = lerp(col, shifted, fringe);
    // Multiplicative brightness (like a CSS filter), not a white overlay.
    col *= 1.0 + BrightnessLift * strength;
    // Compose inside the shader: outside the wake, emit the untouched frame
    // with nearest-neighbour sampling so the crisp look survives; the result
    // is written back with opaque blending, no dst dependency.
    float2 nnUv = (floor(uv / ScreenTexel) + 0.5) * ScreenTexel;
    float3 base = tex2D(screenS, nnUv);
    float coverage = saturate(strength / 0.55);
    coverage = Smoothstep01(coverage);
    // Samples that would land on the sprite fall back to the untouched frame.
    coverage *= 1.0 - PlayerMask(wuv);
    col = lerp(base, col, coverage);
    return float4(col, 1);
}

technique WakeTechnique {
    pass P0 { PixelShader = compile ps_3_0 WakePixel(); }
}
