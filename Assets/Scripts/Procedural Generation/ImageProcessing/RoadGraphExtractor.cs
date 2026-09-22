using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.GraphToolkit.Editor;
using Unity.Profiling;
using UnityEngine;

namespace ProceduralGeneration.ImageProcessing
{
    /// <summary>
    /// A single node in the road network (an endpoint, junction, or synthetic
    /// loop marker). Position is in source-image pixel space unless you
    /// rescale it after extraction. This is the serialized/storage form —
    /// nodes and edges link to each other by Id, not by reference.
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
    /// starting at NodeA's position and ending at NodeB's position. This is
    /// the serialized/storage form — NodeA/NodeB are RoadNode ids, not references.
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
        /// extract a vector graph, clean it up, and serialize it.
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
        /// <param name="reconnectMinScore">Minimum 0-1 match score required to bridge two dangling segment ends across a gap. 0 disables reconnection.</param>
        public static RoadGraph ExtractFromPng(
            string filePath,
            Color roadColor,
            float colorTolerance = 0.15f,
            float simplifyEpsilon = 1.5f,
            float pixelToWorldScale = 1f,
            float minSpurLength = 0f,
            float junctionMergeRadius = 0f,
            int smoothingIterations = 0,
            float smoothingStrength = 0.25f,
            float reconnectMinScore = 0f,
            float reconnectMaxDistanceFac = 16f,
            float minSubgraphSize = 5f)
        {
            Texture2D tex = LoadTexture(filePath);
            try
            {
                bool[,] mask;
                using (new ProfilerMarker("Build mask").Auto())
                    mask = BuildRoadMask(tex, roadColor, colorTolerance);
                
                bool[,] skeleton;
                using (new ProfilerMarker("Build skeleton").Auto())
                    skeleton = ZhangSuenThin(mask);

                // All graph editing (pruning, merging, collapsing) happens on the
                // reference-linked runtime graph, where adding/removing a node or
                // edge is a direct list operation instead of an id-remap pass.
                LiveGraph live;
                using (new ProfilerMarker("Build live graph").Auto())
                    live = BuildLiveGraph(skeleton);

                using (new ProfilerMarker("Prune short").Auto())
                    PruneShortSpurs(live, minSpurLength);
                using (new ProfilerMarker("Merge nodes").Auto())
                    MergeCloseNodes(live, junctionMergeRadius);
                using (new ProfilerMarker("Prune short").Auto())
                    PruneShortSpurs(live, minSpurLength); // merging can expose new tiny dangling connectors
                using (new ProfilerMarker("Reconnect nodes").Auto())
                    ReconnectNodes(live, reconnectMinScore, reconnectMaxDistanceFac, junctionMergeRadius); // bridge gaps left in otherwise-continuous roads
                using (new ProfilerMarker("Collapse straight nodes").Auto())
                    CollapseStraightNodes(live); // fold pass-through nodes left by pruning/merging/reconnecting into their edges
                using (new ProfilerMarker("Prune subgraphs").Auto())
                    PruneLooseSegments(live, minSubgraphSize);
                
                using (new ProfilerMarker("Generate edges").Auto())
                {
                    foreach (var edge in live.Edges)
                    {
                        edge.Points = DouglasPeucker(edge.Points, simplifyEpsilon);
                        edge.Points = SmoothPolyline(edge.Points, smoothingIterations, smoothingStrength);
                    }
                }

                RoadGraph graph;
                using (new ProfilerMarker("Serialize graph").Auto())
                    graph = ToSerializedGraph(live);

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
        // 4. Runtime graph (reference-linked; mutated in place by cleanup)
        // ---------------------------------------------------------------

        // Working representation used only while building/cleaning the graph.
        // Nodes and edges point directly at each other, so adding, rewiring or
        // dropping one is a plain list operation — no ids to keep in sync.
        // It is converted to the id-linked RoadGraph/RoadNode/RoadEdge only
        // once, at the very end, for storage/serialization.
        private class LiveNode
        {
            public Vector2 Position;
            public readonly List<LiveEdge> Edges = new List<LiveEdge>();
        }

        private class LiveEdge
        {
            public LiveNode NodeA;
            public LiveNode NodeB;
            public List<Vector2> Points; // starts at NodeA.Position, ends at NodeB.Position
            public float Length;

            public LiveNode Other(LiveNode node) => NodeA == node ? NodeB : NodeA;
        }

        private class LiveGraph
        {
            public readonly List<LiveNode> Nodes = new List<LiveNode>();
            public readonly List<LiveEdge> Edges = new List<LiveEdge>();

            public void AddEdge(LiveEdge edge)
            {
                Edges.Add(edge);
                edge.NodeA.Edges.Add(edge);
                edge.NodeB.Edges.Add(edge); // added twice for a self-loop, matching graph-degree convention
            }

            public void RemoveEdge(LiveEdge edge)
            {
                Edges.Remove(edge);
                edge.NodeA.Edges.Remove(edge);
                edge.NodeB.Edges.Remove(edge);
            }

            public void RemoveOrphanNodes()
            {
                Nodes.RemoveAll(n => n.Edges.Count == 0);
            }
        }

        // ---------------------------------------------------------------
        // 4a. Graph extraction
        // ---------------------------------------------------------------

        private static readonly int[] Dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] Dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

        private static LiveGraph BuildLiveGraph(bool[,] skeleton)
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

            var graph = new LiveGraph();
            var pixelToNode = new Dictionary<Vector2Int, LiveNode>();

            LiveNode AddNode(Vector2Int p)
            {
                if (pixelToNode.TryGetValue(p, out var existing)) return existing;
                var node = new LiveNode { Position = new Vector2(p.x, p.y) };
                pixelToNode[p] = node;
                graph.Nodes.Add(node);
                nodePixels.Add(p);
                return node;
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
                LiveNode startNode = pixelToNode[start];

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

                    LiveNode endNode = AddNode(end);

                    var edge = new LiveEdge
                    {
                        NodeA = startNode,
                        NodeB = endNode,
                        Points = path,
                        Length = PathLength(path)
                    };
                    graph.AddEdge(edge);
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
        // 4b. Cleanup: prune noise spurs, merge clustered junctions,
        //     fold pass-through nodes
        // ---------------------------------------------------------------

        /// <summary>
        /// Repeatedly removes dangling dead-end edges shorter than minLength.
        /// Zhang-Suen thinning tends to leave short spurious branches off real
        /// roads; without this the network ends up looking overly dense.
        /// </summary>
        private static void PruneShortSpurs(LiveGraph graph, float minLength)
        {
            if (minLength <= 0f) return;

            bool removedAny = true;
            while (removedAny)
            {
                removedAny = false;
                for (int i = graph.Edges.Count - 1; i >= 0; i--)
                {
                    var edge = graph.Edges[i];
                    if (edge.Length >= minLength) continue;

                    bool aDangling = edge.NodeA.Edges.Count == 1;
                    bool bDangling = edge.NodeB.Edges.Count == 1;
                    if (!aDangling && !bDangling) continue;
                    if (aDangling && bDangling && graph.Edges.Count == 1) continue; // don't erase the whole graph

                    graph.RemoveEdge(edge);
                    removedAny = true;
                }
            }

            graph.RemoveOrphanNodes();
        }

        /// <summary>
        /// Merges nodes that lie within mergeRadius of each other into a single
        /// node at their centroid. Skeletonized intersections often produce a
        /// tight cluster of junction pixels instead of one clean crossing; this
        /// collapses those clusters and drops the now-degenerate connector edges.
        /// </summary>
        private static void MergeCloseNodes(LiveGraph graph, float mergeRadius)
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

            // Bucket nodes into a uniform grid sized to mergeRadius so we only ever
            // compare a node against others in its own and neighboring cells,
            // instead of every pair (O(n) instead of O(n^2) for the node counts a
            // large segmentation map produces).
            float radiusSqr = mergeRadius * mergeRadius;
            var grid = new Dictionary<(int, int), List<int>>();

            (int, int) CellOf(Vector2 pos) =>
                (Mathf.FloorToInt(pos.x / mergeRadius), Mathf.FloorToInt(pos.y / mergeRadius));

            for (int i = 0; i < n; i++)
            {
                var cell = CellOf(graph.Nodes[i].Position);
                if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new List<int>();
                list.Add(i);
            }

            for (int i = 0; i < n; i++)
            {
                var (cx, cy) = CellOf(graph.Nodes[i].Position);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy), out var neighbors)) continue;
                        foreach (int j in neighbors)
                        {
                            if (j <= i) continue; // each unordered pair checked once
                            if ((graph.Nodes[i].Position - graph.Nodes[j].Position).sqrMagnitude <= radiusSqr)
                                Union(i, j);
                        }
                    }
                }
            }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!groups.TryGetValue(r, out var list)) groups[r] = list = new List<int>();
                list.Add(i);
            }

            if (groups.Count == n) return; // nothing to merge

            var mergedFor = new LiveNode[n]; // old node index -> its replacement
            var newNodes = new List<LiveNode>();
            foreach (var ids in groups.Values)
            {
                Vector2 centroid = Vector2.zero;
                foreach (var id in ids) centroid += graph.Nodes[id].Position;
                centroid /= ids.Count;

                var mergedNode = new LiveNode { Position = centroid };
                foreach (var id in ids) mergedFor[id] = mergedNode;
                newNodes.Add(mergedNode);
            }

            var nodeToIndex = new Dictionary<LiveNode, int>(n);
            for (int i = 0; i < n; i++) nodeToIndex[graph.Nodes[i]] = i;

            var newEdges = new List<LiveEdge>();
            foreach (var edge in graph.Edges)
            {
                LiveNode a = mergedFor[nodeToIndex[edge.NodeA]];
                LiveNode b = mergedFor[nodeToIndex[edge.NodeB]];
                if (a == b) continue; // collapsed into a single node, drop the tiny connector

                if (edge.Points.Count > 0)
                {
                    edge.Points[0] = a.Position;
                    edge.Points[edge.Points.Count - 1] = b.Position;
                }

                edge.NodeA = a;
                edge.NodeB = b;
                a.Edges.Add(edge);
                b.Edges.Add(edge);
                newEdges.Add(edge);
            }

            graph.Nodes.Clear();
            graph.Nodes.AddRange(newNodes);
            graph.Edges.Clear();
            graph.Edges.AddRange(newEdges);
        }

        /// <summary>
        /// Bridges dangling segment ends that look like a real road broken by a
        /// gap in the segmentation. Two kinds of bridge are considered:
        /// end-to-end (two dangling ends facing each other across the gap) and
        /// end-to-segment (a dangling end running into the middle of another
        /// road, forming a proper T-junction there instead of leaving an open
        /// crossing). Candidates are scored on distance, how close they land to
        /// the straight-line extension of the segment(s) involved, and
        /// directional alignment — end-to-segment only checks alignment on the
        /// dangling end's own side, since a mid-segment point has no facing of
        /// its own to agree or disagree with. Any candidate whose bridge would
        /// cross another existing edge is discarded outright: roads may only
        /// meet at a shared node, never cross in open space.
        /// </summary>
        private static void ReconnectNodes(LiveGraph graph, float minConnectionScore, float reconnectMaxDistanceFac, float nodeSpacing)
        {
            if (minConnectionScore <= 0f) return;

            var openEnds = new List<(LiveNode Node, LiveEdge Edge, Vector2 Direction)>();
            foreach (var edge in graph.Edges)
            {
                if (edge.NodeA.Edges.Count == 1)
                    openEnds.Add((edge.NodeA, edge, ExitDirection(edge, edge.NodeA)));
                if (edge.NodeB.Edges.Count == 1 && edge.NodeB != edge.NodeA)
                    openEnds.Add((edge.NodeB, edge, ExitDirection(edge, edge.NodeB)));
            }

            int count = openEnds.Count;
            if (count == 0) return;

            float distanceThreshold = nodeSpacing > 0f ? reconnectMaxDistanceFac / nodeSpacing : reconnectMaxDistanceFac;

            // Best end-to-end (dangling-to-dangling) candidate per open end.
            var bestEndScore = new float[count];
            var bestEndMatch = new int[count];
            for (int i = 0; i < count; i++) bestEndMatch[i] = -1;

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (openEnds[i].Edge == openEnds[j].Edge) continue; // both ends of the same open segment

                    float score = ScoreEndToEnd(openEnds[i], openEnds[j], distanceThreshold);
                    if (score < minConnectionScore) continue;
                    if (score <= bestEndScore[i] && score <= bestEndScore[j]) continue;
                    if (CrossesAnyEdge(graph, openEnds[i].Node.Position, openEnds[j].Node.Position, openEnds[i].Edge, openEnds[j].Edge))
                        continue; // would cross another road; discard rather than let roads overlap without a junction

                    if (score > bestEndScore[i]) { bestEndScore[i] = score; bestEndMatch[i] = j; }
                    if (score > bestEndScore[j]) { bestEndScore[j] = score; bestEndMatch[j] = i; }
                }
            }

            // Best end-to-segment (T-junction) candidate per open end: the
            // closest point on any other edge that the dangling end could run into.
            var bestSegScore = new float[count];
            var bestSegEdge = new LiveEdge[count];
            var bestSegPoint = new ClosestPointResult[count];

            for (int i = 0; i < count; i++)
            {
                var (node, ownEdge, dir) = openEnds[i];
                foreach (var edge in graph.Edges)
                {
                    if (edge == ownEdge) continue;

                    ClosestPointResult closest = ClosestPointOnEdge(edge, node.Position);
                    float score = ScoreEndToPoint(node.Position, dir, ownEdge.Length, closest.Point, distanceThreshold);
                    if (score < minConnectionScore || score <= bestSegScore[i]) continue;
                    if (CrossesAnyEdge(graph, node.Position, closest.Point, ownEdge, edge)) continue;

                    bestSegScore[i] = score;
                    bestSegEdge[i] = edge;
                    bestSegPoint[i] = closest;
                }
            }

            // Resolve: for each open end, take whichever of its two candidate
            // kinds scores higher (end-to-end requires a mutual best match, like
            // before; end-to-segment splits the target edge to create a junction).
            var used = new bool[count];
            var consumedEdges = new HashSet<LiveEdge>(); // edges already split by an earlier T-junction this pass
            const float snapEpsilon = 0.5f; // treat a T-junction landing this close to an existing node as hitting that node directly
            int endToEndCount = 0;
            int tJunctionCount = 0;

            for (int i = 0; i < count; i++)
            {
                if (used[i]) continue;

                int j = bestEndMatch[i];
                bool endToEndValid = j != -1 && bestEndScore[i] >= minConnectionScore
                                      && bestEndMatch[j] == i && !used[j];
                bool segValid = bestSegEdge[i] != null && bestSegScore[i] >= minConnectionScore
                                 && !consumedEdges.Contains(bestSegEdge[i]);

                if (endToEndValid && (!segValid || bestEndScore[i] >= bestSegScore[i]))
                {
                    LiveNode a = openEnds[i].Node;
                    LiveNode b = openEnds[j].Node;
                    graph.AddEdge(new LiveEdge
                    {
                        NodeA = a,
                        NodeB = b,
                        Points = new List<Vector2> { a.Position, b.Position },
                        Length = Vector2.Distance(a.Position, b.Position)
                    });
                    used[i] = true;
                    used[j] = true;
                    endToEndCount++;
                }
                else if (segValid)
                {
                    LiveNode a = openEnds[i].Node;
                    LiveEdge targetEdge = bestSegEdge[i];
                    Vector2 point = bestSegPoint[i].Point;

                    LiveNode junction;
                    if (Vector2.Distance(point, targetEdge.NodeA.Position) < snapEpsilon) junction = targetEdge.NodeA;
                    else if (Vector2.Distance(point, targetEdge.NodeB.Position) < snapEpsilon) junction = targetEdge.NodeB;
                    else
                    {
                        junction = SplitEdgeAt(graph, targetEdge, bestSegPoint[i]);
                        consumedEdges.Add(targetEdge);
                    }

                    graph.AddEdge(new LiveEdge
                    {
                        NodeA = a,
                        NodeB = junction,
                        Points = new List<Vector2> { a.Position, junction.Position },
                        Length = Vector2.Distance(a.Position, junction.Position)
                    });
                    used[i] = true;
                    tJunctionCount++;
                }
            }

            Logger.Logger.Log($"Reconnections: {endToEndCount} end-to-end, {tJunctionCount} T-junctions", "ProcGen");
        }

        // Direction of travel as the edge arrives at node: from the point before node to node's position.
        private static Vector2 ExitDirection(LiveEdge edge, LiveNode node)
        {
            var points = PointsEndingAt(edge, node);
            if (points.Count < 2) return Vector2.zero;
            Vector2 dir = points[^1] - points[^2];
            return dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector2.zero;
        }

        // Combines distance, extension-line proximity and directional alignment into
        // a single 0-1 score for two dangling ends facing each other. All three are
        // normalized against the reference length (the average length of the two
        // candidate segments, or the gap itself if that's larger) so a gap is only
        // considered a good match when it is small and straight relative to the
        // roads it would connect — no magic constants.
        private static float ScoreEndToEnd(
            (LiveNode Node, LiveEdge Edge, Vector2 Direction) a,
            (LiveNode Node, LiveEdge Edge, Vector2 Direction) b,
            float distanceThreshold)
        {
            Vector2 delta = b.Node.Position - a.Node.Position;
            float distance = delta.magnitude;
            if (distance < 1e-4f) return 0f;
            Vector2 dirAB = delta / distance;

            // Both ends must keep heading toward each other across the gap,
            // not turn away or fold back on themselves.
            float alignA = Vector2.Dot(a.Direction, dirAB);
            float alignB = Vector2.Dot(b.Direction, -dirAB);
            if (alignA <= 0f || alignB <= 0f) return 0f;
            float alignmentScore = alignA * alignB;

            float referenceLength = Mathf.Max((a.Edge.Length + b.Edge.Length) * 0.5f * distanceThreshold, distance);

            // How close each end sits to the other segment's straight-line extension.
            Vector2 perpA = new Vector2(-a.Direction.y, a.Direction.x);
            Vector2 perpB = new Vector2(-b.Direction.y, b.Direction.x);
            float deviation = (Mathf.Abs(Vector2.Dot(perpA, delta)) + Mathf.Abs(Vector2.Dot(perpB, delta))) * 0.5f;
            float extensionScore = Mathf.Clamp01(1f - deviation / referenceLength);

            // A gap that's small relative to the roads it joins is far more
            // likely to be a real break than a coincidental alignment.
            float distanceScore = Mathf.Clamp01(1f - distance / referenceLength);

            return alignmentScore * extensionScore * distanceScore;
        }

        // Scores a dangling end running into a point on another road (a T-junction
        // candidate). Unlike ScoreEndToEnd this only checks alignment on the
        // dangling end's own side — a mid-segment point has no direction of its
        // own to face back with, so requiring it would rule out real T-junctions.
        private static float ScoreEndToPoint(Vector2 fromPos, Vector2 fromDir, float fromEdgeLength, Vector2 targetPoint, float distanceThreshold)
        {
            Vector2 delta = targetPoint - fromPos;
            float distance = delta.magnitude;
            if (distance < 1e-4f) return 0f;
            Vector2 dirTo = delta / distance;

            float alignA = Vector2.Dot(fromDir, dirTo);
            if (alignA <= 0f) return 0f; // must be heading toward the target, not away from it

            float referenceLength = Mathf.Max(fromEdgeLength * distanceThreshold, distance);

            Vector2 perpA = new Vector2(-fromDir.y, fromDir.x);
            float deviation = Mathf.Abs(Vector2.Dot(perpA, delta));
            float extensionScore = Mathf.Clamp01(1f - deviation / referenceLength);

            float distanceScore = Mathf.Clamp01(1f - distance / referenceLength);

            return alignA * extensionScore * distanceScore;
        }

        private struct ClosestPointResult
        {
            public int SegmentIndex;
            public Vector2 Point;
        }

        // Finds the closest point to `from` lying anywhere on edge's polyline.
        private static ClosestPointResult ClosestPointOnEdge(LiveEdge edge, Vector2 from)
        {
            var points = edge.Points;
            float bestDistSqr = float.PositiveInfinity;
            var best = new ClosestPointResult { SegmentIndex = 0, Point = points[0] };

            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector2 p0 = points[i];
                Vector2 p1 = points[i + 1];
                Vector2 seg = p1 - p0;
                float segLenSqr = seg.sqrMagnitude;
                float t = segLenSqr > 1e-8f ? Mathf.Clamp01(Vector2.Dot(from - p0, seg) / segLenSqr) : 0f;
                Vector2 point = p0 + seg * t;

                float distSqr = (from - point).sqrMagnitude;
                if (distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    best = new ClosestPointResult { SegmentIndex = i, Point = point };
                }
            }

            return best;
        }

        // Splits edge into two at the given point (inserting a new junction node)
        // and returns that node. Used to turn a dangling end running into the
        // middle of a road into a proper T-junction instead of an open crossing.
        private static LiveNode SplitEdgeAt(LiveGraph graph, LiveEdge edge, ClosestPointResult at)
        {
            var junction = new LiveNode { Position = at.Point };

            var pointsA = edge.Points.GetRange(0, at.SegmentIndex + 1);
            if (pointsA[^1] != at.Point) pointsA.Add(at.Point);

            var pointsB = new List<Vector2> { at.Point };
            pointsB.AddRange(edge.Points.GetRange(at.SegmentIndex + 1, edge.Points.Count - (at.SegmentIndex + 1)));

            LiveNode nodeA = edge.NodeA;
            LiveNode nodeB = edge.NodeB;

            graph.RemoveEdge(edge);
            graph.Nodes.Add(junction);

            graph.AddEdge(new LiveEdge { NodeA = nodeA, NodeB = junction, Points = pointsA, Length = PathLength(pointsA) });
            graph.AddEdge(new LiveEdge { NodeA = junction, NodeB = nodeB, Points = pointsB, Length = PathLength(pointsB) });

            return junction;
        }

        // True if the segment from p1 to p2 crosses any edge in the graph other
        // than the ones explicitly ignored (the edges owning the endpoints being
        // connected). Keeps the network planar: roads should only ever meet at a
        // shared node, never overlap in open space.
        private static bool CrossesAnyEdge(LiveGraph graph, Vector2 p1, Vector2 p2, LiveEdge ignoreA, LiveEdge ignoreB)
        {
            Vector2 boundsMin = Vector2.Min(p1, p2);
            Vector2 boundsMax = Vector2.Max(p1, p2);

            foreach (var edge in graph.Edges)
            {
                if (edge == ignoreA || edge == ignoreB) continue;

                var points = edge.Points;
                for (int i = 0; i < points.Count - 1; i++)
                {
                    Vector2 q1 = points[i];
                    Vector2 q2 = points[i + 1];

                    if (Mathf.Max(q1.x, q2.x) < boundsMin.x || Mathf.Min(q1.x, q2.x) > boundsMax.x) continue;
                    if (Mathf.Max(q1.y, q2.y) < boundsMin.y || Mathf.Min(q1.y, q2.y) > boundsMax.y) continue;

                    if (SegmentsIntersect(p1, p2, q1, q2)) return true;
                }
            }

            return false;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        // Proper-crossing test — touching at a shared endpoint doesn't count, so a
        // bridge that lands exactly on another road's node isn't flagged as obstructed.
        private static bool SegmentsIntersect(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
        {
            float d1 = Cross(b2 - b1, a1 - b1);
            float d2 = Cross(b2 - b1, a2 - b1);
            float d3 = Cross(a2 - a1, b1 - a1);
            float d4 = Cross(a2 - a1, b2 - a1);

            return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f))
                && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
        }

        /// <summary>
        /// Folds nodes that only have two incident edges into their neighbors,
        /// merging the pair of edges into one continuous edge and dropping the
        /// node. By construction only endpoints (degree 1) and junctions
        /// (degree >= 3) should be nodes; pruning short spurs or merging close
        /// junctions can leave behind degree-2 "pass-through" nodes that split
        /// what is really a single straight/curving road into multiple edges
        /// with a lot of redundant nodes. Collapsing them here lets a single
        /// Douglas-Peucker pass simplify the full road instead of stopping at
        /// each artificial split.
        /// </summary>
        private static void CollapseStraightNodes(LiveGraph graph, float maxAlignment = 0.9f)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;

                foreach (var node in new List<LiveNode>(graph.Nodes))
                {
                    if (node.Edges.Count != 2) continue;

                    LiveEdge edgeA = node.Edges[0];
                    LiveEdge edgeB = node.Edges[1];
                    if (edgeA == edgeB) continue; // a self-loop touching this node twice; nothing to fold

                    LiveNode farA = edgeA.Other(node);
                    LiveNode farB = edgeB.Other(node);
                    
                    Vector2 dA = (farA.Position - node.Position).normalized;
                    Vector2 dB = (node.Position - farB.Position).normalized;
                    if (Vector2.Dot(dA, dB) < maxAlignment) continue;

                    List<Vector2> merged = PointsEndingAt(edgeA, node);
                    merged.RemoveAt(merged.Count - 1); // drop the shared node point before appending
                    merged.AddRange(PointsStartingAt(edgeB, node));

                    graph.RemoveEdge(edgeA);
                    graph.RemoveEdge(edgeB);
                    graph.Nodes.Remove(node);

                    graph.AddEdge(new LiveEdge
                    {
                        NodeA = farA,
                        NodeB = farB,
                        Points = merged,
                        Length = edgeA.Length + edgeB.Length
                    });
                    changed = true;
                }
            }
        }

        private static void PruneLooseSegments(LiveGraph graph, float minSegmentLength)
        {
            Dictionary<LiveEdge, int> edgeSubgraph = new Dictionary<LiveEdge, int>();
            List<List<LiveEdge>> subgraphs = new List<List<LiveEdge>>();
            List<int> toRemove = new List<int>();
            int subID = 0;
            foreach (var edge in graph.Edges)
            {
                edgeSubgraph.Add(edge, -1);
            }
            foreach (var edge in graph.Edges)
            {
                if (edgeSubgraph[edge] != -1)
                    continue;
                subgraphs.Add(new List<LiveEdge>());
                float length = FloodSubgraph(edge, edgeSubgraph, subID, subgraphs);
                if (length < minSegmentLength)
                {
                    toRemove.Add(subID);
                }
                subID++;
            }
            foreach (int id in toRemove)
            {
                foreach (var edge in subgraphs[id])
                {
                    graph.RemoveEdge(edge);
                }
            }
            graph.RemoveOrphanNodes();
        }

        private static float FloodSubgraph(
            LiveEdge start,
            Dictionary<LiveEdge, int> edgeSubgraph,
            int subID,
            List<List<LiveEdge>> subgraphs)
        {
            Queue<LiveEdge> queue = new Queue<LiveEdge>();
            queue.Enqueue(start);
            float length = 0;
            
            subgraphs[subID].Add(start);
            edgeSubgraph[start] = subID;
            while (queue.Count > 0)
            {
                var edge = queue.Dequeue();
                length += edge.Length;
                if (edge.NodeA.Edges.Count > 1)
                {
                    foreach (var neighbour in edge.NodeA.Edges)
                    {
                        if (edgeSubgraph[neighbour] != -1) continue;
                        queue.Enqueue(neighbour);
                        subgraphs[subID].Add(neighbour);
                        edgeSubgraph[neighbour] = subID;
                    }
                }
                if (edge.NodeB.Edges.Count > 1)
                {
                    foreach (var neighbour in edge.NodeB.Edges)
                    {
                        if (edgeSubgraph[neighbour] != -1) continue;
                        queue.Enqueue(neighbour);
                        subgraphs[subID].Add(neighbour);
                        edgeSubgraph[neighbour] = subID;
                    }
                }
            }
            return length;
        }

        // Returns a copy of edge.Points ordered so it ends at node's position.
        private static List<Vector2> PointsEndingAt(LiveEdge edge, LiveNode node)
        {
            var points = new List<Vector2>(edge.Points);
            if (edge.NodeB != node) points.Reverse();
            return points;
        }

        // Returns a copy of edge.Points ordered so it starts at node's position.
        private static List<Vector2> PointsStartingAt(LiveEdge edge, LiveNode node)
        {
            var points = new List<Vector2>(edge.Points);
            if (edge.NodeA != node) points.Reverse();
            return points;
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

        // ---------------------------------------------------------------
        // 7. Serialization: reference graph -> id-linked storage container
        // ---------------------------------------------------------------

        private static RoadGraph ToSerializedGraph(LiveGraph liveGraph)
        {
            var graph = new RoadGraph();
            var nodeIds = new Dictionary<LiveNode, int>(liveGraph.Nodes.Count);

            foreach (var liveNode in liveGraph.Nodes)
            {
                var node = new RoadNode { Id = graph.Nodes.Count, Position = liveNode.Position };
                nodeIds[liveNode] = node.Id;
                graph.Nodes.Add(node);
            }

            foreach (var liveEdge in liveGraph.Edges)
            {
                var edge = new RoadEdge
                {
                    Id = graph.Edges.Count,
                    NodeA = nodeIds[liveEdge.NodeA],
                    NodeB = nodeIds[liveEdge.NodeB],
                    Points = liveEdge.Points,
                    Length = liveEdge.Length
                };
                graph.Edges.Add(edge);
                graph.Nodes[edge.NodeA].EdgeIds.Add(edge.Id);
                graph.Nodes[edge.NodeB].EdgeIds.Add(edge.Id);
            }

            return graph;
        }
    }
}
