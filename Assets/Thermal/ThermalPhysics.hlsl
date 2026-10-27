#ifndef THERMAL_PHYSICS_INCLUDED
#define THERMAL_PHYSICS_INCLUDED
#define THERMAL_HISTORY_SAMPLES 8

static const float SIGMA = 5.670374419e-8;
static const float CALM_CONVECTION = 0.84;
//ISO 6946 internal surface resistances, inner surface ε 0.9, convection + radiation
static const float BACK_UP   = 1.0 / 0.10;             //   heat flowing upward    (10 W/m²K)
static const float BACK_SIDE = 1.0 / 0.13;             //   heat flowing sideways  (7.69)
static const float BACK_DOWN = 1.0 / 0.17;             //   heat flowing downward  (5.88)

struct ThermalEnvironment      // scene conditions
{
    float airTemperature, windSpeed;
    float directNormal, diffuseHorizontal, globalHorizontal;   // clear-sky sunlight, W/m^2
    float groundAlbedo;                                        // 0-1, for sunlight reflected off the ground
    float3 sunDirection;
};

struct ThermalSurface          // material + geometry factors
{
    float emissivity, solarAbsorptivity, skyViewFactor, sunVisibility, skyDiffuse, skyDiffuseBroadband, windA, windB, hasBack;
};

struct EnergyBalance           // reduced coefficients — what the solver actually needs
{
    float absorbedSolar;       // alpha * E * cos(theta) * S   [W/m^2]
    float emissivity;
    float envT4;               // F*T_sky^4 + (1-F)*T_air^4    [K^4]
    float envTemperature;
    float windCoeff, airTemperature, hasBack, normalY;
};

struct ThermalHistory
{
    ThermalEnvironment past[THERMAL_HISTORY_SAMPLES]; 
    float weight[THERMAL_HISTORY_SAMPLES];
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

float BackCoefficient(float normalY, float dT)
{
    float up = (dT >= 0 ? - normalY : normalY);
    if (up >= 0)
    {
        return lerp(BACK_SIDE, BACK_UP, up);
    }
    else
    {
        return lerp(BACK_DOWN, BACK_SIDE, up + 1);
    }
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
    b.hasBack = s.hasBack;
    b.normalY = N.y;
    return b;
}

EnergyBalance MakeHistoryBalance(float3 N, ThermalSurface s, ThermalHistory h)
{
    float direct = 0;
    float diffuse = 0;
    float global = 0;
    float air4 = 0;
    float wind = 0;
    float air = 0;
    
    [unroll]
    for (int k = 0; k < THERMAL_HISTORY_SAMPLES; k++)
    {
        ThermalEnvironment e = h.past[k];
        float w = h.weight[k];
        direct += w * e.directNormal * saturate(dot(N, e.sunDirection));
        diffuse += w * e.diffuseHorizontal;
        global += w * e.globalHorizontal;
        air4 += w * e.airTemperature * e.airTemperature * e.airTemperature * e.airTemperature;
        wind += w * e.windSpeed;
        air += w * e.airTemperature;
    }
    
    EnergyBalance e;
    e.absorbedSolar = s.solarAbsorptivity * (direct * s.sunVisibility + diffuse * s.skyViewFactor + h.past[0].groundAlbedo * global * (1 - s.skyViewFactor));
    e.envT4 = (1 - s.skyViewFactor + s.skyDiffuseBroadband) * air4;
    e.envTemperature = sqrt(sqrt(e.envT4));
    e.windCoeff = s.windA * pow(wind, s.windB);
    e.airTemperature = air;
    e.emissivity = s.emissivity;
    e.hasBack = s.hasBack;
    e.normalY = N.y;
    
    return e;
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
    float h = ConvectionFromWind(b.windCoeff, dT)
        + b.hasBack * BackCoefficient(b.normalY, dT);
    
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
    return -4.0 * b.emissivity * SIGMA * T * T * T - (h + (h > 0 ? calm * calm / (3.0 * h) : 0)) - b.hasBack * BackCoefficient(b.normalY, dT);
}

float SurfaceTemperature(EnergyBalance b)
{
    float h_rad = 4.0 * b.emissivity * SIGMA
                * b.airTemperature * b.airTemperature * b.airTemperature;

    float h0 = b.windCoeff + b.hasBack * BackCoefficient(b.normalY, 0);
    
    float T0 = (b.absorbedSolar + h_rad * b.envTemperature
               + h0 * b.airTemperature)
            / (h_rad + h0);
    
    float h1 = ConvectionFromWind(b.windCoeff, T0 - b.airTemperature) + b.hasBack * BackCoefficient(b.normalY, T0 - b.airTemperature);
    
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
