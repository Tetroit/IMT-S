#ifndef THERMAL_PHYSICS_INCLUDED
#define THERMAL_PHYSICS_INCLUDED


static const float SIGMA = 5.670374419e-8;
static const float CALM_CONVECTION = 0.84;  

struct ThermalEnvironment      // scene conditions
{
    float airTemperature, windSpeed;
    float directNormal, diffuseHorizontal, globalHorizontal;   // clear-sky sunlight, W/m^2
    float groundAlbedo;                                        // 0-1, for sunlight reflected off the ground
    float3 sunDirection;
};

struct ThermalSurface          // material + geometry factors
{
    float emissivity, solarAbsorptivity, skyViewFactor, sunVisibility, skyDiffuse, skyDiffuseBroadband, windA, windB;
};

struct EnergyBalance           // reduced coefficients — what the solver actually needs
{
    float absorbedSolar;       // alpha * E * cos(theta) * S   [W/m^2]
    float emissivity;
    float envT4;               // F*T_sky^4 + (1-F)*T_air^4    [K^4]
    float envTemperature;
    float windCoeff, airTemperature;
};

float ConvectionFromWind(float windCoeff, float dT)
{
    float calmPart = CALM_CONVECTION * pow(abs(dT), 1.0/3.0);
    return sqrt(pow(calmPart,2) + pow(windCoeff, 2));
}

float ConvectiveCoefficient(float windA, float windB, float windSpeed, float dT)
{
    return ConvectionFromWind(windA * pow(windSpeed, windB), dT);
}

EnergyBalance MakeBalance(float3 N, ThermalSurface s, ThermalEnvironment e)
{
    float cosTheta = saturate(dot(N, e.sunDirection));

    float Ta2 = e.airTemperature * e.airTemperature;

    EnergyBalance b;
    b.absorbedSolar   = s.solarAbsorptivity * (e.directNormal * cosTheta * s.sunVisibility 
        + e.diffuseHorizontal * s.skyViewFactor 
        + e.groundAlbedo * e.globalHorizontal * (1 - s.skyViewFactor));
    b.emissivity      = s.emissivity;
    b.envT4           = (1 - s.skyViewFactor + s.skyDiffuseBroadband) * Ta2 * Ta2;
    b.envTemperature = sqrt(sqrt(b.envT4));
    b.windCoeff = s.windA * pow(e.windSpeed, s.windB);
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
    float dT = T - b.airTemperature;
    float h = ConvectionFromWind(b.windCoeff, dT);
    
    return b.absorbedSolar
          + b.emissivity * SIGMA * b.envT4
          - b.emissivity * SIGMA * T * T * T * T
          - h * dT;
}

float fprime(float T, EnergyBalance b)
{
    float dT = T - b.airTemperature;
    float h = ConvectionFromWind(b.windCoeff, dT);
    float calm = CALM_CONVECTION * pow(abs(dT), 1.0/3.0);
    return -4.0 * b.emissivity * SIGMA * T * T * T - (h + (h > 0 ? calm * calm / (3.0 * h) : 0));
}

float SurfaceTemperature(EnergyBalance b)
{
    float h_rad = 4.0 * b.emissivity * SIGMA
                * b.airTemperature * b.airTemperature * b.airTemperature;

    float T0 = (b.absorbedSolar + h_rad * b.envTemperature
               + b.windCoeff * b.airTemperature)
            / (h_rad + b.windCoeff);
    
    float h1 = ConvectionFromWind(b.windCoeff, T0 - b.airTemperature);
    
    float T1 = (b.absorbedSolar + h_rad * b.envTemperature
           + h1 * b.airTemperature)
        / (h_rad + h1);

    [unroll]
    for (int i = 0; i < 3; ++i)
    {
        T1 -= f(T1, b) / fprime(T1, b);
    }
    
    return T1;
}

#endif
