Shader "Thermal/Preview"
{
    // Display only. Maps the single-channel radiance target to grey for looking at.
    // Nothing here is part of the measurement - the float buffer is never modified.
    Properties
    {
        _MainTex ("Radiance", 2D) = "black" {}
        _Low  ("Low limit  [W m-2 sr-1]", Float) = 10.0
        _High ("High limit [W m-2 sr-1]", Float) = 65.0
        _Gamma ("Apply display gamma", Float) = 0.0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float _Low;
            float _High;
            float _Gamma;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float L = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).r;

                // Guard a zero or inverted range rather than producing NaN across the whole image.
                float span = max(_High - _Low, 1e-6);
                float g = saturate((L - _Low) / span);

                // Perceptual only, and applied last. The values above are linear, which is what
                // every measurement wants; this exists because linear mid-grey looks too dark.
                if (_Gamma > 0.5)
                    g = pow(g, 1.0 / 2.2);

                return float4(g, g, g, 1.0);
            }
            ENDHLSL
        }
    }
}
