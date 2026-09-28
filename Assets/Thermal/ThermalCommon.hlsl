#ifndef THERMAL_COMMON_INCLUDED
#define THERMAL_COMMON_INCLUDED

#include "ThermalPhysics.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

TEXTURE2D(_ThermalLut);
SAMPLER(sampler_ThermalLut);
float4 _ThermalLutParams;
float3 _ThermalSunDirection;
float _ThermalAirTemperature;
float _ThermalSolarIrradiance;
float _ThermalConvectiveCoefficient;

// D-013 sky tables, baked by ThermalEnvironment from ThermalMath.SkyEmissivity / SkyDiffuse.
// Dimensionless: multiply by ThermalRadiance(T_air) for in-band radiance, by T_air^4 for the balance.
TEXTURE2D(_ThermalSkyView);
SAMPLER(sampler_ThermalSkyView);
TEXTURE2D(_ThermalSkyDiffuse);
SAMPLER(sampler_ThermalSkyDiffuse);
float4 _ThermalSkyParams;   // x: view entries, y: diffuse entries

ThermalEnvironment MakeEnvironment()
{
    ThermalEnvironment e;
    e.airTemperature  = _ThermalAirTemperature;
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

// Sky emissivity looking along a normalised world direction. At or below the horizon this is the
// table's first entry, which the sky model must make exactly 1 (checked when the table is baked).
float SkyViewEmissivity(float3 direction)
{
    // spaced evenly in sqrt(direction.y), so the entries crowd the horizon where the sky changes fastest
    float u = LutCoord(sqrt(saturate(direction.y)), 0.0, 1.0, _ThermalSkyParams.x);
    return SAMPLE_TEXTURE2D_LOD(_ThermalSkyView, sampler_ThermalSkyView, float2(u, 0.5), 0).r;
}

// Visible-sky diffuse factors for a surface normal: x in-band, y broadband. Each already contains the
// view factor - it replaces F * sky, so never multiply it by F again.
float2 SkyDiffuse(float normalY)
{
    float u = LutCoord(normalY, -1.0, 1.0, _ThermalSkyParams.y);
    return SAMPLE_TEXTURE2D_LOD(_ThermalSkyDiffuse, sampler_ThermalSkyDiffuse, float2(u, 0.5), 0).rg;
}

float SkyRadiance(float3 direction)
{
    return SkyViewEmissivity(direction) * ThermalRadiance(_ThermalAirTemperature);
}

#endif
