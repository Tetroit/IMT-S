#ifndef THERMAL_COMMON_INCLUDED
#define THERMAL_COMMON_INCLUDED

#include "ThermalPhysics.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

TEXTURE2D(_ThermalLut);
SAMPLER(sampler_ThermalLut);
float4 _ThermalLutParams;
float _ThermalSkyTemperature;
float _ThermalSkyTemperature4;
float3 _ThermalSunDirection;
float _ThermalAirTemperature;
float _ThermalAirTemperature4;
float _ThermalSolarIrradiance;
float _ThermalConvectiveCoefficient;

ThermalEnvironment MakeEnvironment()
{
    ThermalEnvironment e;
    e.airTemperature  = _ThermalAirTemperature;
    e.skyTemperature  = _ThermalSkyTemperature;
    e.solarIrradiance = _ThermalSolarIrradiance;
    e.convectiveCoeff = _ThermalConvectiveCoefficient;
    e.sunDirection    = _ThermalSunDirection;
    return e;
}

float ThermalRadiance(float temperatureK)
{
    float u = LutCoord(temperatureK, _ThermalLutParams.x,
                       _ThermalLutParams.y, _ThermalLutParams.z);
    return SAMPLE_TEXTURE2D_LOD(_ThermalLut, sampler_ThermalLut, float2(u, 0.5), 0).r;
}

float SkyRadiance(float3 direction)
{
    // `direction` is a placeholder for a directional sky. Uniform for now, which
    // is why this and the balance describe the same sky by construction.
    return ThermalRadiance(_ThermalSkyTemperature);
}

#endif
