using System;
using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    /// <summary>
    /// Pan and zoom state of the Flight Preview minimap, maps mercator to minimap pixels.
    /// Positions are kept relative to the origin in doubles, only the final pixel is a float.
    /// </summary>
    [Serializable]
    public class FlightPreviewMinimap
    {
        private const double MinPixelsPerUnit = 1e-5;
        private const double MaxPixelsPerUnit = 50;

        [Tooltip("Mercator offset from the origin shown at the minimap center.")]
        public DoubleVector2 viewCenter;
        public double pixelsPerUnit = 0.05;

        /// <summary>Minimap rect in its own clip space, (0, 0) is the top-left corner.</summary>
        [NonSerialized] public Rect rect;
        [NonSerialized] public DoubleVector2 origin;

        public Vector2 ToScreen(DoubleVector2 mercator)
        {
            DoubleVector2 d = mercator - origin - viewCenter;
            return new Vector2((float)(rect.width * 0.5 + d.x * pixelsPerUnit), (float)(rect.height * 0.5 - d.y * pixelsPerUnit));
        }

        public DoubleVector2 ToMercator(Vector2 screen)
        {
            return origin + viewCenter + new DoubleVector2(
                (screen.x - rect.width * 0.5) / pixelsPerUnit,
                -(screen.y - rect.height * 0.5) / pixelsPerUnit);
        }

        public double MercatorPerMeter => WebMercator.MercatorPerMeter(origin.y);

        public float MetersToPixels(double meters) => (float)(meters * MercatorPerMeter * pixelsPerUnit);

        public void Pan(Vector2 pixelDelta)
        {
            viewCenter -= new DoubleVector2(pixelDelta.x / pixelsPerUnit, -pixelDelta.y / pixelsPerUnit);
        }

        /// <summary>Zooms keeping the mercator point under <paramref name="screen"/> in place.</summary>
        public void ZoomAt(Vector2 screen, double factor)
        {
            DoubleVector2 before = ToMercator(screen);
            pixelsPerUnit = Math.Clamp(pixelsPerUnit * factor, MinPixelsPerUnit, MaxPixelsPerUnit);
            viewCenter += before - ToMercator(screen);
        }

        public void Frame(DoubleVector2 min, DoubleVector2 max, float padding = 0.1f)
        {
            viewCenter = (min + max) / 2 - origin;
            double width = Math.Max(max.x - min.x, 1);
            double height = Math.Max(max.y - min.y, 1);
            double scale = Math.Min(rect.width / width, rect.height / height) * (1 - 2 * padding);
            pixelsPerUnit = Math.Clamp(scale, MinPixelsPerUnit, MaxPixelsPerUnit);
        }

        /// <summary>Tile rect in pixels, clamped a little outside the minimap to keep float precision when zoomed in.</summary>
        public bool GetTileRect(TileBounds tile, out Rect screenRect)
        {
            Vector2 bottomLeft = ToScreen(tile.min);
            Vector2 topRight = ToScreen(tile.max);
            screenRect = Rect.MinMaxRect(bottomLeft.x, topRight.y, topRight.x, bottomLeft.y);
            if (!screenRect.Overlaps(rect))
                return false;
            const float margin = 4;
            screenRect = Rect.MinMaxRect(
                Mathf.Max(screenRect.xMin, rect.xMin - margin),
                Mathf.Max(screenRect.yMin, rect.yMin - margin),
                Mathf.Min(screenRect.xMax, rect.xMax + margin),
                Mathf.Min(screenRect.yMax, rect.yMax + margin));
            return true;
        }

        public void DrawScaleBar(Vector2 bottomLeft)
        {
            double metersPerPixel = 1 / (pixelsPerUnit * MercatorPerMeter);
            double target = metersPerPixel * 100;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
            double step = target / magnitude >= 5 ? 5 : target / magnitude >= 2 ? 2 : 1;
            double meters = step * magnitude;
            float length = (float)(meters / metersPerPixel);

            Vector3 a = bottomLeft;
            Vector3 b = bottomLeft + new Vector2(length, 0);
            Handles.color = Color.white;
            Handles.DrawAAPolyLine(2, a, b);
            Handles.DrawAAPolyLine(2, a, a + Vector3.down * 6);
            Handles.DrawAAPolyLine(2, b, b + Vector3.down * 6);
            string label = meters >= 1000 ? $"{meters / 1000:0.##} km" : $"{meters:0.##} m";
            GUI.Label(new Rect(a.x, a.y - 20, 120, 16), label, EditorStyles.whiteMiniLabel);
        }
    }
}
