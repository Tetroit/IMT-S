#ifndef THERMAL_COMMON_INCLUDED
#define THERMAL_COMMON_INCLUDED

#include "ThermalPhysics.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// Radiance per sub-band: the atmosphere's four (ThermalAtmosphere; ThermalMath.SubBandEdges), 14-12.5, 12.5-10.22,
// 10.22-9.26 and 9.26-8 µm, in x, y, z and w. The camera sees their sum (BandRadiance).
TEXTURE2D(_ThermalSubBandLut);
float4 _ThermalLutParams;
float3 _ThermalSunDirection;
float _ThermalAirTemperature;
float4 _ThermalAirSubBandRadiance;     // the LUT at the air temperature, interpolated once per frame on the CPU
float _ThermalWindSpeed;               // m/s at 10 m, open terrain (item 6)
float _ThermalDirectNormalIrradiance;
float _ThermalDiffuseHorizontalIrradiance;
float _ThermalGlobalHorizontalIrradiance;
float _ThermalGroundAlbedo;

// D-013 sky tables, baked by ThermalEnvironment from ThermalMath.SkyEmissivity / SkyDiffuse.
// Dimensionless: multiply by _ThermalAirSubBandRadiance for in-band radiance, by T_air^4 for the balance.
TEXTURE2D(_ThermalSkyView);
SAMPLER(sampler_ThermalSkyView);
TEXTURE2D(_ThermalSkyDiffuse);
SAMPLER(sampler_ThermalSkyDiffuse);
float4 _ThermalSkyParams;   // x: view entries, y: diffuse entries

ThermalEnvironment MakeEnvironment()
{
    ThermalEnvironment e;
    e.airTemperature    = _ThermalAirTemperature;
    e.windSpeed         = _ThermalWindSpeed;
    e.directNormal      = _ThermalDirectNormalIrradiance;
    e.diffuseHorizontal = _ThermalDiffuseHorizontalIrradiance;
    e.globalHorizontal  = _ThermalGlobalHorizontalIrradiance;
    e.groundAlbedo      = _ThermalGroundAlbedo;
    e.sunDirection      = _ThermalSunDirection;
    return e;
}

// The LUT read exactly (item 10, 2026-10-27): its two neighbouring entries loaded unfiltered and blended in float. The
// hardware's bilinear filter holds its weight in 8 bits, so a temperature on one of its 1/256 steps jumped a whole step
// (0.0037 W/m²/sr, 0.11 NETD) whenever the GPU computed it one ulp apart from one frame to the next. ThermalEnvironment's
// SubBandRadiance interpolates the same entries the same way on the CPU.
float4 ThermalSubBandRadiance(float temperatureK)
{
    float x = saturate((temperatureK - _ThermalLutParams.x) / (_ThermalLutParams.y - _ThermalLutParams.x))
            * (_ThermalLutParams.z - 1);
    float i = min(floor(x), _ThermalLutParams.z - 2);
    float4 a = LOAD_TEXTURE2D(_ThermalSubBandLut, int2(i, 0));
    float4 b = LOAD_TEXTURE2D(_ThermalSubBandLut, int2(i + 1, 0));
    return lerp(a, b, x - i);
}

// The band radiance of a radiance per sub-band: their sum.
float BandRadiance(float4 subBands)
{
    return subBands.x + subBands.y + subBands.z + subBands.w;
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

// Per sub-band, the sky's in-band emissivity in each: grey across the band for now.
float4 SkyRadiance(float3 direction)
{
    return SkyViewEmissivity(direction) * _ThermalAirSubBandRadiance;
}

// Item 5 step 2, stored heat: the past samples ThermalEnvironment pushes each frame (sample 0 is now, the same values
// as the globals above), and per surface group that stores heat its weights, row r's in [2r] and [2r + 1]. The ground
// albedo and the shadow are the current ones for every sample.
#define THERMAL_HISTORY_ROWS 5
float4 _ThermalHistorySun[THERMAL_HISTORY_SAMPLES];        // xyz toward the sun, w direct normal irradiance
float4 _ThermalHistoryAir[THERMAL_HISTORY_SAMPLES];        // x air K, y wind m/s, z diffuse and w global horizontal
float4 _ThermalHistoryWeights[THERMAL_HISTORY_ROWS * 2];

ThermalHistory MakeHistory(int row)
{
    ThermalHistory h;
    [unroll]
    for (int k = 0; k < THERMAL_HISTORY_SAMPLES; k++)
    {
        ThermalEnvironment e;
        e.airTemperature    = _ThermalHistoryAir[k].x;
        e.windSpeed         = _ThermalHistoryAir[k].y;
        e.directNormal      = _ThermalHistorySun[k].w;
        e.diffuseHorizontal = _ThermalHistoryAir[k].z;
        e.globalHorizontal  = _ThermalHistoryAir[k].w;
        e.groundAlbedo      = _ThermalGroundAlbedo;
        e.sunDirection      = _ThermalHistorySun[k].xyz;
        h.past[k] = e;
        h.weight[k] = _ThermalHistoryWeights[row * 2 + k / 4][k % 4];
    }
    return h;
}

#endif
