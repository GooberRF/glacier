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
    private const string Common = @"
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
Texture2D    Tex0 : register(t0);
Texture2D    Tex1 : register(t1);
SamplerState Samp : register(s0);
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
    float shade = min(0.35 + 0.5 * head + 0.2 * key, 1.1);
    float3 outc = baseC.rgb * shade * Tint.rgb;
    outc = ApplyFog(outc, i.wp);
    return float4(outc, baseC.a * Tint.a * Params.x);
}

uint PSPick(VSOut i) : SV_Target { return PickId; }
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
    float a = Tex0.Sample(Samp, i.uv).a;
    if (a < 0.5) discard;
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
    private const string GlslCommon = @"#version 330 core
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
uniform sampler2D Tex0;
uniform sampler2D Tex1;

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
    float shade = min(0.35 + 0.5 * head + 0.2 * key, 1.1);
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
    GED_EMIT_CLIP(ViewProj * vec4(wp, 1.0));
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
    float a = texture(Tex0, vUv).a;
    if (a < 0.5) discard;
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
