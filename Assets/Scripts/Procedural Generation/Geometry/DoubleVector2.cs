using System;
using UnityEngine;

namespace ProceduralGeneration
{
    /// <summary>
    /// Double precision 2D vector, used for mercator coordinates
    /// (~4e6 m) where float precision is only ~0.25 m.
    /// </summary>
    [Serializable]
    public struct DoubleVector2
    {
        public double x;
        public double y;

        public DoubleVector2(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        public static DoubleVector2 zero => new DoubleVector2(0, 0);

        public static DoubleVector2 operator +(DoubleVector2 a, DoubleVector2 b) => new DoubleVector2(a.x + b.x, a.y + b.y);
        public static DoubleVector2 operator -(DoubleVector2 a, DoubleVector2 b) => new DoubleVector2(a.x - b.x, a.y - b.y);
        public static DoubleVector2 operator -(DoubleVector2 a) => new DoubleVector2(-a.x, -a.y);
        public static DoubleVector2 operator *(DoubleVector2 a, double s) => new DoubleVector2(a.x * s, a.y * s);
        public static DoubleVector2 operator *(double s, DoubleVector2 a) => new DoubleVector2(a.x * s, a.y * s);
        public static DoubleVector2 operator /(DoubleVector2 a, double s) => new DoubleVector2(a.x / s, a.y / s);
        public static bool ApproximatelyEquals(DoubleVector2 a, DoubleVector2 b, double epsilon = 1e-6)
        {
            return Math.Abs(a.x - b.x) < epsilon && Math.Abs(a.y - b.y) < epsilon;
        }

        // Only convert to float once coordinates are small (relative to an origin).
        public Vector2 ToVector2() => new Vector2((float)x, (float)y);

        public override string ToString() => $"({x:F3}, {y:F3})";
    }
}
