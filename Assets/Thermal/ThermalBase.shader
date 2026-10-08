Shader "Thermal/Base"
{
    Properties
    {
        _Emissivity ("Emissivity", Range(0,1)) = 0.95
        _Temperature ("Temperature [K]", Float) = 300
        _SolarAbsorptivity ("SolarAbsorptivity", Range(0,1)) = 0.9

        // D-010 MVP maps, set per object by ThermalObject. Off by default, so anything without them
        // renders exactly as before.
        [NoScaleOffset] _ThermalPropertyMap ("Property Map (R absorptivity, G emissivity, linear)", 2D) = "white" {}
        _ThermalPropertyMapOn ("Use Property Map", Float) = 0
        [NoScaleOffset][Normal] _ThermalNormalMap ("Normal Map", 2D) = "bump" {}
        _ThermalNormalStrength ("Normal Strength (0 = off)", Range(0,1)) = 0
        _ThermalNormalMipBias ("Normal Mip Bias", Float) = 0
        _ThermalMapST ("Map Tiling (xy) and Offset (zw)", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ThermalBase"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            // Instanced draws - the house tool's modules - pass each instance's matrices in an instancing
            // buffer, which only this variant reads. The override material needs GPU instancing on too.
            #pragma multi_compile_instancing

            #include "ThermalCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;     // for the normal map; all zero on a mesh without tangents
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float4 tangentWS : TEXCOORD2;   // w: the bitangent's sign
                float2 uv : TEXCOORD3;
            };

            float _Temperature;
            float _Emissivity;
            float _SolarAbsorptivity;

            TEXTURE2D(_ThermalPropertyMap);
            SAMPLER(sampler_ThermalPropertyMap);
            TEXTURE2D(_ThermalNormalMap);
            SAMPLER(sampler_ThermalNormalMap);
            float _ThermalPropertyMapOn;
            float _ThermalNormalStrength;
            float _ThermalNormalMipBias;
            float4 _ThermalMapST;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);    // the object matrices below become this instance's
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.tangentWS = float4(TransformObjectToWorldDir(IN.tangentOS.xyz), IN.tangentOS.w * GetOddNegativeScale());
                OUT.uv = IN.uv * _ThermalMapST.xy + _ThermalMapST.zw;
                return OUT;
            }

            float frag(Varyings IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);

                // The normal map bends the normal everything below sees - the sun angle, the sky view and
                // the sky's diffuse factor - while the shadow map keeps the geometry's. Both branches are
                // per object: without a map an object takes exactly the path it always did.
                float3 T = IN.tangentWS.xyz - N * dot(N, IN.tangentWS.xyz);    // Gram-Schmidt onto the surface
                if (_ThermalNormalStrength > 0 && dot(T, T) > 1e-12)
                {
                    float4 packedNormal = SAMPLE_TEXTURE2D_BIAS(_ThermalNormalMap, sampler_ThermalNormalMap,
                                                                IN.uv, _ThermalNormalMipBias);
                    float3 nTS = UnpackNormalScale(packedNormal, _ThermalNormalStrength);
                    T = normalize(T);
                    float3 B = cross(N, T) * IN.tangentWS.w;
                    N = normalize(nTS.x * T + nTS.y * B + nTS.z * N);
                }

                float emissivity = _Emissivity;
                float absorptivity = _SolarAbsorptivity;
                if (_ThermalPropertyMapOn > 0.5)
                {
                    float2 painted = SAMPLE_TEXTURE2D(_ThermalPropertyMap, sampler_ThermalPropertyMap, IN.uv).rg;
                    absorptivity = painted.r;
                    emissivity = painted.g;
                }

                float S = MainLightRealtimeShadow(TransformWorldToShadowCoord(IN.positionWS));
                float F = SkyViewFactor(N);

                float2 skyD = SkyDiffuse(N.y);

                ThermalEnvironment env = MakeEnvironment();
                ThermalSurface surf;
                surf.emissivity = emissivity;
                surf.solarAbsorptivity = absorptivity;
                surf.skyViewFactor = F;
                surf.sunVisibility = S;
                surf.skyDiffuse = skyD.x;
                surf.skyDiffuseBroadband = skyD.y;
                
                EnergyBalance b = MakeBalance(N, surf, env);
                float surfaceTemperature = SurfaceTemperature(b);

                float surfaceRadiance = ThermalRadiance(surfaceTemperature);

                float La = ThermalRadiance(env.airTemperature);
                float reflected = (1 - F) * La + skyD.x * La;

                return surfaceRadiance * surf.emissivity + (1 - surf.emissivity) * reflected;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Set by URP while it renders the shadow map. Declared here, never assigned by us.
            float3 _LightDirection;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVert(ShadowAttributes IN)
            {
                ShadowVaryings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

                // Nudge the caster away from the light, in world space, before projection.
                // Without it a surface shadow-tests against its own quantised depth and the
                // comparison is a coin flip per pixel - acne, which here means a speckle of
                // pixels reading ~28 K too cold.
                positionWS = ApplyShadowBias(positionWS, normalWS, _LightDirection);

                OUT.positionCS = TransformWorldToHClip(positionWS);

                // That nudge can push a vertex past the near plane, which would clip it out of
                // the map and drop its shadow entirely.
                #if UNITY_REVERSED_Z
                OUT.positionCS.z = min(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                OUT.positionCS.z = max(OUT.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return OUT;
            }

            float4 ShadowFrag(ShadowVaryings IN) : SV_Target
            {
                return 0; // ColorMask 0 - only the depth buffer is consumed
            }
            ENDHLSL
        }
    }
}