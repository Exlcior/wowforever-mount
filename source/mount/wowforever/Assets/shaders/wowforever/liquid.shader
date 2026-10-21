// Forever liquids. Colours, textures and kind come from LiquidType.db2 of the pinned build;
// the motion, depth absorption and shoreline foam are adapted to s&box rendering.
HEADER
{
    Description = "Forever water (depth colours, ripples, shore foam) and molten liquids (lava, fel, tar)";
}
FEATURES
{
    #include "common/features.hlsl"
    Feature( F_MOLTEN, 0..1, "Liquid" );
}
MODES
{
    Forward();
}
COMMON
{
    #define BLEND_MODE_ALREADY_SET 1
    #include "common/shared.hlsl"
}
struct VertexInput
{
    #include "common/vertexinput.hlsl"
};
struct PixelInput
{
    #include "common/pixelinput.hlsl"
};
VS
{
    #include "common/vertex.hlsl"
    PixelInput MainVs( VertexInput i )
    {
        return FinalizeVertex( ProcessVertex( i ) );
    }
}
PS
{
    StaticCombo( S_MOLTEN, F_MOLTEN, Sys(ALL) );
    #if !S_MOLTEN
        // Water renders after opaque geometry, outside the depth chain, and reads what lies beneath.
        #define S_TRANSLUCENT 1
        #define DEPTH_STATE_ALREADY_SET 1
    #endif
    #include "common/pixel.hlsl"
    #include "common/classes/Depth.hlsl"
    RenderState( CullMode, NONE );
    #if !S_MOLTEN
        RenderState( DepthWriteEnable, false );
        BoolAttribute( bWantsFBCopyTexture, true );
        Texture2D g_tFrameBufferCopyTexture < Attribute("FrameBufferCopyTexture"); SrgbRead(false); >;
    #endif

    SamplerState LiquidSampler < Filter(ANISOTROPIC); MaxAniso(8); AddressU(WRAP); AddressV(WRAP); >;
    // Water inputs.
    CreateInputTexture2D( TextureFoam, Srgb, 8, "", "_foam", "Water", Default3(1,1,1) );
    CreateInputTexture2D( TextureRim, Srgb, 8, "", "_rim", "Water", Default3(1,1,1) );
    CreateInputTexture2D( TextureBump, Linear, 8, "NormalizeNormals", "_normal", "Water", Default3(0.5,0.5,1) );
    Texture2D g_tFoam < Channel(RGBA, Box(TextureFoam), Srgb); OutputFormat(BC7); SrgbRead(true); >;
    Texture2D g_tRim < Channel(RGBA, Box(TextureRim), Srgb); OutputFormat(BC7); SrgbRead(true); >;
    Texture2D g_tBump < Channel(RGB, Box(TextureBump), Linear); OutputFormat(BC7); SrgbRead(false); >;
    float4 g_vShallowColor < Default4(0.55,0.74,0.70,1); UiType(Color); UiGroup("Water"); >;
    float4 g_vMidColor < Default4(0.16,0.46,0.54,1); UiType(Color); UiGroup("Water"); >;
    float4 g_vDeepColor < Default4(0.04,0.15,0.22,1); UiType(Color); UiGroup("Water"); >;
    float g_flWaveScale < Default(9.0); Range(1,40); UiGroup("Water"); >;
    float g_flWaveStrength < Default(0.30); Range(0,1); UiGroup("Water"); >;
    float g_flClarity < Default(0.35); Range(0.02,4); UiGroup("Water"); >;
    float g_flFoamStrength < Default(0.55); Range(0,1); UiGroup("Water"); >;
    float g_flRoughness < Default(0.08); Range(0.02,0.5); UiGroup("Water"); >;
    // Molten inputs.
    CreateInputTexture2D( TextureColor, Srgb, 8, "", "_color", "Molten", Default3(1,0.4,0.05) );
    CreateInputTexture2D( TextureEmissive, Srgb, 8, "", "_emissive", "Molten", Default3(1,0.4,0.05) );
    Texture2D g_tColor < Channel(RGB, Box(TextureColor), Srgb); OutputFormat(BC7); SrgbRead(true); >;
    Texture2D g_tEmissive < Channel(RGB, Box(TextureEmissive), Srgb); OutputFormat(BC7); SrgbRead(true); >;
    float4 g_vGlowColor < Default4(1,0.4,0,1); UiType(Color); UiGroup("Molten"); >;
    float g_flGlow < Default(2.2); Range(0,8); UiGroup("Molten"); >;
    float g_flFlowScale < Default(14.0); Range(1,60); UiGroup("Molten"); >;

    float4 MainPs( PixelInput i ) : SV_Target
    {
        Material m = Material::Init( i );
        float3 world = m.WorldPosition;
        // Source units (yards): one projection for every tile and building keeps seams invisible.
        float2 p = world.xy / 36.0;
        float t = g_flTime;
    #if S_MOLTEN
        float2 uvA = p / g_flFlowScale + float2( t * 0.010, t * 0.004 );
        float2 uvB = float2( p.y, -p.x ) / ( g_flFlowScale * 1.7 ) + float2( -t * 0.006, t * 0.008 );
        float3 color = lerp( g_tColor.Sample( LiquidSampler, uvA ).rgb, g_tColor.Sample( LiquidSampler, uvB ).rgb, 0.45 );
        float3 glow = g_tEmissive.Sample( LiquidSampler, uvA * 1.3 + float2( 0, t * 0.012 ) ).rgb;
        float pulse = 0.85 + 0.15 * sin( t * 1.3 + dot( p, float2( 0.21, 0.17 ) ) );
        m.Albedo = color * 0.35;
        m.Normal = float3( 0, 0, 1 );
        m.Roughness = 0.55;
        m.Metalness = 0;
        m.AmbientOcclusion = 1;
        m.Opacity = 1;
        m.Emission = ( color * 0.6 + glow * g_vGlowColor.rgb ) * g_flGlow * pulse;
        float4 lit = ShadingModelStandard::Shade( i, m );
        lit.rgb = Fog::Apply( world, i.vPositionSs.xy, lit.rgb );
        return lit;
    #else
        float2 uvA = p / g_flWaveScale + float2( t * 0.020, t * 0.011 );
        float2 uvB = float2( p.x * 0.8 - p.y * 0.6, p.x * 0.6 + p.y * 0.8 ) / ( g_flWaveScale * 0.61 ) + float2( -t * 0.014, t * 0.019 );
        float3 a = DecodeNormal( g_tBump.Sample( LiquidSampler, uvA ).xyz );
        float3 b = DecodeNormal( g_tBump.Sample( LiquidSampler, uvB ).xyz );
        float2 slope = a.xy + float2( b.x * 0.8 + b.y * 0.6, -b.x * 0.6 + b.y * 0.8 ) * 0.6;
        float distanceYards = length( world - g_vCameraPositionWs ) / 36.0;
        float detailFade = lerp( 1.0, 0.25, smoothstep( 120.0, 1200.0, distanceYards ) );
        float3 normal = normalize( float3( slope * detailFade * g_flWaveStrength, 1 ) );
        float3 toCamera = CalculatePositionToCameraDirWs( world );
        bool underwater = g_vCameraPositionWs.z < world.z;
        float3 surfaceNormal = underwater ? -normal : normal;
        float facing = saturate( dot( surfaceNormal, toCamera ) );
        float fresnel = 0.02 + 0.98 * pow( 1.0 - facing, 5 );

        // Depth below the surface, in yards, picks the original shallow, mid and deep colours.
        float3 bed = Depth::GetWorldPosition( i.vPositionSs.xy );
        float depth = clamp( ( world.z - bed.z ) / 36.0, 0.0, 80.0 );
        float2 offset = normal.xy * 10.0 * smoothstep( 0.0, 1.0, depth );
        float2 screen = clamp( i.vPositionSs.xy + offset, 1.0, g_vViewportSize.xy - 2.0 );
        if ( Depth::GetWorldPosition( screen ).z > world.z - 0.5 ) screen = i.vPositionSs.xy;
        float2 frameUv = screen * g_vInvViewportSize.xy * g_vFrameBufferCopyInvSizeAndUvScale.zw;
        float3 behind = g_tFrameBufferCopyTexture.SampleLevel( g_sTrilinearClamp, frameUv, 0 ).rgb;
        float3 tint = lerp( g_vShallowColor.rgb, g_vMidColor.rgb, smoothstep( 0.0, 2.5, depth ) );
        tint = lerp( tint, g_vDeepColor.rgb, smoothstep( 2.5, 14.0, depth ) );
        float cover = 1.0 - exp( -depth * g_flClarity / max( facing, 0.25 ) );
        float3 waterColor = lerp( behind * lerp( float3( 1, 1, 1 ), g_vShallowColor.rgb, 0.35 ), tint, cover );

        // Shoreline foam from the original rim and foam masks, fading out in open water.
        float4 rim = g_tRim.Sample( LiquidSampler, p * 0.22 + float2( t * 0.012, -t * 0.008 ) );
        float4 foamTex = g_tFoam.Sample( LiquidSampler, p * 0.15 + float2( -t * 0.009, t * 0.006 ) );
        float shore = ( 1.0 - smoothstep( 0.05, 0.9, depth ) ) * smoothstep( 0.0, 0.04, depth );
        float foam = saturate( shore * max( rim.a, foamTex.a * 0.7 ) * g_flFoamStrength * 1.6 );
        if ( underwater ) foam = 0;

        m.Albedo = foamTex.rgb * foam;
        m.Normal = surfaceNormal;
        float variance = length( ddx( normal ) ) + length( ddy( normal ) );
        m.Roughness = lerp( sqrt( saturate( g_flRoughness * g_flRoughness + variance * 0.8 ) ), 0.7, foam );
        m.Metalness = 0;
        m.AmbientOcclusion = 1;
        m.Opacity = 1;
        m.Emission = waterColor * ( 1.0 - fresnel ) * ( 1.0 - foam );
        float4 shaded = ShadingModelStandard::Shade( i, m );
        // Same distance haze as the terrain, so far water meets the horizon without a hard edge.
        return float4( Fog::Apply( world, i.vPositionSs.xy, shaded.rgb ), 1 );
    #endif
    }
}
