Shader "Thermal/Base"
{
    Properties
    {
        _Emissivity ("Emissivity", Range(0,1)) = 0.95
        _Temperature ("Temperature [K]", Float) = 300
        _SolarAbsorptivity ("SolarAbsorptivity", Range(0,1)) = 0.9
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

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            #include "ThermalCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float _Temperature;
            float _Emissivity;
            float _SolarAbsorptivity;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                return OUT;
            }

            float frag(Varyings IN) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float S = MainLightRealtimeShadow(TransformWorldToShadowCoord(IN.positionWS));
                float F = SkyViewFactor(N);

                ThermalEnvironment env = MakeEnvironment();
                ThermalSurface surf;
                surf.emissivity = _Emissivity;
                surf.solarAbsorptivity = _SolarAbsorptivity;
                surf.skyViewFactor = F;
                surf.sunVisibility = S;
                
                EnergyBalance b = MakeBalance(N, surf, env);
                float surfaceTemperature = SurfaceTemperature(b);

                float surfaceRadiance = ThermalRadiance(surfaceTemperature);

                float incoming = lerp(ThermalRadiance(env.airTemperature), SkyRadiance(N), F);

                return surfaceRadiance * surf.emissivity + (1 - surf.emissivity) * incoming;
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Set by URP while it renders the shadow map. Declared here, never assigned by us.
            float3 _LightDirection;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVert(ShadowAttributes IN)
            {
                ShadowVaryings OUT;

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