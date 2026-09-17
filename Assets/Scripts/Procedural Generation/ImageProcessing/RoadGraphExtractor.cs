using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProceduralGeneration.ImageProcessing
{
    /// <summary>
    /// A single node in the road network (an endpoint, junction, or synthetic
    /// loop marker). Position is in source-image pixel space unless you
    /// rescale it after extraction.
    /// </summary>
    [Serializable]
    public class RoadNode
    {
        public int Id;
        public Vector2 Position;
        public List<int> EdgeIds = new List<int>();
    }

    /// <summary>
    /// A road segment between two nodes. Points is the simplified, smoothed
    /// polyline (Douglas-Peucker reduced, then Chaikin corner-cut), always
    /// starting at NodeA's position and ending at NodeB's position.
    /// </summary>
    [Serializable]
    public class RoadEdge
    {
        public int Id;
        public int NodeA;
        public int NodeB;
        public List<Vector2> Points = new List<Vector2>();
        public float Length;
    }

    public class RoadGraph
    {
        public List<RoadNode> Nodes = new List<RoadNode>();
        public List<RoadEdge> Edges = new List<RoadEdge>();
    }

    public static class RoadGraphExtractor
    {
        /// <summary>
        /// Full pipeline: load a PNG, mask by road color, skeletonize,
        /// and extract a vector graph.
        /// </summary>
        /// <param name="filePath">Absolute path to the segmented PNG.</param>
        /// <param name="roadColor">The color used for roads in the segmentation map.</param>
        /// <param name="colorTolerance">0-1 Euclidean RGB distance tolerance for matching roadColor.</param>
        /// <param name="simplifyEpsilon">Douglas-Peucker epsilon in pixels. Higher = fewer points per edge.</param>
        /// <param name="pixelToWorldScale">Optional uniform scale applied to all output positions.</param>
        /// <param name="minSpurLength">Dangling dead-end edges shorter than this (pixels) are pruned as skeletonization noise. 0 disables.</param>
        /// <param name="junctionMergeRadius">Junction nodes closer than this (pixels) are merged into one, collapsing dense intersection clusters. 0 disables.</param>
        /// <param name="smoothingIterations">Number of Chaikin corner-cutting passes applied to each simplified edge. 0 disables smoothing.</param>
        /// <param name="smoothingStrength">Chaikin cut ratio per iteration, in (0, 0.5). Higher = more rounded corners.</param>
        public static RoadGraph ExtractFromPng(
            string filePath,
            Color roadColor,
            float colorTolerance = 0.15f,
            float simplifyEpsilon = 1.5f,
            float pixelToWorldScale = 1f,
            float minSpurLength = 0f,
            float junctionMergeRadius = 0f,
            int smoothingIterations = 0,
            float smoothingStrength = 0.25f)
        {
            Texture2D tex = LoadTexture(filePath);
            try
            {
                bool[,] mask = BuildRoadMask(tex, roadColor, colorTolerance);
                bool[,] skeleton = ZhangSuenThin(mask);
                RoadGraph graph = BuildGraph(skeleton);

                PruneShortSpurs(graph, minSpurLength);
                MergeCloseNodes(graph, junctionMergeRadius);
                PruneShortSpurs(graph, minSpurLength); // merging can expose new tiny dangling connectors

                foreach (var edge in graph.Edges)
                {
                    edge.Points = DouglasPeucker(edge.Points, simplifyEpsilon);
                    edge.Points = SmoothPolyline(edge.Points, smoothingIterations, smoothingStrength);
                }

                if (!Mathf.Approximately(pixelToWorldScale, 1f))
                {
                    foreach (var n in graph.Nodes)
                        n.Position *= pixelToWorldScale;
                    foreach (var e in graph.Edges)
                        for (int i = 0; i < e.Points.Count; i++)
                            e.Points[i] *= pixelToWorldScale;
                }

                return graph;
            }
            finally
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(tex);
                else
                    UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        // ---------------------------------------------------------------
        // 1. Loading
        // ---------------------------------------------------------------

        public static Texture2D LoadTexture(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"PNG not found at: {filePath}");

            byte[] bytes = File.ReadAllBytes(filePath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes))
                throw new Exception($"Failed to decode PNG at: {filePath}");

            return tex;
        }

        // ---------------------------------------------------------------
        // 2. Masking
        // ---------------------------------------------------------------

        public static bool[,] BuildRoadMask(Texture2D tex, Color roadColor, float tolerance)
        {
            int w = tex.width, h = tex.height;
            Color[] pixels = tex.GetPixels();
            bool[,] mask = new bool[w, h];
            float tolSqr = tolerance * tolerance;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = pixels[y * w + x];
                    float dr = c.r - roadColor.r;
                    float dg = c.g - roadColor.g;
                    float db = c.b - roadColor.b;
                    mask[x, y] = (dr * dr + dg * dg + db * db) <= tolSqr;
                }
            }
            return mask;
        }

        // ---------------------------------------------------------------
        // 3. Skeletonization (Zhang-Suen thinning)
        // ---------------------------------------------------------------

        public static bool[,] ZhangSuenThin(bool[,] input)
        {
            int w = input.GetLength(0);
            int h = input.GetLength(1);
            bool[,] img = (bool[,])input.Clone();
            bool changed = true;

            while (changed)
            {
                changed = false;
                changed |= ZhangSuenStep(img, w, h, 0);
                changed |= ZhangSuenStep(img, w, h, 1);
            }
            return img;
        }

        private static bool ZhangSuenStep(bool[,] img, int w, int h, int step)
        {
            var toRemove = new List<Vector2Int>();

            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    if (!img[x, y]) continue;

                    bool p2 = img[x, y - 1];
                    bool p3 = img[x + 1, y - 1];
                    bool p4 = img[x + 1, y];
                    bool p5 = img[x + 1, y + 1];
                    bool p6 = img[x, y + 1];
                    bool p7 = img[x - 1, y + 1];
                    bool p8 = img[x - 1, y];
                    bool p9 = img[x - 1, y - 1];

                    int B = CountTrue(p2, p3, p4, p5, p6, p7, p8, p9);
                    if (B < 2 || B > 6) continue;

                    int A = CountTransitions(p2, p3, p4, p5, p6, p7, p8, p9);
                    if (A != 1) continue;

                    if (step == 0)
                    {
                        if (p2 && p4 && p6) continue;
                        if (p4 && p6 && p8) continue;
                    }
                    else
                    {
                        if (p2 && p4 && p8) continue;
                        if (p2 && p6 && p8) continue;
                    }

                    toRemove.Add(new Vector2Int(x, y));
                }
            }

            foreach (var p in toRemove) img[p.x, p.y] = false;
            return toRemove.Count > 0;
        }

        private static int CountTrue(params bool[] vals)
        {
            int c = 0;
            foreach (var v in vals) if (v) c++;
            return c;
        }

        // Counts 0->1 transitions in the circular sequence p2..p9,p2
        private static int CountTransitions(bool p2, bool p3, bool p4, bool p5, bool p6, bool p7, bool p8, bool p9)
        {
            bool[] seq = { p2, p3, p4, p5, p6, p7, p8, p9, p2 };
            int transitions = 0;
            for (int i = 0; i < 8; i++)
                if (!seq[i] && seq[i + 1]) transitions++;
            return transitions;
        }

        // ---------------------------------------------------------------
        // 4. Graph extraction
        // ---------------------------------------------------------------

        private static readonly int[] Dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] Dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

        public static RoadGraph BuildGraph(bool[,] skeleton)
        {
            int w = skeleton.GetLength(0);
            int h = skeleton.GetLength(1);

            int NeighborCount(int x, int y)
            {
                int c = 0;
                for (int i = 0; i < 8; i++)
                {
                    int nx = x + Dx[i], ny = y + Dy[i];
                    if (nx >= 0 && nx < w && ny >= 0 && ny < h && skeleton[nx, ny]) c++;
                }
                return c;
            }

            // Nodes = endpoints (degree 1) and junctions (degree >= 3).
            var nodePixels = new HashSet<Vector2Int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (skeleton[x, y] && NeighborCount(x, y) != 2)
                        nodePixels.Add(new Vector2Int(x, y));

            var graph = new RoadGraph();
            var pixelToNodeId = new Dictionary<Vector2Int, int>();

            void AddNode(Vector2Int p)
            {
                if (pixelToNodeId.ContainsKey(p)) return;
                var node = new RoadNode { Id = graph.Nodes.Count, Position = new Vector2(p.x, p.y) };
                pixelToNodeId[p] = node.Id;
                graph.Nodes.Add(node);
                nodePixels.Add(p);
            }

            foreach (var p in nodePixels) AddNode(p);

            bool[,] consumed = new bool[w, h];
            var tracedDirections = new HashSet<(Vector2Int, Vector2Int)>();

            List<Vector2> TraceEdge(Vector2Int start, Vector2Int firstStep)
            {
                var path = new List<Vector2> { new Vector2(start.x, start.y) };
                Vector2Int prev = start, cur = firstStep;

                while (true)
                {
                    path.Add(new Vector2(cur.x, cur.y));

                    if (nodePixels.Contains(cur) && cur != start)
                        break;

                    consumed[cur.x, cur.y] = true;

                    Vector2Int next = new Vector2Int(-1, -1);
                    for (int i = 0; i < 8; i++)
                    {
                        var cand = new Vector2Int(cur.x + Dx[i], cur.y + Dy[i]);
                        if (cand.x < 0 || cand.x >= w || cand.y < 0 || cand.y >= h) continue;
                        if (!skeleton[cand.x, cand.y]) continue;
                        if (cand == prev) continue;
                        next = cand;
                        break;
                    }

                    if (next.x == -1) break; // dead end
                    prev = cur;
                    cur = next;
                }

                return path;
            }

            void ProcessEdgesFrom(Vector2Int start)
            {
                for (int i = 0; i < 8; i++)
                {
                    var firstStep = new Vector2Int(start.x + Dx[i], start.y + Dy[i]);
                    if (firstStep.x < 0 || firstStep.x >= w || firstStep.y < 0 || firstStep.y >= h) continue;
                    if (!skeleton[firstStep.x, firstStep.y]) continue;
                    if (tracedDirections.Contains((start, firstStep))) continue;

                    List<Vector2> path = TraceEdge(start, firstStep);
                    var end = new Vector2Int((int)path[^1].x, (int)path[^1].y);

                    tracedDirections.Add((start, firstStep));
                    if (path.Count >= 2)
                    {
                        var beforeEnd = new Vector2Int((int)path[^2].x, (int)path[^2].y);
                        tracedDirections.Add((end, beforeEnd));
                    }

                    if (end == start && path.Count <= 2) continue; // trivial non-edge

                    AddNode(end);

                    var edge = new RoadEdge
                    {
                        Id = graph.Edges.Count,
                        NodeA = pixelToNodeId[start],
                        NodeB = pixelToNodeId[end],
                        Points = path,
                        Length = PathLength(path)
                    };
                    graph.Edges.Add(edge);
                    graph.Nodes[edge.NodeA].EdgeIds.Add(edge.Id);
                    graph.Nodes[edge.NodeB].EdgeIds.Add(edge.Id);
                }
            }

            // Trace edges out from every junction/endpoint.
            foreach (var p in new List<Vector2Int>(nodePixels))
                ProcessEdgesFrom(p);

            // Handle closed loops (e.g. roundabouts) that have no junction,
            // so every skeleton pixel on them has degree 2 and never got visited above.
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!skeleton[x, y] || consumed[x, y]) continue;
                    var p = new Vector2Int(x, y);
                    if (nodePixels.Contains(p)) continue;

                    AddNode(p); // synthetic junction to break the loop open
                    ProcessEdgesFrom(p);
                }
            }

            return graph;
        }

        private static float PathLength(List<Vector2> path)
        {
            float len = 0f;
            for (int i = 1; i < path.Count; i++)
                len += Vector2.Distance(path[i - 1], path[i]);
            return len;
        }

        // ---------------------------------------------------------------
        // 4b. Cleanup: prune noise spurs, merge clustered junctions
        // ---------------------------------------------------------------

        /// <summary>
        /// Repeatedly removes dangling dead-end edges shorter than minLength.
        /// Zhang-Suen thinning tends to leave short spurious branches off real
        /// roads; without this the network ends up looking overly dense.
        /// </summary>
        public static void PruneShortSpurs(RoadGraph graph, float minLength)
        {
            if (minLength <= 0f) return;

            while (true)
            {
                var degree = new int[graph.Nodes.Count];
                foreach (var edge in graph.Edges)
                {
                    degree[edge.NodeA]++;
                    degree[edge.NodeB]++;
                }

                bool removedAny = false;
                for (int i = graph.Edges.Count - 1; i >= 0; i--)
                {
                    var edge = graph.Edges[i];
                    if (edge.Length >= minLength) continue;

                    bool aDangling = degree[edge.NodeA] == 1;
                    bool bDangling = degree[edge.NodeB] == 1;
                    if (!aDangling && !bDangling) continue;
                    if (aDangling && bDangling && graph.Edges.Count == 1) continue; // don't erase the whole graph

                    graph.Edges.RemoveAt(i);
                    removedAny = true;
                }

                if (!removedAny) break;
                CompactGraph(graph);
            }
        }

        /// <summary>
        /// Merges nodes that lie within mergeRadius of each other into a single
        /// node at their centroid. Skeletonized intersections often produce a
        /// tight cluster of junction pixels instead of one clean crossing; this
        /// collapses those clusters and drops the now-degenerate connector edges.
        /// </summary>
        public static void MergeCloseNodes(RoadGraph graph, float mergeRadius)
        {
            if (mergeRadius <= 0f) return;

            int n = graph.Nodes.Count;
            if (n == 0) return;

            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }

            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a != b) parent[b] = a;
            }

            float radiusSqr = mergeRadius * mergeRadius;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if ((graph.Nodes[i].Position - graph.Nodes[j].Position).sqrMagnitude <= radiusSqr)
                        Union(i, j);

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!groups.TryGetValue(r, out var list)) groups[r] = list = new List<int>();
                list.Add(i);
            }

            if (groups.Count == n) return; // nothing to merge

            var oldToNew = new int[n];
            var newNodes = new List<RoadNode>();
            foreach (var ids in groups.Values)
            {
                Vector2 centroid = Vector2.zero;
                foreach (var id in ids) centroid += graph.Nodes[id].Position;
                centroid /= ids.Count;

                var mergedNode = new RoadNode { Id = newNodes.Count, Position = centroid };
                foreach (var id in ids) oldToNew[id] = mergedNode.Id;
                newNodes.Add(mergedNode);
            }

            var newEdges = new List<RoadEdge>();
            foreach (var edge in graph.Edges)
            {
                int a = oldToNew[edge.NodeA];
                int b = oldToNew[edge.NodeB];
                if (a == b) continue; // collapsed into a single node, drop the tiny connector

                if (edge.Points.Count > 0)
                {
                    edge.Points[0] = newNodes[a].Position;
                    edge.Points[edge.Points.Count - 1] = newNodes[b].Position;
                }

                edge.NodeA = a;
                edge.NodeB = b;
                edge.Id = newEdges.Count;
                newEdges.Add(edge);
            }

            foreach (var node in newNodes)
            {
                node.EdgeIds.Clear();
            }
            foreach (var edge in newEdges)
            {
                newNodes[edge.NodeA].EdgeIds.Add(edge.Id);
                newNodes[edge.NodeB].EdgeIds.Add(edge.Id);
            }

            graph.Nodes.Clear();
            graph.Nodes.AddRange(newNodes);
            graph.Edges.Clear();
            graph.Edges.AddRange(newEdges);
        }

        // Removes nodes no longer referenced by any edge and remaps ids/EdgeIds.
        private static void CompactGraph(RoadGraph graph)
        {
            var referenced = new HashSet<int>();
            foreach (var edge in graph.Edges)
            {
                referenced.Add(edge.NodeA);
                referenced.Add(edge.NodeB);
            }

            var oldToNew = new int[graph.Nodes.Count];
            var keptNodes = new List<RoadNode>();
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                if (!referenced.Contains(i)) { oldToNew[i] = -1; continue; }
                oldToNew[i] = keptNodes.Count;
                var node = graph.Nodes[i];
                node.Id = keptNodes.Count;
                node.EdgeIds.Clear();
                keptNodes.Add(node);
            }

            for (int i = 0; i < graph.Edges.Count; i++)
            {
                var edge = graph.Edges[i];
                edge.Id = i;
                edge.NodeA = oldToNew[edge.NodeA];
                edge.NodeB = oldToNew[edge.NodeB];
                keptNodes[edge.NodeA].EdgeIds.Add(edge.Id);
                keptNodes[edge.NodeB].EdgeIds.Add(edge.Id);
            }

            graph.Nodes.Clear();
            graph.Nodes.AddRange(keptNodes);
        }

        // ---------------------------------------------------------------
        // 5. Simplification (Ramer-Douglas-Peucker)
        // ---------------------------------------------------------------

        public static List<Vector2> DouglasPeucker(List<Vector2> points, float epsilon)
        {
            if (points.Count < 3) return new List<Vector2>(points);

            float maxDist = 0f;
            int index = 0;
            Vector2 start = points[0], end = points[points.Count - 1];

            for (int i = 1; i < points.Count - 1; i++)
            {
                float d = PerpendicularDistance(points[i], start, end);
                if (d > maxDist) { maxDist = d; index = i; }
            }

            if (maxDist > epsilon)
            {
                var left = DouglasPeucker(points.GetRange(0, index + 1), epsilon);
                var right = DouglasPeucker(points.GetRange(index, points.Count - index), epsilon);

                var result = new List<Vector2>(left);
                result.RemoveAt(result.Count - 1); // avoid duplicating the shared midpoint
                result.AddRange(right);
                return result;
            }

            return new List<Vector2> { start, end };
        }

        private static float PerpendicularDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            if (a == b) return Vector2.Distance(p, a);
            float num = Mathf.Abs((b.y - a.y) * p.x - (b.x - a.x) * p.y + b.x * a.y - b.y * a.x);
            float den = Vector2.Distance(a, b);
            return num / den;
        }

        // ---------------------------------------------------------------
        // 6. Smoothing (Chaikin corner cutting)
        // ---------------------------------------------------------------

        /// <summary>
        /// Rounds the corners of a polyline using Chaikin subdivision, run for
        /// the given number of iterations. The first and last point are kept
        /// fixed so edges stay attached to their node positions. Applied after
        /// Douglas-Peucker so it softens the simplified corners instead of the
        /// raw pixel-level jitter.
        /// </summary>
        public static List<Vector2> SmoothPolyline(List<Vector2> points, int iterations, float strength)
        {
            if (points.Count < 3 || iterations <= 0) return points;

            strength = Mathf.Clamp(strength, 0.01f, 0.49f);
            List<Vector2> current = points;

            for (int iter = 0; iter < iterations; iter++)
            {
                var next = new List<Vector2>((current.Count - 1) * 2);
                next.Add(current[0]);
                for (int i = 0; i < current.Count - 1; i++)
                {
                    Vector2 p0 = current[i];
                    Vector2 p1 = current[i + 1];
                    next.Add(Vector2.Lerp(p0, p1, strength));
                    next.Add(Vector2.Lerp(p0, p1, 1f - strength));
                }
                next.Add(current[current.Count - 1]);
                current = next;
            }

            return current;
        }
    }
}
