#ifndef THERMAL_SURFACE_RADIANCE_INCLUDED
#define THERMAL_SURFACE_RADIANCE_INCLUDED

// What the thermal camera sees of a surface: the sim's whole per-pixel path, from the energy balance to the air in
// front of it, in one function (2026-10-30, out of Thermal/Base's frag). Every shader that draws for the thermal camera
// calls it - Thermal/Base, Thermal/Terrain, and any renderer of its own that injects surfaces - so all of them compute
// what the sim computes, and a change to the physics reaches all of them at once.
// A shader brings what its surface is (ThermalPixel); everything that is the same for the whole scene - the sun, the
// air, the sky's tables, stored heat's past, the radiance LUT and the atmosphere's table - comes from the globals
// ThermalEnvironment sets.

#include "ThermalCommon.hlsl"

struct ThermalPixel
{
    float3 positionWS;
    float3 normalWS;        // what the sun, the sky view and the sky's diffuse factor see: unit, or a mip's shorter mean
    float absorptivity;     // solar, 0-1
    float emissivity;       // 8-14 µm, 0-1
    float sunVisibility;    // 0 in shadow .. 1 in the sun
    float windA, windB;     // the surface group's wind numbers (ThermalSurfaceGroups.Wind)
    float hasBack;          // 1 for a thin object's back face to the air behind it (ThermalSurfaceGroups.Back)
    float historyRow;       // the surface group's stored heat row (ThermalSurfaceGroups.HistoryRow), -1 for none
};

// The band radiance reaching the camera from this surface, W/m²/sr.
float ThermalRadianceAtCamera(ThermalPixel p)
{
    float3 N = p.normalWS;
    float F = SkyViewFactor(N);

    float2 skyD = SkyDiffuse(N.y);

    ThermalEnvironment env = MakeEnvironment();
    ThermalSurface surf;
    surf.emissivity = p.emissivity;
    surf.solarAbsorptivity = p.absorptivity;
    surf.skyViewFactor = F;
    surf.sunVisibility = p.sunVisibility;
    surf.skyDiffuse = skyD.x;
    surf.skyDiffuseBroadband = skyD.y;
    surf.windA = p.windA;
    surf.windB = p.windB;
    surf.hasBack = p.hasBack;

    // Masonry and ground answer to the past few hours, thin objects and vegetation to now (item 5 step 2). Thermal/
    // Base's row is one per draw, so every pixel of a draw takes the same branch; a surface whose group changes from
    // pixel to pixel branches per pixel.
    EnergyBalance b;
    if (p.historyRow >= 0)
        b = MakeHistoryBalance(N, surf, MakeHistory((int)p.historyRow));
    else
        b = MakeBalance(N, surf, env);
    float surfaceTemperature = SurfaceTemperature(b);

    // Per sub-band (ThermalCommon), summed for the camera at the end.
    float4 surfaceRadiance = ThermalSubBandRadiance(surfaceTemperature);

    float4 La = _ThermalAirSubBandRadiance;     // the same for every pixel: once per frame, on the CPU
    float4 reflected = (1 - F) * La + skyD.x * La;

    // What leaves the surface towards the camera: its own emission and what it reflects.
    float4 leaving = surfaceRadiance * surf.emissivity + (1 - surf.emissivity) * reflected;

    // Through the air to the camera, per sub-band (the atmosphere, step 4): what survives of it, and what the air adds
    // on the way (ThermalAtmosphere.hlsl).
    float3 camToPixel = p.positionWS - _WorldSpaceCameraPos;
    float4 tau;
    float4 path;
    ThermalAtmosphereAlong(camToPixel, tau, path);
    float4 arriving = tau * leaving + path;
    return BandRadiance(arriving);
}

#endif
