using System;
using IMT.Thermal;
using Unity.Mathematics;
using UnityEngine;

[ExecuteAlways]
public class ThermalEnvironment : MonoBehaviour
{
    [SerializeField, Range(180f, 320f)] float m_SkyTemperature = 220f;
    [SerializeField] float m_AirTemperature = 295f;
    [SerializeField] private GameObject sun;
    [SerializeField] private float m_SolarIrradiance = 900;
    [SerializeField] private float m_ConvectiveCoefficient = 15;

    void OnValidate() { Push(); }
                                     void Update()     { Push(); }

    void Push()
    {
        //predictRadiance();
        (Texture2D tex, Vector4 parameters) = createLut();
        Shader.SetGlobalTexture("_ThermalLut", tex);
        Shader.SetGlobalVector("_ThermalLutParams", parameters);
            
        Shader.SetGlobalFloat("_ThermalSkyTemperature", m_SkyTemperature); // TODO: push the fourth power of this and air temp for quicker flux calculations
        Shader.SetGlobalFloat("_ThermalSkyTemperature4", (float)Math.Pow(m_SkyTemperature, 4.0f)); // TODO: push the fourth power of this and air temp for quicker flux calculations
        
        Shader.SetGlobalVector("_ThermalSunDirection", -sun.transform.forward);
        
        Shader.SetGlobalFloat("_ThermalAirTemperature", m_AirTemperature);
        Shader.SetGlobalFloat("_ThermalAirTemperature4", (float)Math.Pow(m_AirTemperature, 4.0f));
        Shader.SetGlobalFloat("_ThermalSolarIrradiance", m_SolarIrradiance);
        Shader.SetGlobalFloat("_ThermalConvectiveCoefficient", m_ConvectiveCoefficient);
    }
    
    const float SIGMA = 5.670374419e-8f;
    
    private float f(float T, float solarFlux, float eps, float alpha, float T_air, float T_sky, float h)
    {
        float absorbed = alpha * solarFlux;
        float sky_in = eps * SIGMA * T_sky*T_sky*T_sky*T_sky;
        float emitted = eps * SIGMA * T*T*T*T;
        float convected = h * (T - T_air);
        return absorbed + sky_in - emitted - convected;
    }

    private float fprime(float T, float eps, float h)
    {
        return -4.0f * eps * SIGMA * T*T*T - h;
    }

    private void predictRadiance()
    {
        var sunDir = -sun.transform.forward;
        float emissivity = 0.1f;
        float solarAbsorptivity = 0.9f;

        Vector3[] faces = new Vector3[]
        {
            new Vector3(0, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0),
            new Vector3(-1, 0, 0), new Vector3(0, 0, -1)
        };

        foreach (var face in faces)
        {
            var cosTheta = Math.Max(0, Vector3.Dot(face, sunDir));
            float solarFlux = m_SolarIrradiance * cosTheta;
            float h_rad = 4.0f * emissivity * SIGMA * m_AirTemperature*m_AirTemperature*m_AirTemperature;
            float Temperature = (solarAbsorptivity * solarFlux + h_rad * m_SkyTemperature +
                                 m_ConvectiveCoefficient * m_AirTemperature) / (h_rad + m_ConvectiveCoefficient);

            Temperature -= f(Temperature, solarFlux, emissivity, solarAbsorptivity,
                m_AirTemperature, m_SkyTemperature,
                m_ConvectiveCoefficient) / fprime(
                Temperature, emissivity, m_ConvectiveCoefficient);
            Temperature -= f(Temperature, solarFlux, emissivity, solarAbsorptivity,
                m_AirTemperature, m_SkyTemperature,
                m_ConvectiveCoefficient) / fprime(
                Temperature, emissivity, m_ConvectiveCoefficient);
            
            var L = emissivity * BandIntegration(Temperature) + (1 - emissivity) * BandIntegration(m_SkyTemperature);
            
            Debug.Log(L);
        }
    } 

    private (Texture2D, Vector4) createLut()
    {
        int samples = 1024;
        double lower = 180, higher = 1200;

        Texture2D tex = new Texture2D(samples, 1, TextureFormat.RFloat, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        float[] data = new float[samples];

        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)(samples - 1);
            double T = lower + t * (higher - lower);

            data[i] = (float)BandIntegration(T);
        }
        
        tex.SetPixelData(data, 0);
        tex.Apply(false, true);

        return (tex, new Vector4((float)lower, (float)higher, samples, 0));
    }

    private double BandIntegration(double T)
    {
        double lo = 8e-6, hi = 14e-6, n = 100;
        var dl = (hi - lo) / n;
        double integral = 0;
        for (int i = 0; i < n; i++)
        {
            integral += ComputePlanckLaw(lo + (i + 0.5) * dl, T);
        }

        integral *= dl;

        return integral;
    }
    
    private double ComputePlanckLaw(double lambdaMeter, double temperatureK)
    {
        double x = (ThermalConstants.PlanckConstant * ThermalConstants.SpeedOfLight) /
                   (lambdaMeter * ThermalConstants.BoltzmannConstant * temperatureK);
        double secondHalf;
        if (x > 30)
        {
            secondHalf = Math.Exp(-x);
        }
        else
        {
            secondHalf = 1.0 / (Math.Exp(x) - 1.0);
        }


        var B = ((2 * ThermalConstants.PlanckConstant * Math.Pow(ThermalConstants.SpeedOfLight, 2)) /
                 Math.Pow(lambdaMeter, 5)) * secondHalf;
        return B;
    }
}
