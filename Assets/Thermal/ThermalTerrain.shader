// The thermal shader for terrain (2026-11-02): Thermal/Base with a vertex stage of its own, for a terrain that raises
// its meshes by a height map. All the physics is ThermalRadianceAtCamera's (ThermalSurfaceRadiance.hlsl), the same
// function Thermal/Base calls, so the thermal camera sees the terrain exactly as it would see the same surface drawn
// with Thermal/Base. The two places marked TERRAIN are the terrain's own: the height and the normal.
//
// Draw the terrain from ThermalTerrainFeature's pass (subscribe to ThermalTerrainFeature.Draw and record the draws in
// the buffer it hands over): drawn as a scene renderer, the thermal renderer's RenderObjects pass would replace this
// shader with Thermal/Base. Everything else is Thermal/Base's: the same properties - the property map (R absorptivity,
// G emissivity, linear, as the trim sheet paints them) and the surface group's numbers, as ThermalMaterial writes them
// into a property block - and the sun's visibility from URP's shadow map. Thermal → Terrain Check compares a flat quad
// drawn with each.
Shader "Thermal/Terrain"
{
    Properties
    {
        _Emissivity ("Emissivity", Range(0,1)) = 0.95
        _SolarAbsorptivity ("SolarAbsorptivity", Range(0,1)) = 0.9
        [NoScaleOffset] _ThermalPropertyMap ("Property Map (R absorptivity, G emissivity, linear)", 2D) = "white" {}
        _ThermalPropertyMapOn ("Use Property Map", Float) = 0
        _ThermalMapST ("Map Tiling (xy) and Offset (zw)", Vector) = (1, 1, 0, 0)
        _ThermalWindA ("Wind a (from the surface group)", Float) = 2.38
        _ThermalWindB ("Wind b (from the surface group)", Float) = 0.89
        _ThermalBackside ("Back side (thin objects)", Float) = 0
        _ThermalHistoryRow ("Stored heat row (from the surface group)", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ThermalTerrain"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_instancing

            #include "ThermalSurfaceRadiance.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
            };

            float _Emissivity;
            float _SolarAbsorptivity;
            TEXTURE2D(_ThermalPropertyMap);
            SAMPLER(sampler_ThermalPropertyMap);
            float _ThermalPropertyMapOn;
            float4 _ThermalMapST;
            float _ThermalWindA;
            float _ThermalWindB;
            float _ThermalBackside;
            float _ThermalHistoryRow;

            // TERRAIN: how far the surface rises above the mesh at uv, in metres along the object's y: the height map,
            // read with SAMPLE_TEXTURE2D_LOD (no derivatives in a vertex stage). Here 0, a flat mesh.
            float TerrainHeight(float2 uv)
            {
                return 0;
            }

            // TERRAIN: the surface's normal at uv, unit, world space. Here straight up. From the height map, per pixel:
            // central differences one texel either side, over the metres between them. Not from ddx/ddy, which come in
            // 2x2 blocks and change as the camera moves, which shows as flicker.
            float3 TerrainNormal(float2 uv)
            {
                return float3(0, 1, 0);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                float3 positionOS = IN.positionOS.xyz + float3(0, TerrainHeight(IN.uv), 0);
                OUT.positionCS = TransformObjectToHClip(positionOS);
                OUT.positionWS = TransformObjectToWorld(positionOS);
                OUT.uv = IN.uv;
                return OUT;
            }

            float frag(Varyings IN) : SV_Target
            {
                // As Thermal/Base: the surface's values from its property map where it has one, else the material's.
                float absorptivity = _SolarAbsorptivity;
                float emissivity = _Emissivity;
                if (_ThermalPropertyMapOn > 0.5)
                {
                    float2 painted = SAMPLE_TEXTURE2D(_ThermalPropertyMap, sampler_ThermalPropertyMap,
                                                      IN.uv * _ThermalMapST.xy + _ThermalMapST.zw).rg;
                    absorptivity = painted.r;
                    emissivity = painted.g;
                }

                ThermalPixel p;
                p.positionWS = IN.positionWS;
                p.normalWS = TerrainNormal(IN.uv);
                p.absorptivity = absorptivity;
                p.emissivity = emissivity;
                p.sunVisibility = MainLightRealtimeShadow(TransformWorldToShadowCoord(IN.positionWS));
                p.windA = _ThermalWindA;
                p.windB = _ThermalWindB;
                p.hasBack = _ThermalBackside;
                p.historyRow = _ThermalHistoryRow;
                return ThermalRadianceAtCamera(p);
            }
            ENDHLSL
        }
    }
}
