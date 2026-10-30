#ifndef THERMAL_ATMOSPHERE_INCLUDED
#define THERMAL_ATMOSPHERE_INCLUDED

// The atmosphere's table (step 3, ThermalAtmosphereTable): per sub-band, the transmission τ and the path radiance L_path
// from the camera along 128 directions out to 256 ranges. A range per column, a direction per row, the sub-bands in
// x, y, z and w. Plain HLSL, so the Atmosphere Check's compute shader reads it exactly as Thermal/Base does.
Texture2D<float4> _ThermalAtmosphereTau;
Texture2D<float4> _ThermalAtmospherePath;
float4 _ThermalAtmosphereParams;   // x: directions, y: ranges, z: range scale (m), w: the last range (m)

// The air between the camera and a point <fromCamera> metres away (world space, the point minus the camera): τ and
// L_path per sub-band. Read exactly, as the LUT is (item 10): the four neighbouring entries loaded unfiltered and
// blended in float. Rows are even in sign(v) v² of sin(elevation) and columns in log(1 + s / scale), so both are
// inverted here; ThermalAtmosphereTable.Lookup reads the same entries the same way on the CPU. Beyond the last range
// the last column, above or below every row the outermost.
void ThermalAtmosphereAlong(float3 fromCamera, out float4 tau, out float4 path)
{
    float s = length(fromCamera);
    float sinElevation = s > 0 ? fromCamera.y / s : 0;
    float v = sign(sinElevation) * sqrt(abs(sinElevation));
    float row = clamp((v + 1) * 0.5 * (_ThermalAtmosphereParams.x - 1), 0, _ThermalAtmosphereParams.x - 1);
    float column = clamp(log(1 + s / _ThermalAtmosphereParams.z) / log(1 + _ThermalAtmosphereParams.w / _ThermalAtmosphereParams.z)
                         * (_ThermalAtmosphereParams.y - 1), 0, _ThermalAtmosphereParams.y - 1);
    float r = min(floor(row), _ThermalAtmosphereParams.x - 2);
    float c = min(floor(column), _ThermalAtmosphereParams.y - 2);
    float fr = row - r, fc = column - c;

    float4 t00 = _ThermalAtmosphereTau.Load(int3(c, r, 0)), t01 = _ThermalAtmosphereTau.Load(int3(c + 1, r, 0));
    float4 t10 = _ThermalAtmosphereTau.Load(int3(c, r + 1, 0)), t11 = _ThermalAtmosphereTau.Load(int3(c + 1, r + 1, 0));
    tau = lerp(lerp(t00, t01, fc), lerp(t10, t11, fc), fr);

    float4 p00 = _ThermalAtmospherePath.Load(int3(c, r, 0)), p01 = _ThermalAtmospherePath.Load(int3(c + 1, r, 0));
    float4 p10 = _ThermalAtmospherePath.Load(int3(c, r + 1, 0)), p11 = _ThermalAtmospherePath.Load(int3(c + 1, r + 1, 0));
    path = lerp(lerp(p00, p01, fc), lerp(p10, p11, fc), fr);
}

#endif
