HEADER
{
    Description = "Forever original diffuse/MCAL terrain layers";
}
FEATURES
{
    #include "common/features.hlsl"
    Feature(F_HEIGHT_BLEND,0..1,"Terrain");
    Feature(F_LAYER_COUNT,0..8(0="Default",1="One",2="Two",3="Three",4="Four",5="Five",6="Six",7="Seven",8="Eight"),"Terrain");
}
MODES
{
    Forward();
    Depth(S_MODE_DEPTH);
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
    PixelInput MainVs(VertexInput i) { return FinalizeVertex(ProcessVertex(i)); }
}
PS
{
    #include "common/pixel.hlsl"
    StaticCombo(S_HEIGHT_BLEND,F_HEIGHT_BLEND,Sys(ALL));
    StaticCombo(S_LAYER_COUNT_RAW,F_LAYER_COUNT,Sys(ALL));
    #if S_LAYER_COUNT_RAW == 0
        #define S_LAYER_COUNT 1
    #else
        #define S_LAYER_COUNT S_LAYER_COUNT_RAW
    #endif
    SamplerState DiffuseSampler < Filter(ANISOTROPIC); MaxAniso(16); AddressU(WRAP); AddressV(WRAP); >;
    SamplerState BlendSampler < Filter(BILINEAR); AddressU(CLAMP); AddressV(CLAMP); >;
    float4 g_vLayerScale0 < Default4(1,1,1,1); >;
    float4 g_vLayerScale1 < Default4(1,1,1,1); >;
    float4 g_vHeightScale0 < Default4(1,1,1,1); >;
    float4 g_vHeightScale1 < Default4(1,1,1,1); >;
    float4 g_vHeightOffset0 < Default4(0,0,0,0); >;
    float4 g_vHeightOffset1 < Default4(0,0,0,0); >;

    #if S_LAYER_COUNT > 0
        CreateInputTexture2D(TextureColor,Srgb,8,"","_color","Terrain,10/10",Default3(1,1,1));
        Texture2D g_tLayer0 < Channel(RGB,Box(TextureColor),Srgb); OutputFormat(BC7); SrgbRead(true); >;
        #if S_HEIGHT_BLEND
            CreateInputTexture2D(TextureHeight0,Linear,8,"","_height","Terrain,20/10",Default(1));
            Texture2D g_tHeight0 < Channel(R,Box(TextureHeight0),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
        #endif
    #endif
    #if S_LAYER_COUNT > 1
        CreateInputTexture2D(TextureLayer1,Srgb,8,"","_color","Terrain,10/11",Default3(1,1,1));
        Texture2D g_tLayer1 < Channel(RGB,Box(TextureLayer1),Srgb); OutputFormat(BC7); SrgbRead(true); >;
        #if S_HEIGHT_BLEND
            CreateInputTexture2D(TextureHeight1,Linear,8,"","_height","Terrain,20/11",Default(1));
            Texture2D g_tHeight1 < Channel(R,Box(TextureHeight1),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
        #endif
    #endif
    #if S_LAYER_COUNT > 2
        CreateInputTexture2D(TextureLayer2,Srgb,8,"","_color","Terrain,10/12",Default3(1,1,1));
        Texture2D g_tLayer2 < Channel(RGB,Box(TextureLayer2),Srgb); OutputFormat(BC7); SrgbRead(true); >;
        #if S_HEIGHT_BLEND
            CreateInputTexture2D(TextureHeight2,Linear,8,"","_height","Terrain,20/12",Default(1));
            Texture2D g_tHeight2 < Channel(R,Box(TextureHeight2),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
        #endif
    #endif
    #if S_LAYER_COUNT > 3
        CreateInputTexture2D(TextureLayer3,Srgb,8,"","_color","Terrain,10/13",Default3(1,1,1));
        Texture2D g_tLayer3 < Channel(RGB,Box(TextureLayer3),Srgb); OutputFormat(BC7); SrgbRead(true); >;
        #if S_HEIGHT_BLEND
            CreateInputTexture2D(TextureHeight3,Linear,8,"","_height","Terrain,20/13",Default(1));
            Texture2D g_tHeight3 < Channel(R,Box(TextureHeight3),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
        #endif
    #endif
    #if S_LAYER_COUNT > 4
        CreateInputTexture2D(TextureLayer4,Srgb,8,"","_color","Terrain,10/14",Default3(1,1,1));
        Texture2D g_tLayer4 < Channel(RGB,Box(TextureLayer4),Srgb); OutputFormat(BC7); SrgbRead(true); >;
        #if S_HEIGHT_BLEND
            CreateInputTexture2D(TextureHeight4,Linear,8,"","_height","Terrain,20/14",Default(1));
            Texture2D g_tHeight4 < Channel(R,Box(TextureHeight4),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
        #endif
    #endif
    #if S_LAYER_COUNT > 5
        CreateInputTexture2D(TextureLayer5,Srgb,8,"","_color","Terrain,10/15",Default3(1,1,1));
        Texture2D g_tLayer5 < Channel(RGB,Box(TextureLayer5),Srgb); OutputFormat(BC7); SrgbRead(true); >;
        #if S_HEIGHT_BLEND
            CreateInputTexture2D(TextureHeight5,Linear,8,"","_height","Terrain,20/15",Default(1));
            Texture2D g_tHeight5 < Channel(R,Box(TextureHeight5),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
        #endif
    #endif
    #if S_LAYER_COUNT > 6
        CreateInputTexture2D(TextureLayer6,Srgb,8,"","_color","Terrain,10/16",Default3(1,1,1));
        Texture2D g_tLayer6 < Channel(RGB,Box(TextureLayer6),Srgb); OutputFormat(BC7); SrgbRead(true); >;
        #if S_HEIGHT_BLEND
            CreateInputTexture2D(TextureHeight6,Linear,8,"","_height","Terrain,20/16",Default(1));
            Texture2D g_tHeight6 < Channel(R,Box(TextureHeight6),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
        #endif
    #endif
    #if S_LAYER_COUNT > 7
        CreateInputTexture2D(TextureLayer7,Srgb,8,"","_color","Terrain,10/17",Default3(1,1,1));
        Texture2D g_tLayer7 < Channel(RGB,Box(TextureLayer7),Srgb); OutputFormat(BC7); SrgbRead(true); >;
        #if S_HEIGHT_BLEND
            CreateInputTexture2D(TextureHeight7,Linear,8,"","_height","Terrain,20/17",Default(1));
            Texture2D g_tHeight7 < Channel(R,Box(TextureHeight7),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
        #endif
    #endif
    #if S_LAYER_COUNT > 1
        CreateInputTexture2D(TextureBlend,Linear,8,"","_blend","Terrain,30/10",Default3(0,0,0));
        Texture2D g_tBlend0 < Channel(RGB,Box(TextureBlend),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
    #endif
    #if S_LAYER_COUNT > 4
        CreateInputTexture2D(TextureBlend1,Linear,8,"","_blend","Terrain,30/11",Default3(0,0,0));
        Texture2D g_tBlend1 < Channel(RGB,Box(TextureBlend1),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
    #endif
    #if S_LAYER_COUNT > 7
        CreateInputTexture2D(TextureBlend2,Linear,8,"","_blend","Terrain,30/12",Default3(0,0,0));
        Texture2D g_tBlend2 < Channel(RGB,Box(TextureBlend2),Linear); OutputFormat(RGBA8888); SrgbRead(false); >;
    #endif
    float4 MainPs(PixelInput i) : SV_Target0
    {
        float2 uv=saturate(i.vTextureCoords.xy);
        float2 chunk=min(floor(uv*16.0),15.0);
        // MCAL's border samples belong to this chunk, never to the adjacent material set.
        float2 blendUv=(chunk*64.0+saturate(uv*16.0-chunk)*63.0+0.5)/1024.0;
        float w[S_LAYER_COUNT];
        w[0]=1.0;
        #if S_LAYER_COUNT > 1
            float3 alpha0=g_tBlend0.SampleLevel(BlendSampler,blendUv,0).rgb;
        #endif
        #if S_LAYER_COUNT > 4
            float3 alpha1=g_tBlend1.SampleLevel(BlendSampler,blendUv,0).rgb;
        #endif
        #if S_LAYER_COUNT > 7
            float3 alpha2=g_tBlend2.SampleLevel(BlendSampler,blendUv,0).rgb;
        #endif
        #if S_LAYER_COUNT > 1
            w[1]=alpha0.x; w[0]-=w[1];
        #endif
        #if S_LAYER_COUNT > 2
            w[2]=alpha0.y; w[0]-=w[2];
        #endif
        #if S_LAYER_COUNT > 3
            w[3]=alpha0.z; w[0]-=w[3];
        #endif
        #if S_LAYER_COUNT > 4
            w[4]=alpha1.x; w[0]-=w[4];
        #endif
        #if S_LAYER_COUNT > 5
            w[5]=alpha1.y; w[0]-=w[5];
        #endif
        #if S_LAYER_COUNT > 6
            w[6]=alpha1.z; w[0]-=w[6];
        #endif
        #if S_LAYER_COUNT > 7
            w[7]=alpha2.x; w[0]-=w[7];
        #endif
        w[0]=saturate(w[0]);
        #if S_LAYER_COUNT > 0
            float2 uv0=uv*(128.0/g_vLayerScale0.x);
            #if S_HEIGHT_BLEND
                w[0]*=g_tHeight0.Sample(DiffuseSampler,uv0).r*g_vHeightScale0.x+g_vHeightOffset0.x;
            #endif
        #endif
        #if S_LAYER_COUNT > 1
            float2 uv1=uv*(128.0/g_vLayerScale0.y);
            #if S_HEIGHT_BLEND
                w[1]*=g_tHeight1.Sample(DiffuseSampler,uv1).r*g_vHeightScale0.y+g_vHeightOffset0.y;
            #endif
        #endif
        #if S_LAYER_COUNT > 2
            float2 uv2=uv*(128.0/g_vLayerScale0.z);
            #if S_HEIGHT_BLEND
                w[2]*=g_tHeight2.Sample(DiffuseSampler,uv2).r*g_vHeightScale0.z+g_vHeightOffset0.z;
            #endif
        #endif
        #if S_LAYER_COUNT > 3
            float2 uv3=uv*(128.0/g_vLayerScale0.w);
            #if S_HEIGHT_BLEND
                w[3]*=g_tHeight3.Sample(DiffuseSampler,uv3).r*g_vHeightScale0.w+g_vHeightOffset0.w;
            #endif
        #endif
        #if S_LAYER_COUNT > 4
            float2 uv4=uv*(128.0/g_vLayerScale1.x);
            #if S_HEIGHT_BLEND
                w[4]*=g_tHeight4.Sample(DiffuseSampler,uv4).r*g_vHeightScale1.x+g_vHeightOffset1.x;
            #endif
        #endif
        #if S_LAYER_COUNT > 5
            float2 uv5=uv*(128.0/g_vLayerScale1.y);
            #if S_HEIGHT_BLEND
                w[5]*=g_tHeight5.Sample(DiffuseSampler,uv5).r*g_vHeightScale1.y+g_vHeightOffset1.y;
            #endif
        #endif
        #if S_LAYER_COUNT > 6
            float2 uv6=uv*(128.0/g_vLayerScale1.z);
            #if S_HEIGHT_BLEND
                w[6]*=g_tHeight6.Sample(DiffuseSampler,uv6).r*g_vHeightScale1.z+g_vHeightOffset1.z;
            #endif
        #endif
        #if S_LAYER_COUNT > 7
            float2 uv7=uv*(128.0/g_vLayerScale1.w);
            #if S_HEIGHT_BLEND
                w[7]*=g_tHeight7.Sample(DiffuseSampler,uv7).r*g_vHeightScale1.w+g_vHeightOffset1.w;
            #endif
        #endif
        #if S_HEIGHT_BLEND
            float maximum=0;
            [unroll] for(int j=0;j<S_LAYER_COUNT;j++) maximum=max(maximum,w[j]);
            [unroll] for(int j=0;j<S_LAYER_COUNT;j++) w[j]*=1-saturate(maximum-w[j]);
        #endif
        float total=0;
        [unroll] for(int j=0;j<S_LAYER_COUNT;j++) total+=w[j];
        total=max(total,0.000001);
        Material m=Material::Init(i); m.Albedo=0;

        #if S_LAYER_COUNT > 0
            m.Albedo+=g_tLayer0.Sample(DiffuseSampler,uv0).rgb*(w[0]/total);
        #endif
        #if S_LAYER_COUNT > 1
            m.Albedo+=g_tLayer1.Sample(DiffuseSampler,uv1).rgb*(w[1]/total);
        #endif
        #if S_LAYER_COUNT > 2
            m.Albedo+=g_tLayer2.Sample(DiffuseSampler,uv2).rgb*(w[2]/total);
        #endif
        #if S_LAYER_COUNT > 3
            m.Albedo+=g_tLayer3.Sample(DiffuseSampler,uv3).rgb*(w[3]/total);
        #endif
        #if S_LAYER_COUNT > 4
            m.Albedo+=g_tLayer4.Sample(DiffuseSampler,uv4).rgb*(w[4]/total);
        #endif
        #if S_LAYER_COUNT > 5
            m.Albedo+=g_tLayer5.Sample(DiffuseSampler,uv5).rgb*(w[5]/total);
        #endif
        #if S_LAYER_COUNT > 6
            m.Albedo+=g_tLayer6.Sample(DiffuseSampler,uv6).rgb*(w[6]/total);
        #endif
        #if S_LAYER_COUNT > 7
            m.Albedo+=g_tLayer7.Sample(DiffuseSampler,uv7).rgb*(w[7]/total);
        #endif
        return ShadingModelStandard::Shade(i,m);
    }
}
