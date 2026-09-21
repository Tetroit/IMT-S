#ifndef THERMAL_PHYSICS_INCLUDED
#define THERMAL_PHYSICS_INCLUDED



static const float SIGMA = 5.670374419e-8;

struct ThermalEnvironment      // scene conditions
{
    float airTemperature, skyTemperature, solarIrradiance, convectiveCoeff;
    float3 sunDirection;
};

struct ThermalSurface          // material + geometry factors
{
    float emissivity, solarAbsorptivity, skyViewFactor, sunVisibility;
};

struct EnergyBalance           // reduced coefficients — what the solver actually needs
{
    float absorbedSolar;       // alpha * E * cos(theta) * S   [W/m^2]
    float emissivity;
    float envT4;               // F*T_sky^4 + (1-F)*T_air^4    [K^4]
    float envTemperature;
    float convectiveCoeff, airTemperature;
};

EnergyBalance MakeBalance(float3 N, ThermalSurface s, ThermalEnvironment e)
{
    float cosTheta = saturate(dot(N, e.sunDirection));

    float Ta2 = e.airTemperature * e.airTemperature;
    float Ts2 = e.skyTemperature * e.skyTemperature;

    EnergyBalance b;
    b.absorbedSolar   = s.solarAbsorptivity * e.solarIrradiance * cosTheta * s.sunVisibility;
    b.emissivity      = s.emissivity;
    b.envT4           = lerp(Ta2 * Ta2, Ts2 * Ts2, s.skyViewFactor);
    b.envTemperature = sqrt(sqrt(b.envT4));
    b.convectiveCoeff = e.convectiveCoeff;
    b.airTemperature  = e.airTemperature;
    return b;
}

float SkyViewFactor(float3 N)
{
    return 0.5 * (1.0 + N.y);     // exact for an unoccluded surface over flat ground
}

float LutCoord(float T, float loK, float hiK, float entries)
{
    float t = (T - loK) / (hiK - loK);
    return (t * (entries - 1) + 0.5) / entries;
}

float f(float T, EnergyBalance b)
{
    return b.absorbedSolar
          + b.emissivity * SIGMA * b.envT4
          - b.emissivity * SIGMA * T * T * T * T
          - b.convectiveCoeff * (T - b.airTemperature);
}

float fprime(float T, EnergyBalance b)
{
    return -4.0 * b.emissivity * SIGMA * T * T * T - b.convectiveCoeff;
}

float SurfaceTemperature(EnergyBalance b)
{
    float h_rad = 4.0 * b.emissivity * SIGMA
                * b.airTemperature * b.airTemperature * b.airTemperature;

    float T = (b.absorbedSolar + h_rad * b.envTemperature
               + b.convectiveCoeff * b.airTemperature)
            / (h_rad + b.convectiveCoeff);

    T -= f(T, b) / fprime(T, b);
    T -= f(T, b) / fprime(T, b);
    return T;
}

#endif
