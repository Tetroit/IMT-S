using System;
using UnityEngine;

namespace ProceduralGeneration
{
    /// <summary>
    /// Global scene config for converting mercator coordinates (EPSG:3857) to Unity space.
    /// Unity position = (mercator - origin) * scale.
    /// </summary>
    public class GeometryContext : MonoBehaviour
    {
        [Tooltip("Unity units per mercator unit.")]
        public double scale = 1.0;

        [Tooltip("Mercator coordinates (EPSG:3857, metres) that map to Unity (0, 0).")]
        public DoubleVector2 origin;

        public DoubleVector2 MercatorToWorld(DoubleVector2 mercator) => (mercator - origin) * scale;

        public SatContext.SatContext originReference;

        /// <summary>
        /// Sets origin to the center of <see cref="originReference"/> tile
        /// </summary>
        public void SetOriginAtContextReference()
        {
            double[] bounds = originReference.metadata.output.mercatorBoundsM;
            origin.x = (bounds[0] + bounds[2])/2;
            origin.y = (bounds[1] + bounds[3])/2;
        }
    }
}
