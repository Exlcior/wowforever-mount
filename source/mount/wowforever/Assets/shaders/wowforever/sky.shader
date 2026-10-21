// Forever sky dome: the client's vertical colour bands (top, middle, band 1, band 2, smog, fog)
// and sun, fed every frame by AzerothAmbience from the zone lights. The fog band equals the
// distance haze colour, so terrain and sky meet without a seam.
HEADER
{
	Description = "Forever sky bands";
	Version = 1;
}

MODES
{
	Forward();
}

FEATURES
{
	#include "vr_common_features.fxc"
}

COMMON
{
	#include "system.fxc"
	#include "vr_common.fxc"
}

struct VS_INPUT
{
	float4 vPositionOs : POSITION < Semantic( PosXyz ); >;
};

struct PS_INPUT
{
	float3 vPositionWs : TEXCOORD1;
	#if ( PROGRAM == VFX_PROGRAM_VS )
		float4 vPositionPs : SV_Position;
	#endif
	#if ( PROGRAM == VFX_PROGRAM_PS )
		float4 vPositionSs : SV_Position;
	#endif
};

VS
{
	#define IS_SPRITECARD 1
	#include "system.fxc"

	PS_INPUT MainVs( const VS_INPUT i )
	{
		PS_INPUT o;
		float flSkyboxScale = g_flNearPlane + g_flFarPlane;
		float3 vPositionWs = g_vCameraPositionWs.xyz + i.vPositionOs.xyz * flSkyboxScale;
		o.vPositionPs = Position3WsToPs( vPositionWs );
		o.vPositionWs = vPositionWs;
		return o;
	}
}

PS
{
	#include "vr_lighting.fxc"

	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );
	RenderState( DepthEnable, true );
	RenderState( DepthFunc, GREATER_EQUAL );
	BoolAttribute( sky, true );

	// Linear colours, set by AzerothAmbience.
	float3 g_vSkyTop < Default3( 0.00, 0.012, 0.067 ); UiType( Color ); UiGroup( "Sky" ); >;
	float3 g_vSkyMiddle < Default3( 0.042, 0.36, 0.62 ); UiType( Color ); UiGroup( "Sky" ); >;
	float3 g_vSkyBand1 < Default3( 0.32, 0.72, 0.91 ); UiType( Color ); UiGroup( "Sky" ); >;
	float3 g_vSkyBand2 < Default3( 0.43, 0.70, 0.74 ); UiType( Color ); UiGroup( "Sky" ); >;
	float3 g_vSkySmog < Default3( 0.46, 0.46, 0.46 ); UiType( Color ); UiGroup( "Sky" ); >;
	float3 g_vSkyFog < Default3( 0.074, 0.19, 0.27 ); UiType( Color ); UiGroup( "Sky" ); >;
	float3 g_vSunColor < Default3( 1.0, 0.93, 0.73 ); UiType( Color ); UiGroup( "Sky" ); >;
	float g_flSkyBrightness < Default( 1.0 ); Range( 0, 8 ); UiGroup( "Sky" ); >;

	struct PS_OUTPUT
	{
		float4 vColor0 : SV_Target0;
	};

	PS_OUTPUT MainPs( PS_INPUT i )
	{
		PS_OUTPUT o;
		float3 vRay = normalize( i.vPositionWs - g_vCameraPositionWs );
		// Elevation (sine) at which each band of the client dome sits; below the horizon stays fog.
		float e = vRay.z;
		float3 color = g_vSkyFog;
		color = lerp( color, g_vSkySmog, smoothstep( 0.03, 0.08, e ) );
		color = lerp( color, g_vSkyBand2, smoothstep( 0.08, 0.16, e ) );
		color = lerp( color, g_vSkyBand1, smoothstep( 0.16, 0.30, e ) );
		color = lerp( color, g_vSkyMiddle, smoothstep( 0.30, 0.60, e ) );
		color = lerp( color, g_vSkyTop, smoothstep( 0.60, 1.00, e ) );

		float3 sunDirectionWs = -g_DirectionalLightDirection.xyz;
		float facing = saturate( dot( vRay, normalize( sunDirectionWs ) ) );
		float disk = smoothstep( 0.9993, 0.9997, facing );
		float halo = pow( facing, 64.0 ) * 0.35 + pow( facing, 8.0 ) * 0.08;
		color += g_vSunColor * ( disk * 6.0 + halo ) * saturate( e * 20.0 + 0.5 );

		o.vColor0 = float4( color * g_flSkyBrightness, 1.0 );
		return o;
	}
}
