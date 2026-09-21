using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace IMT.Thermal
{
    public struct ThermalConstants
    {
        public const double BoltzmannConstant = 1.380649e-23;
        public const double SpeedOfLight = 299792458;
        public const double PlanckConstant = 6.62607015e-34;
    }

    public class ThermalMath : MonoBehaviour
    {
        
        public static double ComputePlanckLaw(double lambdaMeter, double temperatureK)
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

        [MenuItem("Thermal/Planck Law Test")]
        public static void TestPlanckLawImplementation()
        {
            double thermalK = 300;
            double waveLengthM = 9.659 * 1e-6;

            double B = ComputePlanckLaw(waveLengthM, thermalK);

            Debug.Log($"ThermalK: {thermalK}K, wavelength: {waveLengthM}m, result: {B}");
        }

        public static double BandIntegration(double temperatureK)
        {
            double lo = 8e-6, hi = 14e-6, n = 100;
            var dl = (hi - lo) / n;
            double integral = 0;
            for (int i = 0; i < n; i++)
            {
                integral += ComputePlanckLaw(lo + (i + 0.5) * dl, temperatureK);
            }

            integral *= dl;

            return integral;
        }

        [MenuItem("Thermal/PredictRadiance")]
        public static void PredictRadiance()
        {
            
        }

        [MenuItem("Thermal/LutTest")]
        public static void CreateLUT()
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

            double maxError = 0;
            double maxErrorT = 0;
            double maxErrori = 0;

            for (int i = 0; i < samples - 2; i++)
            {
                double T = lower + (i + 0.5) / (samples - 1) * (higher - lower);
                float lut = 0.5f * (data[i] + data[i + 1]);
                double err = Math.Abs(lut - BandIntegration(T)) / BandIntegration(T);

                if (err > maxError)
                {
                    maxError = err;
                    maxErrorT = T;
                    maxErrori = i;
                }
            }

            Debug.Log($"Max error: {maxError}, produced by: {maxErrorT}K, produced by index: {maxErrori}");
            
            tex.SetPixelData(data, 0);
            tex.Apply(false, false);
            
            Shader.SetGlobalTexture("_ThermalLut", tex);
            Shader.SetGlobalVector("_ThermalLutParams", new Vector4((float)lower, (float)higher, samples, 0));
            
            //Shader.SetGlobalFloat("_SkyTemp", skyTempK);
        }

        [MenuItem("Thermal/Convergence Check")]
        public static void ConvergenceCheckBandIntergration()
        {
            double temperatureK = 300;
            double lo = 8e-6, hi = 14e-6, n = 1000;
            List<int> iters = new List<int> { 10, 100, 1000, 10000, 100000 };
            foreach (var iter in iters)
            {
                var dl = (hi - lo) / iter;
                double integral = 0;
                for (int i = 0; i < iter; i++)
                {
                    integral += ComputePlanckLaw(lo + (i + 0.5) * dl, temperatureK);
                }

                integral *= dl;
                Debug.Log(
                    $"iter={iter}  dl={dl:E3}  covers {lo * 1e6:F3} to {(lo + iter * dl) * 1e6:F3} um  -> {integral}");
            }
        }
    }
}