HEADER
{
    Description = "Forever props with scrolling textures or unlit surfaces (waterfalls, lava, glows, flames)";
}
FEATURES
{
    #include "common/features.hlsl"
    Feature( F_WOW_BLEND, 0..4(0="Opaque",1="Alpha key",2="Alpha",3="Add",4="Alpha add"), "Rendering" );
    Feature( F_UNLIT, 0..1, "Rendering" );
    Feature( F_BACKFACES, 0..1, "Rendering" );
}
MODES
{
    Forward();
}
COMMON
{
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
    // The client's texture transform, as a constant speed in UV per second.
    float2 g_vUvScroll < Default2( 0, 0 ); UiGroup( "Animation" ); >;
    PixelInput MainVs( VertexInput i )
    {
        PixelInput o = ProcessVertex( i );
        o.vTextureCoords.xy += g_vUvScroll * g_flTime;
        return FinalizeVertex( o );
    }
}
PS
{
    StaticCombo( S_WOW_BLEND, F_WOW_BLEND, Sys( ALL ) );
    StaticCombo( S_UNLIT, F_UNLIT, Sys( ALL ) );
    StaticCombo( S_BACKFACES, F_BACKFACES, Sys( ALL ) );
    #include "common/pixel.hlsl"
    CreateInputTexture2D( TextureColor, Srgb, 8, "", "_color", "Material,10/10", Default4( 1, 1, 1, 1 ) );
    Texture2D g_tColor < Channel( RGBA, Box( TextureColor ), Srgb ); OutputFormat( BC7 ); SrgbRead( true ); >;
    SamplerState TextureSampler < Filter( ANISOTROPIC ); MaxAniso( 8 ); AddressU( WRAP ); AddressV( WRAP ); >;

    #if S_BACKFACES
        RenderState( CullMode, NONE );
    #endif
    #if S_WOW_BLEND >= 2
        RenderState( BlendEnable, true );
        RenderState( DepthWriteEnable, false );
        RenderState( BlendOp, ADD );
        #if S_WOW_BLEND == 2
            RenderState( SrcBlend, SRC_ALPHA );
            RenderState( DstBlend, INV_SRC_ALPHA );
        #elif S_WOW_BLEND == 3
            RenderState( SrcBlend, ONE );
            RenderState( DstBlend, ONE );
        #else
            RenderState( SrcBlend, SRC_ALPHA );
            RenderState( DstBlend, ONE );
        #endif
    #endif

    float4 MainPs( PixelInput i ) : SV_Target0
    {
        float4 color = g_tColor.Sample( TextureSampler, i.vTextureCoords.xy );
    #if S_WOW_BLEND == 1
        clip( color.a - 0.5 );
    #endif
    #if S_WOW_BLEND == 0 || S_WOW_BLEND == 1
        color.a = 1;
    #endif
    #if S_UNLIT
        #if S_WOW_BLEND <= 2
            color.rgb = Fog::Apply( Material::Init( i ).WorldPosition, i.vPositionSs.xy, color.rgb );
        #endif
        return color;
    #else
        Material m = Material::Init( i );
        m.Albedo = color.rgb;
        m.Opacity = color.a;
        m.Roughness = 1;
        m.Metalness = 0;
        m.AmbientOcclusion = 1;
        return ShadingModelStandard::Shade( i, m );
    #endif
    }
}
