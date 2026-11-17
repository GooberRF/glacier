using Ged.Rendering.Rhi;

namespace Ged.Rendering.Graphics;

/// <summary>
/// Source for every viewport shader program, one <see cref="RhiShaderSource"/>
/// each. The HLSL is compiled at runtime via D3DCompile (entry points
/// <c>VSMain</c>/<c>PSMain</c>/<c>PSPick</c>). The game is not sRGB-managed, so
/// shading is done in the texture's native (non-gamma-corrected) space and the
/// lightmap combine is the game-accurate modulate-2x (128 = neutral); see
/// docs/research/red-lighting-model.md §(d).
/// <para>
/// L2 (OpenGL 3.3): add the GLSL 330 sources SIDE-BY-SIDE by populating the
/// <see cref="RhiShaderSource.GlslVertex"/>/<see cref="RhiShaderSource.GlslFragment"/>/
/// <see cref="RhiShaderSource.GlslPickFragment"/> members below. The cbuffer
/// layouts (register b0 = <c>FrameConstants</c> 160 B, b1 = <c>DrawConstants</c>
/// 96 B) must map to std140 UBOs at binding points 0 and 1 with identical field
/// offsets; the vertex inputs must be declared <c>layout(location = N)</c> in the
/// same order as the layout arrays in <see cref="ShaderPrograms"/>.
/// </para>
/// </summary>
internal static class Shaders
{
    /// <summary>Shared constant buffers, textures and sampler.</summary>
    private const string Common = CommonCb + @"
Texture2D    Tex0 : register(t0);
Texture2D    Tex1 : register(t1);
SamplerState Samp : register(s0);
";

    /// <summary>The shared constant buffers and fog.</summary>
    private const string CommonCb = @"
cbuffer FrameConstants : register(b0)
{
    float4x4 ViewProj;
    float4   CameraRight;
    float4   CameraUp;
    float4   CameraPos;
    float4   Params;      // x=globalAlpha y=lmScale z=mode w=animTime(sec)
    float4   FogColor;    // xyz=color w=enable
    float4   FogParams;   // x=start y=end(farclip)
};

// Distance fog: blend outc toward FogColor by camera distance when enabled.
float3 ApplyFog(float3 outc, float3 worldPos)
{
    if (FogColor.w < 0.5) return outc;
    float dist = length(CameraPos.xyz - worldPos);
    float f = saturate((dist - FogParams.x) / max(FogParams.y - FogParams.x, 0.001));
    return lerp(outc, FogColor.rgb, f);
}
cbuffer DrawConstants : register(b1)
{
    float4x4 World;
    float4   Tint;
    uint     PickId;
    uint     HasLightmap;
    float2   Scroll;      // diffuse-UV scroll velocity (added as Scroll * animTime)
};
";

    // ---- Static / mover geometry ----
    private const string WorldHlsl = Common + @"
struct VSIn  { float3 pos:POSITION; float3 nrm:NORMAL; float2 uv:TEXCOORD0; float2 uv2:TEXCOORD1; float4 col:COLOR0; uint pid:PICKID; };
struct VSOut { float4 clip:SV_Position; float2 uv:TEXCOORD0; float2 uv2:TEXCOORD1; float4 col:COLOR0; float3 nrm:NORMAL; float3 wp:TEXCOORD2; nointerpolation uint pid:PICKID; };

VSOut VSMain(VSIn i)
{
    VSOut o;
    float4 wp = mul(World, float4(i.pos, 1.0));
    o.clip = mul(ViewProj, wp);
    o.uv = i.uv; o.uv2 = i.uv2; o.col = i.col; o.nrm = i.nrm; o.wp = wp.xyz; o.pid = i.pid;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    int mode = (int)Params.z;
    // In-shader UV scroll (liquid conveyors etc.): offset the diffuse UV by the
    // per-batch scroll velocity * animation time. Lightmap UV (uv2) is unaffected.
    float2 duv = i.uv + Scroll * Params.w;
    float4 baseC = Tex0.Sample(Samp, duv);
    float3 lm = (HasLightmap != 0) ? Tex1.Sample(Samp, i.uv2).rgb : float3(0.5, 0.5, 0.5);
    float3 outc;
    if (mode == 1)       outc = baseC.rgb * lm * Params.y;   // textures x lightmap 2x
    else if (mode == 2)  outc = lm * Params.y;               // just lightmaps
    else if (mode == 3)  outc = i.col.rgb;                   // room colors
    else if (mode == 4)  outc = float3(0.82, 0.82, 0.88);    // wireframe flat
    else                 outc = baseC.rgb;                   // just textures
    outc *= Tint.rgb;
    outc = ApplyFog(outc, i.wp);
    float a = baseC.a * Tint.a * Params.x;
    return float4(outc, a);
}

uint PSPick(VSOut i) : SV_Target { return i.pid; }
";

    // ---- V3M meshes ----
    private const string MeshHlsl = Common + @"
struct VSIn  { float3 pos:POSITION; float3 nrm:NORMAL; float2 uv:TEXCOORD0; };
struct VSOut { float4 clip:SV_Position; float2 uv:TEXCOORD0; float3 nrm:NORMAL; float3 wp:TEXCOORD1; };

VSOut VSMain(VSIn i)
{
    VSOut o;
    float4 wp = mul(World, float4(i.pos, 1.0));
    o.clip = mul(ViewProj, wp);
    o.uv = i.uv;
    o.nrm = mul((float3x3)World, i.nrm);
    o.wp = wp.xyz;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float4 baseC = Tex0.Sample(Samp, i.uv);
    float3 n = normalize(i.nrm);
    // Headlight: a light co-located with the camera so any face turned toward the
    // viewer is lit and no visible surface reads pure black (thumbnails + viewport).
    float3 toCam = normalize(CameraPos.xyz - i.wp);
    float head = saturate(dot(n, toCam));
    // A fixed top key light adds form so a face viewed flat-on still shows shape.
    float key = saturate(dot(n, normalize(float3(0.35, 0.85, 0.4))));
    // HasLightmap doubles as the mesh fullbright flag (meshes never sample a lightmap):
    // VFX fullbright / self-illuminated draws bypass lighting and render at full brightness.
    float shade = (HasLightmap != 0) ? 1.0 : min(0.35 + 0.5 * head + 0.2 * key, 1.1);
    float3 outc = baseC.rgb * shade * Tint.rgb;
    outc = ApplyFog(outc, i.wp);
    return float4(outc, baseC.a * Tint.a * Params.x);
}

uint PSPick(VSOut i) : SV_Target { return PickId; }
";

    // ---- Alpine terrain (top surface): Alpine's multi-layer weight blend ----
    // Reimplements the blend of Alpine Faction's Direct3D 11 terrain pixel shader (resources/shaders/standard_ps.hlsl:
    // 1222-1335 ter_coords / ter_triplanar_weights / ter_albedo, 1346-1457 overlays, 1492-1506 the chart light): each
    // layer's texture in planar XZ space at its UV scale (u = x / scale, v = z / scale, origin-relative for precision),
    // triplanar where the layer asks (|n|^4 weights, walls stood upright as wall_uv lays them), the 8 weights bilinearly
    // read from the two RGBA8 maps over the footprint, only the terrain's own layers counting, renormalised (layer 0 when
    // none is left); overlays laid over in order (albedo = albedo (1 - a cov) + rgb a cov); the baked chart at the
    // fragment's XZ as a lightmap texel. Textures: Tex0-7 layers, Tex8/9 weights, Tex10 chart, Tex11-14 overlays, Tex15
    // overlay coverage. The map samplers clamp by keeping the coordinate within the texel centres.
    private const string TerrainCb = @"
cbuffer TerrainConstants : register(b2)
{
    float4 TerOrigin;     // xyz origin (min-X / min-Z corner), w layer count
    float4 TerExtent;     // xy footprint (x, z), zw weight map size in texels
    float4 TerUv0;        // layer UV scales 0-3
    float4 TerUv1;        // layer UV scales 4-7
    float4 TerTri0;       // layer triplanar flags 0-3
    float4 TerTri1;       // layer triplanar flags 4-7
    float4 TerOvUv;       // overlay UV scales
    float4 TerOvTri;      // overlay triplanar flags
    float4 TerOvOn;       // overlay present (1) or not (0)
    float4 TerChart;      // xy chart origin (x, z), z texel size, w 1 = a chart applies
    float4 TerChartSize;  // xy chart texels, z 1 = fullbright
};
";

    private const string TerrainHlsl = CommonCb + TerrainCb + @"
Texture2D Tex0 : register(t0);  Texture2D Tex1 : register(t1);  Texture2D Tex2 : register(t2);  Texture2D Tex3 : register(t3);
Texture2D Tex4 : register(t4);  Texture2D Tex5 : register(t5);  Texture2D Tex6 : register(t6);  Texture2D Tex7 : register(t7);
Texture2D Tex8 : register(t8);  Texture2D Tex9 : register(t9);  Texture2D Tex10 : register(t10); Texture2D Tex11 : register(t11);
Texture2D Tex12 : register(t12); Texture2D Tex13 : register(t13); Texture2D Tex14 : register(t14); Texture2D Tex15 : register(t15);
SamplerState Samp : register(s0);

struct VSIn  { float3 pos:POSITION; float3 nrm:NORMAL; float2 uv:TEXCOORD0; float2 uv2:TEXCOORD1; float4 col:COLOR0; uint pid:PICKID; };
struct VSOut { float4 clip:SV_Position; float4 col:COLOR0; float3 nrm:NORMAL; float3 wp:TEXCOORD0; nointerpolation uint pid:PICKID; };

VSOut VSMain(VSIn i)
{
    VSOut o;
    float4 wp = mul(World, float4(i.pos, 1.0));
    o.clip = mul(ViewProj, wp);
    o.col = i.col; o.nrm = i.nrm; o.wp = wp.xyz; o.pid = i.pid;
    return o;
}

float2 TerMapUv(float2 xz, float2 origin, float2 extent, float2 size)
{
    float2 h = 0.5 / max(size, float2(1.0, 1.0));
    return clamp((xz - origin) / extent, h, 1.0 - h);
}

float4 TerTex(Texture2D t, float3 wp, float s, float tri, float3 tw)
{
    float inv = 1.0 / s;
    float3 p = (wp - TerOrigin.xyz) * inv + frac(TerOrigin.xyz * inv);
    float4 planar = t.Sample(Samp, p.xz);
    float4 sides = planar * tw.y + t.Sample(Samp, float2(p.z, -p.y)) * tw.x + t.Sample(Samp, float2(p.x, -p.y)) * tw.z;
    return lerp(planar, sides, step(0.5, tri));
}

float3 TerOverlay(float3 albedo, float4 s, float cov)
{
    return albedo * (1.0 - s.a * cov) + s.rgb * s.a * cov;
}

float4 PSMain(VSOut i) : SV_Target
{
    int mode = (int)Params.z;
    float3 n = normalize(i.nrm);
    float3 tw = pow(abs(n), 4.0);
    tw /= max(tw.x + tw.y + tw.z, 1e-4);
    float2 muv = TerMapUv(i.wp.xz, TerOrigin.xz, TerExtent.xy, TerExtent.zw);
    float4 w0 = Tex8.Sample(Samp, muv) * step(float4(0.5, 1.5, 2.5, 3.5), TerOrigin.wwww);
    float4 w1 = Tex9.Sample(Samp, muv) * step(float4(4.5, 5.5, 6.5, 7.5), TerOrigin.wwww);
    float sum = dot(w0, float4(1.0, 1.0, 1.0, 1.0)) + dot(w1, float4(1.0, 1.0, 1.0, 1.0));
    if (sum < 1e-4) { w0 = float4(1.0, 0.0, 0.0, 0.0); w1 = float4(0.0, 0.0, 0.0, 0.0); sum = 1.0; }
    w0 /= sum; w1 /= sum;
    float3 c = w0.x * TerTex(Tex0, i.wp, TerUv0.x, TerTri0.x, tw).rgb
             + w0.y * TerTex(Tex1, i.wp, TerUv0.y, TerTri0.y, tw).rgb
             + w0.z * TerTex(Tex2, i.wp, TerUv0.z, TerTri0.z, tw).rgb
             + w0.w * TerTex(Tex3, i.wp, TerUv0.w, TerTri0.w, tw).rgb
             + w1.x * TerTex(Tex4, i.wp, TerUv1.x, TerTri1.x, tw).rgb
             + w1.y * TerTex(Tex5, i.wp, TerUv1.y, TerTri1.y, tw).rgb
             + w1.z * TerTex(Tex6, i.wp, TerUv1.z, TerTri1.z, tw).rgb
             + w1.w * TerTex(Tex7, i.wp, TerUv1.w, TerTri1.w, tw).rgb;
    float4 cov = Tex15.Sample(Samp, muv) * TerOvOn;
    c = TerOverlay(c, TerTex(Tex11, i.wp, TerOvUv.x, TerOvTri.x, tw), cov.x);
    c = TerOverlay(c, TerTex(Tex12, i.wp, TerOvUv.y, TerOvTri.y, tw), cov.y);
    c = TerOverlay(c, TerTex(Tex13, i.wp, TerOvUv.z, TerOvTri.z, tw), cov.z);
    c = TerOverlay(c, TerTex(Tex14, i.wp, TerOvUv.w, TerOvTri.w, tw), cov.w);

    float2 cuv = TerMapUv(i.wp.xz, TerChart.xy, TerChartSize.xy * TerChart.z, TerChartSize.xy);
    float3 lm = lerp(float3(0.5, 0.5, 0.5), Tex10.Sample(Samp, cuv).rgb, step(0.5, TerChart.w) * (1.0 - step(0.5, TerChartSize.z)));
    float3 outc;
    if (mode == 1)       outc = c * lm * Params.y;
    else if (mode == 2)  outc = lm * Params.y;
    else if (mode == 3)  outc = i.col.rgb;
    else if (mode == 4)  outc = float3(0.82, 0.82, 0.88);
    else                 outc = c;
    outc *= Tint.rgb;
    outc = ApplyFog(outc, i.wp);
    return float4(outc, Tint.a * Params.x);
}

uint PSPick(VSOut i) : SV_Target { return i.pid; }
";

    // ---- Billboards (point-object glyphs) ----
    private const string BillboardHlsl = Common + @"
struct VSIn  { float3 center:POSITION; float2 corner:TEXCOORD0; float2 uv:TEXCOORD1; float4 col:COLOR0; uint pid:PICKID; };
struct VSOut { float4 clip:SV_Position; float2 uv:TEXCOORD0; float4 col:COLOR0; nointerpolation uint pid:PICKID; };

VSOut VSMain(VSIn i)
{
    float3 wp = i.center + CameraRight.xyz * i.corner.x + CameraUp.xyz * i.corner.y;
    VSOut o;
    o.clip = mul(ViewProj, float4(wp, 1.0));
    // Object glyphs (PickKind.Object = 2 in the id's high nibble) take their depth from a point pulled
    // toward the camera by the glyph's half-height: an object standing on a floor or against a wall keeps
    // its whole icon visible and clickable instead of losing the half (and the centre pixel) that sits
    // at or behind that surface. Screen position is unchanged; real walls in front still occlude it.
    if ((i.pid >> 28) == 2u)
    {
        float3 fwd = normalize(cross(CameraRight.xyz, CameraUp.xyz));
        float4 pulled = mul(ViewProj, float4(wp - fwd * abs(i.corner.y), 1.0));
        float ndcz = pulled.w > 1e-4 ? saturate(pulled.z / pulled.w) : 0.0;
        o.clip.z = ndcz * o.clip.w;
    }
    o.uv = i.uv; o.col = i.col; o.pid = i.pid;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    // Icon atlas: white core (tinted by the object colour) + dark rim in RGB,
    // coverage in alpha. Multiply RGB by the tint so the rim stays dark.
    float4 c = Tex0.Sample(Samp, i.uv);
    if (c.a < 0.02) discard;
    return float4(c.rgb * i.col.rgb, c.a * i.col.a);
}

uint PSPick(VSOut i) : SV_Target
{
    // An object glyph is clickable over its whole square, so the middle of a ring- or outline-shaped icon
    // (respawn, target, regions, path node) selects it; other glyphs (vertex dots) keep their drawn shape.
    float a = Tex0.Sample(Samp, i.uv).a;
    if (a < 0.5 && (i.pid >> 28) != 2u) discard;
    return i.pid;
}
";

    // ---- Lines (grid / links / ranges / outlines / wireframe overlay) ----
    private const string LineHlsl = Common + @"
struct VSIn  { float3 pos:POSITION; float4 col:COLOR0; };
struct VSOut { float4 clip:SV_Position; float4 col:COLOR0; };

VSOut VSMain(VSIn i)
{
    VSOut o;
    o.clip = mul(ViewProj, float4(i.pos, 1.0));
    o.col = i.col;
    return o;
}

float4 PSMain(VSOut i) : SV_Target { return i.col; }
";

    // ============================================================================
    // GLSL 330 ports (L2 OpenGL backend), side-by-side with the HLSL above.
    //
    // Parity rules applied throughout (see D3D11RenderDevice / GlRenderDevice remarks):
    //   * cbuffer b0/b1  -> std140 blocks FrameConstants/DrawConstants (bound to points
    //     0/1 by the device); IDENTICAL field order/offsets to the HLSL cbuffers.
    //   * mul(M, v)      -> M * v   (untransposed upload; GLSL reads the std140 mat4
    //     column-major, the same transpose HLSL's cbuffer read applies).
    //   * mul((float3x3)World, n) -> mat3(World) * n.
    //   * vertex inputs  -> layout(location = N) in the SAME ordinal order as the
    //     ShaderPrograms layout arrays; PICKID uses a uint attribute (glVertexAttribIPointer).
    //   * nointerpolation uint -> flat (integers cannot be interpolated).
    //   * gl_Position is emitted via GED_EMIT_CLIP so the device can apply the
    //     no-clip-control depth fixup transparently (clip-control path is a plain assign).
    //   * Tex0/Tex1 samplers -> texture units 0/1 (bound by the device).
    // ============================================================================
    private const string GlslCommon = GlslCommonCb + @"
uniform sampler2D Tex0;
uniform sampler2D Tex1;
";

    private const string GlslCommonCb = @"#version 330 core
layout(std140) uniform FrameConstants
{
    mat4 ViewProj;
    vec4 CameraRight;
    vec4 CameraUp;
    vec4 CameraPos;
    vec4 Params;      // x=globalAlpha y=lmScale z=mode w=animTime(sec)
    vec4 FogColor;    // xyz=color w=enable
    vec4 FogParams;   // x=start y=end(farclip)
};
layout(std140) uniform DrawConstants
{
    mat4 World;
    vec4 Tint;
    uint PickId;
    uint HasLightmap;
    vec2 Scroll;      // diffuse-UV scroll velocity (added as Scroll * animTime)
};

vec3 ApplyFog(vec3 outc, vec3 worldPos)
{
    if (FogColor.w < 0.5) return outc;
    float dist = length(CameraPos.xyz - worldPos);
    float f = clamp((dist - FogParams.x) / max(FogParams.y - FogParams.x, 0.001), 0.0, 1.0);
    return mix(outc, FogColor.rgb, f);
}
";

    // ---- Static / mover geometry ----
    private const string WorldGlslVertex = GlslCommon + @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec2 aUv2;
layout(location = 4) in vec4 aCol;
layout(location = 5) in uint aPid;
out vec2 vUv;
out vec2 vUv2;
out vec4 vCol;
out vec3 vWp;
flat out uint vPid;

void main()
{
    vec4 wp = World * vec4(aPos, 1.0);
    GED_EMIT_CLIP(ViewProj * wp);
    vUv = aUv; vUv2 = aUv2; vCol = aCol; vWp = wp.xyz; vPid = aPid;
}
";

    private const string WorldGlslFragment = GlslCommon + @"
in vec2 vUv;
in vec2 vUv2;
in vec4 vCol;
in vec3 vWp;
out vec4 fragColor;

void main()
{
    int mode = int(Params.z);
    vec2 duv = vUv + Scroll * Params.w;
    vec4 baseC = texture(Tex0, duv);
    vec3 lm = (HasLightmap != 0u) ? texture(Tex1, vUv2).rgb : vec3(0.5, 0.5, 0.5);
    vec3 outc;
    if (mode == 1)      outc = baseC.rgb * lm * Params.y;
    else if (mode == 2) outc = lm * Params.y;
    else if (mode == 3) outc = vCol.rgb;
    else if (mode == 4) outc = vec3(0.82, 0.82, 0.88);
    else                outc = baseC.rgb;
    outc *= Tint.rgb;
    outc = ApplyFog(outc, vWp);
    float a = baseC.a * Tint.a * Params.x;
    fragColor = vec4(outc, a);
}
";

    private const string WorldGlslPick = GlslCommon + @"
flat in uint vPid;
out uint fragId;
void main() { fragId = vPid; }
";

    // ---- Alpine terrain (see the HLSL) ----
    private const string TerrainGlslBlock = @"
layout(std140) uniform TerrainConstants
{
    vec4 TerOrigin;
    vec4 TerExtent;
    vec4 TerUv0;
    vec4 TerUv1;
    vec4 TerTri0;
    vec4 TerTri1;
    vec4 TerOvUv;
    vec4 TerOvTri;
    vec4 TerOvOn;
    vec4 TerChart;
    vec4 TerChartSize;
};
uniform sampler2D Tex0;  uniform sampler2D Tex1;  uniform sampler2D Tex2;  uniform sampler2D Tex3;
uniform sampler2D Tex4;  uniform sampler2D Tex5;  uniform sampler2D Tex6;  uniform sampler2D Tex7;
uniform sampler2D Tex8;  uniform sampler2D Tex9;  uniform sampler2D Tex10; uniform sampler2D Tex11;
uniform sampler2D Tex12; uniform sampler2D Tex13; uniform sampler2D Tex14; uniform sampler2D Tex15;
";

    private const string TerrainGlslVertex = GlslCommonCb + TerrainGlslBlock + @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec2 aUv2;
layout(location = 4) in vec4 aCol;
layout(location = 5) in uint aPid;
out vec4 vCol;
out vec3 vNrm;
out vec3 vWp;
flat out uint vPid;

void main()
{
    vec4 wp = World * vec4(aPos, 1.0);
    GED_EMIT_CLIP(ViewProj * wp);
    vCol = aCol; vNrm = aNrm; vWp = wp.xyz; vPid = aPid;
}
";

    private const string TerrainGlslFragment = GlslCommonCb + TerrainGlslBlock + @"
in vec4 vCol;
in vec3 vNrm;
in vec3 vWp;
out vec4 fragColor;

vec2 TerMapUv(vec2 xz, vec2 origin, vec2 extent, vec2 size)
{
    vec2 h = 0.5 / max(size, vec2(1.0, 1.0));
    return clamp((xz - origin) / extent, h, vec2(1.0) - h);
}

vec4 TerTex(sampler2D t, vec3 wp, float s, float tri, vec3 tw)
{
    float inv = 1.0 / s;
    vec3 p = (wp - TerOrigin.xyz) * inv + fract(TerOrigin.xyz * inv);
    vec4 planar = texture(t, p.xz);
    vec4 sides = planar * tw.y + texture(t, vec2(p.z, -p.y)) * tw.x + texture(t, vec2(p.x, -p.y)) * tw.z;
    return mix(planar, sides, step(0.5, tri));
}

vec3 TerOverlay(vec3 albedo, vec4 s, float cov)
{
    return albedo * (1.0 - s.a * cov) + s.rgb * s.a * cov;
}

void main()
{
    int mode = int(Params.z);
    vec3 n = normalize(vNrm);
    vec3 tw = pow(abs(n), vec3(4.0));
    tw /= max(tw.x + tw.y + tw.z, 1e-4);
    vec2 muv = TerMapUv(vWp.xz, TerOrigin.xz, TerExtent.xy, TerExtent.zw);
    vec4 w0 = texture(Tex8, muv) * step(vec4(0.5, 1.5, 2.5, 3.5), TerOrigin.wwww);
    vec4 w1 = texture(Tex9, muv) * step(vec4(4.5, 5.5, 6.5, 7.5), TerOrigin.wwww);
    float sum = dot(w0, vec4(1.0)) + dot(w1, vec4(1.0));
    if (sum < 1e-4) { w0 = vec4(1.0, 0.0, 0.0, 0.0); w1 = vec4(0.0); sum = 1.0; }
    w0 /= sum; w1 /= sum;
    vec3 c = w0.x * TerTex(Tex0, vWp, TerUv0.x, TerTri0.x, tw).rgb
           + w0.y * TerTex(Tex1, vWp, TerUv0.y, TerTri0.y, tw).rgb
           + w0.z * TerTex(Tex2, vWp, TerUv0.z, TerTri0.z, tw).rgb
           + w0.w * TerTex(Tex3, vWp, TerUv0.w, TerTri0.w, tw).rgb
           + w1.x * TerTex(Tex4, vWp, TerUv1.x, TerTri1.x, tw).rgb
           + w1.y * TerTex(Tex5, vWp, TerUv1.y, TerTri1.y, tw).rgb
           + w1.z * TerTex(Tex6, vWp, TerUv1.z, TerTri1.z, tw).rgb
           + w1.w * TerTex(Tex7, vWp, TerUv1.w, TerTri1.w, tw).rgb;
    vec4 cov = texture(Tex15, muv) * TerOvOn;
    c = TerOverlay(c, TerTex(Tex11, vWp, TerOvUv.x, TerOvTri.x, tw), cov.x);
    c = TerOverlay(c, TerTex(Tex12, vWp, TerOvUv.y, TerOvTri.y, tw), cov.y);
    c = TerOverlay(c, TerTex(Tex13, vWp, TerOvUv.z, TerOvTri.z, tw), cov.z);
    c = TerOverlay(c, TerTex(Tex14, vWp, TerOvUv.w, TerOvTri.w, tw), cov.w);

    vec2 cuv = TerMapUv(vWp.xz, TerChart.xy, TerChartSize.xy * TerChart.z, TerChartSize.xy);
    vec3 lm = mix(vec3(0.5), texture(Tex10, cuv).rgb, step(0.5, TerChart.w) * (1.0 - step(0.5, TerChartSize.z)));
    vec3 outc;
    if (mode == 1)      outc = c * lm * Params.y;
    else if (mode == 2) outc = lm * Params.y;
    else if (mode == 3) outc = vCol.rgb;
    else if (mode == 4) outc = vec3(0.82, 0.82, 0.88);
    else                outc = c;
    outc *= Tint.rgb;
    outc = ApplyFog(outc, vWp);
    fragColor = vec4(outc, Tint.a * Params.x);
}
";

    private const string TerrainGlslPick = GlslCommonCb + @"
flat in uint vPid;
out uint fragId;
void main() { fragId = vPid; }
";

    // ---- V3M meshes ----
    private const string MeshGlslVertex = GlslCommon + @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNrm;
layout(location = 2) in vec2 aUv;
out vec2 vUv;
out vec3 vNrm;
out vec3 vWp;

void main()
{
    vec4 wp = World * vec4(aPos, 1.0);
    GED_EMIT_CLIP(ViewProj * wp);
    vUv = aUv;
    vNrm = mat3(World) * aNrm;
    vWp = wp.xyz;
}
";

    private const string MeshGlslFragment = GlslCommon + @"
in vec2 vUv;
in vec3 vNrm;
in vec3 vWp;
out vec4 fragColor;

void main()
{
    vec4 baseC = texture(Tex0, vUv);
    vec3 n = normalize(vNrm);
    vec3 toCam = normalize(CameraPos.xyz - vWp);
    float head = clamp(dot(n, toCam), 0.0, 1.0);
    float key = clamp(dot(n, normalize(vec3(0.35, 0.85, 0.4))), 0.0, 1.0);
    // HasLightmap doubles as the mesh fullbright flag (VFX fullbright / self-illuminated).
    float shade = (HasLightmap != 0u) ? 1.0 : min(0.35 + 0.5 * head + 0.2 * key, 1.1);
    vec3 outc = baseC.rgb * shade * Tint.rgb;
    outc = ApplyFog(outc, vWp);
    fragColor = vec4(outc, baseC.a * Tint.a * Params.x);
}
";

    private const string MeshGlslPick = GlslCommon + @"
out uint fragId;
void main() { fragId = PickId; }
";

    // ---- Billboards (point-object glyphs) ----
    private const string BillboardGlslVertex = GlslCommon + @"
layout(location = 0) in vec3 aCenter;
layout(location = 1) in vec2 aCorner;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec4 aCol;
layout(location = 4) in uint aPid;
out vec2 vUv;
out vec4 vCol;
flat out uint vPid;

void main()
{
    vec3 wp = aCenter + CameraRight.xyz * aCorner.x + CameraUp.xyz * aCorner.y;
    vec4 clip = ViewProj * vec4(wp, 1.0);
    // Object glyphs take their depth from a point pulled toward the camera by the half-height (see the HLSL).
    if ((aPid >> 28u) == 2u)
    {
        vec3 fwd = normalize(cross(CameraRight.xyz, CameraUp.xyz));
        vec4 pulled = ViewProj * vec4(wp - fwd * abs(aCorner.y), 1.0);
        float ndcz = pulled.w > 1e-4 ? clamp(pulled.z / pulled.w, 0.0, 1.0) : 0.0;
        clip.z = ndcz * clip.w;
    }
    GED_EMIT_CLIP(clip);
    vUv = aUv; vCol = aCol; vPid = aPid;
}
";

    private const string BillboardGlslFragment = GlslCommon + @"
in vec2 vUv;
in vec4 vCol;
out vec4 fragColor;

void main()
{
    vec4 c = texture(Tex0, vUv);
    if (c.a < 0.02) discard;
    fragColor = vec4(c.rgb * vCol.rgb, c.a * vCol.a);
}
";

    private const string BillboardGlslPick = GlslCommon + @"
in vec2 vUv;
flat in uint vPid;
out uint fragId;

void main()
{
    // Object glyphs are clickable over their whole square (see the HLSL).
    float a = texture(Tex0, vUv).a;
    if (a < 0.5 && (vPid >> 28u) != 2u) discard;
    fragId = vPid;
}
";

    // ---- Lines (grid / links / ranges / outlines / wireframe overlay) ----
    private const string LineGlslVertex = GlslCommon + @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec4 aCol;
out vec4 vCol;

void main()
{
    GED_EMIT_CLIP(ViewProj * vec4(aPos, 1.0));
    vCol = aCol;
}
";

    private const string LineGlslFragment = GlslCommon + @"
in vec4 vCol;
out vec4 fragColor;
void main() { fragColor = vCol; }
";

    public static RhiShaderSource World { get; } = new()
    {
        Hlsl = WorldHlsl,
        HasPick = true,
        GlslVertex = WorldGlslVertex,
        GlslFragment = WorldGlslFragment,
        GlslPickFragment = WorldGlslPick,
    };

    public static RhiShaderSource Terrain { get; } = new()
    {
        Hlsl = TerrainHlsl,
        HasPick = true,
        GlslVertex = TerrainGlslVertex,
        GlslFragment = TerrainGlslFragment,
        GlslPickFragment = TerrainGlslPick,
    };

    public static RhiShaderSource Mesh { get; } = new()
    {
        Hlsl = MeshHlsl,
        HasPick = true,
        GlslVertex = MeshGlslVertex,
        GlslFragment = MeshGlslFragment,
        GlslPickFragment = MeshGlslPick,
    };

    public static RhiShaderSource Billboard { get; } = new()
    {
        Hlsl = BillboardHlsl,
        HasPick = true,
        GlslVertex = BillboardGlslVertex,
        GlslFragment = BillboardGlslFragment,
        GlslPickFragment = BillboardGlslPick,
    };

    public static RhiShaderSource Line { get; } = new()
    {
        Hlsl = LineHlsl,
        HasPick = false,
        GlslVertex = LineGlslVertex,
        GlslFragment = LineGlslFragment,
    };
}
