using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralGeneration
{
    [Serializable]
    public struct TileLodSettings
    {
        [Tooltip("Coarsest zoom level, used for the far distance.")]
        [Range(0, WebMercator.MaxZoom)]
        public int minZoom;

        [Tooltip("Finest zoom level, used inside the radius.")]
        [Range(0, WebMercator.MaxZoom)]
        public int maxZoom;

        [Tooltip("Ground metres around the focus (point or path) where every tile has max zoom.")]
        [Min(0)]
        public float radius;

        [Tooltip("Flight altitude in metres. Distances are measured from the eye, and it sets the horizon distance.")]
        [Min(0)]
        public float altitude;

        [Tooltip("Higher keeps fine tiles further out. 1 = zoom drops by one each time the eye distance doubles past the radius.")]
        [Min(0.01f)]
        public float detailBias;

        [Tooltip("Ground metres from the focus beyond which no tiles are generated. 0 = horizon distance at the altitude.")]
        [Min(0)]
        public float maxViewDistance;

        [Tooltip("Split tiles until neighbours differ by at most one zoom level, so terrain chunks can be stitched.")]
        public bool balance;

        public static TileLodSettings Default => new TileLodSettings
        {
            minZoom = 13,
            maxZoom = 15,
            radius = 100,
            altitude = 300,
            detailBias = 1,
            maxViewDistance = 0,
            balance = true,
        };

        public void Validate()
        {
            minZoom = Mathf.Clamp(minZoom, 0, WebMercator.MaxZoom);
            maxZoom = Mathf.Clamp(maxZoom, minZoom, WebMercator.MaxZoom);
            radius = Mathf.Max(0, radius);
            altitude = Mathf.Max(0, altitude);
            detailBias = Mathf.Max(0.01f, detailBias);
            maxViewDistance = Mathf.Max(0, maxViewDistance);
        }

        /// <summary>
        /// Zoom a tile needs when its nearest point is <paramref name="groundDistance"/> metres from the focus.
        /// Screen space error heuristic: a tile's projected size is its edge length over the eye distance,
        /// the tile is fine enough while that stays below what a max zoom tile has at the radius.
        /// Each halving of the allowed error costs one zoom level, so zoom drops by one per doubling of eye distance.
        /// </summary>
        public int RequiredZoom(double groundDistance)
        {
            if (groundDistance <= radius)
                return maxZoom;
            double eyeDistance = EyeDistance(groundDistance);
            double referenceDistance = Math.Max(EyeDistance(radius), 1e-3) * detailBias;
            int drop = (int)Math.Floor(Math.Log(eyeDistance / referenceDistance, 2));
            return Mathf.Clamp(maxZoom - Math.Max(drop, 0), minZoom, maxZoom);
        }

        private double EyeDistance(double groundDistance) => Math.Sqrt(groundDistance * groundDistance + (double)altitude * altitude);

        /// <summary>Ground metres from the focus that get tiles.</summary>
        public double ViewDistance()
        {
            if (maxViewDistance > 0)
                return Math.Max(maxViewDistance, radius);
            // Distance to the horizon over a sphere, from at least a standing observer's eye height.
            double h = Math.Max(altitude, MinEyeHeight);
            return Math.Max(Math.Sqrt(2 * WebMercator.EarthRadius * h + h * h), radius);
        }

        private const double MinEyeHeight = 2;
    }

    /// <summary>
    /// What the LOD is measured from (the camera position or the flight path), in mercator units.
    /// </summary>
    public interface ILodFocus
    {
        bool isValid { get; }
        void GetBounds(out DoubleVector2 min, out DoubleVector2 max);
        /// <summary>Mercator distance from the focus to the nearest point of the rectangle, 0 when they overlap.</summary>
        double DistanceToRect(DoubleVector2 min, DoubleVector2 max);
    }

    public sealed class PointLodFocus : ILodFocus
    {
        private readonly DoubleVector2 _point;

        public PointLodFocus(DoubleVector2 point) => _point = point;

        public bool isValid => true;

        public void GetBounds(out DoubleVector2 min, out DoubleVector2 max)
        {
            min = _point;
            max = _point;
        }

        public double DistanceToRect(DoubleVector2 min, DoubleVector2 max) => LodGeometry.PointRectDistance(_point, min, max);
    }

    public sealed class PathLodFocus : ILodFocus
    {
        private readonly List<DoubleVector2> _nodes;

        public PathLodFocus(IEnumerable<DoubleVector2> nodes) => _nodes = new List<DoubleVector2>(nodes);

        public bool isValid => _nodes.Count > 0;

        public void GetBounds(out DoubleVector2 min, out DoubleVector2 max)
        {
            min = new DoubleVector2(double.MaxValue, double.MaxValue);
            max = new DoubleVector2(double.MinValue, double.MinValue);
            foreach (var node in _nodes)
            {
                min = new DoubleVector2(Math.Min(min.x, node.x), Math.Min(min.y, node.y));
                max = new DoubleVector2(Math.Max(max.x, node.x), Math.Max(max.y, node.y));
            }
        }

        public double DistanceToRect(DoubleVector2 min, DoubleVector2 max)
        {
            if (_nodes.Count == 1)
                return LodGeometry.PointRectDistance(_nodes[0], min, max);
            double best = double.MaxValue;
            for (int i = 0; i < _nodes.Count - 1; i++)
            {
                best = Math.Min(best, LodGeometry.SegmentRectDistance(_nodes[i], _nodes[i + 1], min, max));
                if (best <= 0)
                    return 0;
            }
            return best;
        }
    }

    public static class LodGeometry
    {
        public static double PointRectDistance(DoubleVector2 p, DoubleVector2 min, DoubleVector2 max)
        {
            double dx = Math.Max(Math.Max(min.x - p.x, 0), p.x - max.x);
            double dy = Math.Max(Math.Max(min.y - p.y, 0), p.y - max.y);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static double PointSegmentDistance(DoubleVector2 p, DoubleVector2 a, DoubleVector2 b)
        {
            double abx = b.x - a.x, aby = b.y - a.y;
            double lengthSq = abx * abx + aby * aby;
            double t = lengthSq > 0 ? ((p.x - a.x) * abx + (p.y - a.y) * aby) / lengthSq : 0;
            t = Math.Clamp(t, 0, 1);
            double dx = a.x + abx * t - p.x, dy = a.y + aby * t - p.y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// For a segment that misses a convex rectangle the closest pair always has an endpoint or a corner in it.
        /// </summary>
        public static double SegmentRectDistance(DoubleVector2 a, DoubleVector2 b, DoubleVector2 min, DoubleVector2 max)
        {
            if (SegmentIntersectsRect(a, b, min, max))
                return 0;
            double d = Math.Min(PointRectDistance(a, min, max), PointRectDistance(b, min, max));
            d = Math.Min(d, PointSegmentDistance(min, a, b));
            d = Math.Min(d, PointSegmentDistance(max, a, b));
            d = Math.Min(d, PointSegmentDistance(new DoubleVector2(min.x, max.y), a, b));
            d = Math.Min(d, PointSegmentDistance(new DoubleVector2(max.x, min.y), a, b));
            return d;
        }

        /// <summary>Liang-Barsky clip, true when any part of the segment is inside the rectangle.</summary>
        public static bool SegmentIntersectsRect(DoubleVector2 a, DoubleVector2 b, DoubleVector2 min, DoubleVector2 max)
        {
            double dx = b.x - a.x, dy = b.y - a.y;
            double t0 = 0, t1 = 1;
            return Clip(-dx, a.x - min.x, ref t0, ref t1)
                && Clip(dx, max.x - a.x, ref t0, ref t1)
                && Clip(-dy, a.y - min.y, ref t0, ref t1)
                && Clip(dy, max.y - a.y, ref t0, ref t1);
        }

        private static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (p == 0)
                return q >= 0;
            double r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }
    }

    /// <summary>
    /// Builds a quadtree of Web Mercator tiles refined towards a focus, like Google Earth chunk LOD:
    /// start from min zoom tiles inside the view distance, split a tile while its nearest point is closer
    /// than its zoom allows (<see cref="TileLodSettings.RequiredZoom"/>), cull tiles past the view distance.
    /// </summary>
    public static class TileLodBuilder
    {
        public const int MaxTiles = 100000;

        public struct Result
        {
            public List<TileBounds> tiles;
            /// <summary>Generation stopped at <see cref="MaxTiles"/>.</summary>
            public bool truncated;
            /// <summary>Ground metres from the focus that got tiles.</summary>
            public double viewDistance;
        }

        public static Result Build(TileLodSettings settings, ILodFocus focus)
        {
            settings.Validate();
            var result = new Result { tiles = new List<TileBounds>(), viewDistance = settings.ViewDistance() };
            if (focus == null || !focus.isValid)
                return result;

            focus.GetBounds(out DoubleVector2 focusMin, out DoubleVector2 focusMax);
            // Mercator distances are stretched by 1 / cos(latitude), the focus is small enough for one factor.
            double mercatorPerMeter = WebMercator.MercatorPerMeter((focusMin.y + focusMax.y) / 2);
            double extent = result.viewDistance * mercatorPerMeter;

            var rootMin = TileBounds.FromMercator(new DoubleVector2(focusMin.x - extent, focusMax.y + extent), settings.minZoom);
            var rootMax = TileBounds.FromMercator(new DoubleVector2(focusMax.x + extent, focusMin.y - extent), settings.minZoom);

            var stack = new Stack<TileBounds>();
            for (int y = rootMin.y; y <= rootMax.y; y++)
                for (int x = rootMin.x; x <= rootMax.x; x++)
                    stack.Push(new TileBounds(x, y, settings.minZoom));

            while (stack.Count > 0)
            {
                if (result.tiles.Count >= MaxTiles)
                {
                    result.truncated = true;
                    break;
                }
                var tile = stack.Pop();
                double distance = focus.DistanceToRect(tile.min, tile.max) / mercatorPerMeter;
                if (distance > result.viewDistance)
                    continue;
                if (tile.zoom < settings.RequiredZoom(distance))
                {
                    for (int i = 0; i < 4; i++)
                        stack.Push(tile.GetChild(i));
                }
                else
                {
                    result.tiles.Add(tile);
                }
            }

            if (settings.balance && !result.truncated)
                result.truncated = !Balance(result.tiles, settings.minZoom);

            result.tiles.Sort();
            return result;
        }

        /// <summary>
        /// Restricted quadtree: splits leaves until edge neighbours differ by at most one zoom level.
        /// Returns false when <see cref="MaxTiles"/> was hit.
        /// </summary>
        private static bool Balance(List<TileBounds> tiles, int minZoom)
        {
            var leaves = new HashSet<TileBounds>(tiles);
            var queue = new Queue<TileBounds>(tiles);
            int[] offsetsX = { 1, -1, 0, 0 };
            int[] offsetsY = { 0, 0, 1, -1 };
            bool complete = true;

            while (queue.Count > 0)
            {
                var tile = queue.Dequeue();
                if (!leaves.Contains(tile))
                    continue;
                for (int i = 0; i < 4; i++)
                {
                    var neighbour = new TileBounds(tile.x + offsetsX[i], tile.y + offsetsY[i], tile.zoom);
                    if (!neighbour.isValid)
                        continue;
                    // The leaf covering the neighbour area, finer neighbours check against this tile themselves.
                    var cover = neighbour;
                    while (cover.zoom >= minZoom && !leaves.Contains(cover))
                        cover = cover.parent;
                    if (cover.zoom < minZoom || cover.zoom >= tile.zoom - 1)
                        continue;
                    if (leaves.Count + 3 > MaxTiles)
                    {
                        complete = false;
                        queue.Clear();
                        break;
                    }
                    leaves.Remove(cover);
                    for (int c = 0; c < 4; c++)
                    {
                        var child = cover.GetChild(c);
                        leaves.Add(child);
                        queue.Enqueue(child);
                    }
                    // The new cover can still be too coarse.
                    queue.Enqueue(tile);
                }
            }

            tiles.Clear();
            tiles.AddRange(leaves);
            return complete;
        }
    }
}
