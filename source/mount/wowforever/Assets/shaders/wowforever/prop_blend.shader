HEADER
{
    Description = "Forever multiply, multiply 2x and premultiplied props";
}
FEATURES
{
    #include "common/features.hlsl"
    Feature( F_TRANSLUCENT, 0..1, "Rendering" );
    Feature( F_WOW_BLEND, 0..7(0="Default",1="Unused",2="Alpha",3="Add",4="Alpha Add",5="Multiply",6="Multiply 2x",7="Premultiplied"), "Rendering" );
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
    PixelInput MainVs( VertexInput i ) { return FinalizeVertex( ProcessVertex(i) ); }
}
PS
{
    StaticCombo(S_TRANSLUCENT,F_TRANSLUCENT,Sys(ALL));
    StaticCombo(S_WOW_BLEND,F_WOW_BLEND,Sys(ALL));
    #include "common/pixel.hlsl"
    CreateInputTexture2D(TextureColor,Srgb,8,"","_color","Material,10/10",Default3(1,1,1));
    CreateInputTexture2D(TextureTranslucency,Linear,8,"","_trans","Material,10/20",Default(1));
    Texture2D g_tColor < Channel(RGB,Box(TextureColor),Srgb); Channel(A,Box(TextureTranslucency),Linear); OutputFormat(BC7); SrgbRead(true); >;
    SamplerState TextureSampler < Filter(ANISOTROPIC); MaxAniso(8); AddressU(WRAP); AddressV(WRAP); >;
    RenderState(BlendEnable,true);
    RenderState(DepthWriteEnable,false);
    RenderState(BlendOp,ADD);
    #if S_WOW_BLEND == 5
        RenderState(SrcBlend,DEST_COLOR);
        RenderState(DstBlend,ZERO);
    #elif S_WOW_BLEND == 6
        RenderState(SrcBlend,DEST_COLOR);
        RenderState(DstBlend,SRC_COLOR);
    #else
        RenderState(SrcBlend,ONE);
        RenderState(DstBlend,INV_SRC_ALPHA);
    #endif
    float4 MainPs(PixelInput i) : SV_Target0 { return g_tColor.Sample(TextureSampler,i.vTextureCoords.xy); }
}
