using System.Collections.Generic;
using UnityEngine;

namespace ProceduralGeneration
{

    /// <summary>
    /// A simple Catmull-Rom spline through a list of world-space control points.
    /// Supports normalized sampling (0-1 over the whole spline), closest-point/
    /// distance queries against an arbitrary world position, and gizmo drawing.
    /// Works fully standalone (no dependency on the com.unity.splines package),
    /// tested against Unity 6.6 / URP & built-in render pipelines.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class Spline : MonoBehaviour
    {
        [Header("Control Points (local space)")]
        [SerializeField] private List<Vector3> points = new List<Vector3>
        {
            new Vector3(0, 0, 0),
            new Vector3(2, 0, 2),
            new Vector3(4, 0, 0),
            new Vector3(6, 0, 2),
        };

        [Header("Shape")]
        [SerializeField] private bool closed = false;
        [Tooltip("How taut the curve is. 0 = very loose, 1 = default Catmull-Rom.")]
        [Range(0f, 1f)]
        [SerializeField] private float tension = 0.5f;

        [Header("Gizmos")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private Color lineColor = Color.cyan;
        [SerializeField] private Color pointColor = Color.yellow;
        [SerializeField] private int gizmoResolutionPerSegment = 20;
        [SerializeField] private float pointGizmoRadius = 0.08f;

        // Cached arc-length lookup table: table[i] = (t, cumulativeDistance)
        private struct ArcEntry { public float t; public float dist; }
        private List<ArcEntry> arcTable;
        private float cachedLength = -1f;
        private const int ARC_TABLE_RESOLUTION = 200;

        public IReadOnlyList<Vector3> Points => points;
        public bool Closed => closed;
        public int SegmentCount => closed ? points.Count : Mathf.Max(0, points.Count - 1);

        private void OnValidate()
        {
            InvalidateCache();
        }

        public void InvalidateCache()
        {
            arcTable = null;
            cachedLength = -1f;
        }

        public void AddPoint(Vector3 localPoint)
        {
            points.Add(localPoint);
            InvalidateCache();
        }

        public void SetPoint(int index, Vector3 localPoint)
        {
            if (index < 0 || index >= points.Count) return;
            points[index] = localPoint;
            InvalidateCache();
        }

        // ---------------------------------------------------------------
        // Sampling
        // ---------------------------------------------------------------

        /// <summary>
        /// Samples a world-space position at normalized parameter t in [0,1]
        /// spread evenly across all segments (NOT arc-length corrected).
        /// </summary>
        public Vector3 Sample(float t)
        {
            Vector3 local = SampleLocal(t);
            return transform.TransformPoint(local);
        }

        /// <summary>
        /// Samples a world-space position at normalized parameter t in [0,1],
        /// corrected so that t maps linearly to arc-length (constant speed).
        /// </summary>
        public Vector3 SampleUniform(float t)
        {
            float rawT = ArcLengthToT(Mathf.Clamp01(t) * GetLength());
            return Sample(rawT);
        }

        public Vector3 SampleLocal(float t)
        {
            int segments = SegmentCount;
            if (points.Count == 0) return transform.position;
            if (points.Count == 1 || segments == 0) return points[0];

            t = Mathf.Clamp01(t);
            float scaledT = t * segments;
            int segIndex = Mathf.Clamp(Mathf.FloorToInt(scaledT), 0, segments - 1);
            float localT = scaledT - segIndex;

            Vector3 p0 = GetPointSafe(segIndex - 1);
            Vector3 p1 = GetPointSafe(segIndex);
            Vector3 p2 = GetPointSafe(segIndex + 1);
            Vector3 p3 = GetPointSafe(segIndex + 2);

            return CatmullRom(p0, p1, p2, p3, localT, tension);
        }

        /// <summary>Tangent (unnormalized) at normalized parameter t, in world space.</summary>
        public Vector3 SampleTangent(float t)
        {
            const float eps = 0.0005f;
            float t0 = Mathf.Clamp01(t - eps);
            float t1 = Mathf.Clamp01(t + eps);
            Vector3 a = Sample(t0);
            Vector3 b = Sample(t1);
            Vector3 dir = b - a;
            return dir.sqrMagnitude > 0f ? dir.normalized : Vector3.forward;
        }

        private Vector3 GetPointSafe(int index)
        {
            int count = points.Count;
            if (closed)
            {
                index = ((index % count) + count) % count;
                return points[index];
            }
            index = Mathf.Clamp(index, 0, count - 1);
            return points[index];
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t, float tension)
        {
            // Standard Catmull-Rom basis, with a tension scalar applied to the tangents.
            float alpha = Mathf.Lerp(2f, 1f, tension); // 1 = classic Catmull-Rom, 2 = looser
            float t2 = t * t;
            float t3 = t2 * t;

            Vector3 m1 = (p2 - p0) * 0.5f * tension * 2f / alpha;
            Vector3 m2 = (p3 - p1) * 0.5f * tension * 2f / alpha;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            return h00 * p1 + h10 * m1 + h01 * p2 + h11 * m2;
        }

        // ---------------------------------------------------------------
        // Arc length / uniform parameterization
        // ---------------------------------------------------------------

        public float GetLength()
        {
            BuildArcTableIfNeeded();
            return cachedLength;
        }

        private void BuildArcTableIfNeeded()
        {
            if (arcTable != null) return;

            arcTable = new List<ArcEntry>(ARC_TABLE_RESOLUTION + 1);
            Vector3 prev = SampleLocal(0f);
            float acc = 0f;
            arcTable.Add(new ArcEntry { t = 0f, dist = 0f });

            for (int i = 1; i <= ARC_TABLE_RESOLUTION; i++)
            {
                float t = i / (float)ARC_TABLE_RESOLUTION;
                Vector3 p = SampleLocal(t);
                acc += Vector3.Distance(prev, p) * transform.lossyScale.x; // approx uniform scale
                arcTable.Add(new ArcEntry { t = t, dist = acc });
                prev = p;
            }
            cachedLength = acc;
        }

        private float ArcLengthToT(float targetDist)
        {
            BuildArcTableIfNeeded();
            if (arcTable.Count == 0 || cachedLength <= 0f) return 0f;
            targetDist = Mathf.Clamp(targetDist, 0f, cachedLength);

            // Binary search the arc table for the bracketing entries.
            int lo = 0, hi = arcTable.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (arcTable[mid].dist < targetDist) lo = mid + 1;
                else hi = mid;
            }
            if (lo == 0) return arcTable[0].t;

            ArcEntry a = arcTable[lo - 1];
            ArcEntry b = arcTable[lo];
            float span = b.dist - a.dist;
            float frac = span > 0f ? (targetDist - a.dist) / span : 0f;
            return Mathf.Lerp(a.t, b.t, frac);
        }

        // ---------------------------------------------------------------
        // Closest point / distance queries
        // ---------------------------------------------------------------

        /// <summary>
        /// Finds the shortest distance from worldPos to the spline.
        /// Coarse sample pass followed by a local refinement (ternary-ish search)
        /// for sub-segment accuracy.
        /// </summary>
        public float GetDistanceTo(Vector3 worldPos, int coarseSamples = 64)
        {
            return GetDistanceTo(worldPos, out _, out _, coarseSamples);
        }

        /// <summary>
        /// Same as GetDistanceTo but also outputs the closest world-space point
        /// and the normalized t at which it occurs.
        /// </summary>
        public float GetDistanceTo(Vector3 worldPos, out Vector3 closestPoint, out float closestT, int coarseSamples = 64)
        {
            if (points.Count == 0)
            {
                closestPoint = transform.position;
                closestT = 0f;
                return Vector3.Distance(worldPos, closestPoint);
            }
            if (points.Count == 1)
            {
                closestPoint = transform.TransformPoint(points[0]);
                closestT = 0f;
                return Vector3.Distance(worldPos, closestPoint);
            }

            coarseSamples = Mathf.Max(4, coarseSamples);

            // Coarse pass: find the best bracketing interval.
            float bestT = 0f;
            float bestSqr = float.MaxValue;
            Vector3 prevSample = Sample(0f);
            float prevT = 0f;

            for (int i = 1; i <= coarseSamples; i++)
            {
                float t = i / (float)coarseSamples;
                Vector3 s = Sample(t);
                float sqr = (s - worldPos).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    bestT = t;
                }
                prevSample = s;
                prevT = t;
            }

            // Refine within [bestT - step, bestT + step] using golden-section-ish
            // ternary search on the squared distance function (unimodal locally).
            float step = 1f / coarseSamples;
            float lo = Mathf.Clamp01(bestT - step);
            float hi = Mathf.Clamp01(bestT + step);

            for (int iter = 0; iter < 24; iter++)
            {
                float m1 = lo + (hi - lo) / 3f;
                float m2 = hi - (hi - lo) / 3f;
                float d1 = (Sample(m1) - worldPos).sqrMagnitude;
                float d2 = (Sample(m2) - worldPos).sqrMagnitude;
                if (d1 < d2) hi = m2;
                else lo = m1;
            }

            closestT = (lo + hi) * 0.5f;
            closestPoint = Sample(closestT);
            return Vector3.Distance(worldPos, closestPoint);
        }

        // ---------------------------------------------------------------
        // Gizmos
        // ---------------------------------------------------------------

        private void OnDrawGizmos()
        {
            if (!drawGizmos || points.Count == 0) return;

            int segments = SegmentCount;
            if (segments > 0)
            {
                Gizmos.color = lineColor;
                int totalSteps = segments * Mathf.Max(2, gizmoResolutionPerSegment);
                Vector3 prev = Sample(0f);
                for (int i = 1; i <= totalSteps; i++)
                {
                    float t = i / (float)totalSteps;
                    Vector3 cur = Sample(t);
                    Gizmos.DrawLine(prev, cur);
                    prev = cur;
                }
            }

            Gizmos.color = pointColor;
            foreach (var p in points)
            {
                Gizmos.DrawSphere(transform.TransformPoint(p), pointGizmoRadius);
            }
        }

    #if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            // Highlight control point indices in the scene view for easier editing.
            for (int i = 0; i < points.Count; i++)
            {
                UnityEditor.Handles.Label(transform.TransformPoint(points[i]) + Vector3.up * 0.15f, i.ToString());
            }
        }
    #endif
    }
}