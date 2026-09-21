using System;
using UnityEngine;

namespace IMT.Thermal
{
    [CreateAssetMenu(fileName = "ThermalMaterial", menuName = "Thermal/Material")]
    public class ThermalMaterial : ScriptableObject
    {
        [SerializeField, Range(0, 1)] public float m_Emissivity = 0.98f;
        [SerializeField, Range(0, 1)] public float m_SolarAbsorptivity = 0.9f;

        public static readonly int k_Emissivity = Shader.PropertyToID("_Emissivity");
        public static readonly int k_SolarAbsorptivity = Shader.PropertyToID("_SolarAbsorptivity");


        private void OnValidate()
        {
#if UNITY_EDITOR
            foreach (var o in FindObjectsByType<ThermalObject>())
                if (o.material == this)
                    o.Push();
#endif
        }
    }
}