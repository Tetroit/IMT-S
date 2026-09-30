using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public struct Segment2 {
    public Vector2 a, b;
    public Segment2(Vector2 a, Vector2 b) { this.a = a; this.b = b; }
}

class SkeletonVertex {
    public Vector2 pos;
    public Vector2 bisector;
    public SkeletonVertex prev;
    public SkeletonVertex next;
    public int depth;
        
    public SkeletonVertex(Vector2 p) {
        pos = p; 
        bisector = Vector2.zero; 
        prev = next = null;
        depth = 0;
    }
        
    public SkeletonVertex(Vector2 p, SkeletonVertex a, SkeletonVertex b) {
        pos = p; 
        bisector = Vector2.zero;
        prev = a;
        next = b;
        depth = 0;
    }
}

public static class SimpleStraightSkeleton {
    
    public static List<Segment2> computeSkeleton(List<Vector3> outlinePoints, List<Vector3> centerLinePoints, float mergeDist) {
        List<SkeletonVertex> visited = new List<SkeletonVertex>();
        var skeleton = new List<Segment2>();
        
        if (outlinePoints == null || outlinePoints.Count < 3) {
            return skeleton;
        }
        
        var outline = new List<Vector2>();
        var centerLine = new List<Vector2>();
        
        foreach (var i in outlinePoints) {
            outline.Add(VectorUtils.to2(i));
        }
        
        foreach (var i in centerLinePoints) {
            centerLine.Add(VectorUtils.to2(i));
        }
        
        var verts = createVertices(outline, 30, .6f);
        foreach (var v in verts) {
            updateBisector(v, centerLine);
        }
        
        int maxIters = Math.Min(1000, verts.Count * 10);
        int iter = 0;
        
        while (verts.Count> 2 && iter++ < maxIters) {
            
            float bestT = float.PositiveInfinity;
            SkeletonVertex bestV = null;
            SkeletonVertex bestNext = null;
            Vector2 bestPoint = Vector2.zero;

            for (int i = 0; i < verts.Count; i++) {
                SkeletonVertex v = verts[i];
                SkeletonVertex vNext = verts[i].next;
                // solve intersection of rays: v.pos + t * v.bisector  and  w.pos + s * w.bisector
                var hitTest = intersectRays(v.pos, v.bisector, vNext.pos, vNext.bisector);
                if (hitTest.intersect) {
                    if (hitTest.t > VectorUtils.EPS && hitTest.s > VectorUtils.EPS) {
                        if (hitTest.t < bestT) {
                            bestT = hitTest.t;
                            bestV = v;
                            bestNext = v.next;
                            bestPoint = hitTest.hit;
                        }
                    }
                }
            }
            
            if (bestV == null) {
                break;
            }
 
            SkeletonVertex prev = bestV.prev;
            SkeletonVertex next = bestNext.next;
            
            SkeletonVertex merged = new SkeletonVertex(bestPoint, prev, next);
            merged.depth = Mathf.Max(bestV.depth, bestNext.depth) + 1;
            
            var snappedToCenter = closestPointOnSegments(centerLine, bestV.pos);
            
            if (merged.depth >= 2) {
                bestPoint = snappedToCenter;
            }
            merged.pos = bestPoint;
            
            bestV.next = merged;
            bestNext.next = merged;
            
            visited.Add(bestV);
            verts.Remove(bestV);
            visited.Add(bestNext);
            verts.Remove(bestNext);

    
            int insertIndex = Math.Max(0, verts.IndexOf(prev) + 1);
            if (insertIndex < 0 || insertIndex > verts.Count) {
                insertIndex = verts.Count;
            }

            verts.Insert(insertIndex, merged);
            
            rebuildLinks(verts);

            if (prev != null) {
                updateBisector(prev, centerLine);
            }
            if (next != null) {
                updateBisector(next, centerLine);
            }
            updateBisector(merged, centerLine);

        }
        
        if (verts.Count == 2) {
            var a = verts[0];
            a.next = verts[1];
            visited.Add(a);
        }
        
        for (int i = 0; i < visited.Count; i++) {
            SkeletonVertex v = visited[i];
            skeleton.Add(new Segment2(v.pos, v.next.pos));
        }
        
        return buildAllSegments(outline, skeleton);;
    }
    
    static List<SkeletonVertex> createVertices(List<Vector2> points, float angleThresholdDegrees, float minDistance) {
        
        var verts = new List<SkeletonVertex>();
        int n = points.Count;
        if (n < 2) return verts;
        float currentAngle = 0;
        
        for (int i = 0; i < n; i++) {
            Vector2 prev = points[(i - 1 + n) % n];
            Vector2 curr = points[i];
            Vector2 next = points[(i + 1) % n];

            Vector2 dirPrev = (curr - prev).normalized;
            Vector2 dirNext = (next - curr).normalized;

            float angle = Vector2.Angle(dirPrev, dirNext);
            currentAngle += angle;
            float d = Vector2.Distance(prev, curr);

            if (i == 0 || i == n - 1 || (currentAngle > angleThresholdDegrees && d > 0)) {
                var sv = new SkeletonVertex(curr);
                verts.Add(sv);
                currentAngle = 0;
            }
        }
        
        rebuildLinks(verts);
        return verts;
    }
    
    static void rebuildLinks(List<SkeletonVertex> verts) {
        for (int i = 0; i < verts.Count; i++) {
            verts[i].prev = verts[(i - 1 + verts.Count) % verts.Count];
            verts[i].next = verts[(i + 1) % verts.Count];
        }
    }
    
    static (bool intersect, float t, float s, Vector2 hit) intersectRays(Vector2 p, Vector2 u, Vector2 q, Vector2 v) {
        float t = 0;
        float s = 0;
        Vector2 hit = Vector2.zero;
        float denom = u.x * v.y - u.y * v.x;
        if (Mathf.Abs(denom) < VectorUtils.EPS) {
            return (false, t, s, hit);
        }
        
        Vector2 pq = q - p;
        t = (pq.x * v.y - pq.y * v.x) / denom;
        s = (pq.x * u.y - pq.y * u.x) / denom;
        hit = p + u * t;
        return (true, t, s, hit);
    }
    
    static Vector2 closestPointOnSegment(Vector2 a, Vector2 b, Vector2 p) {
        Vector2 ab = b - a;
        float t = Vector2.Dot(p - a, ab) / Vector2.Dot(ab, ab);
        t = Mathf.Clamp01(t);
        return a + t * ab;
    }
    
    static Vector2 closestPointOnSegments(List<Vector2> centerLine, Vector2 point) {
        float best = float.PositiveInfinity;
        Vector2 found = centerLine[0];
        
        for (int i = 0; i < centerLine.Count - 1; i++) {
            Vector2 a = centerLine[i];
            Vector2 b = centerLine[i + 1];

            Vector2 q = closestPointOnSegment(a, b, point);
            float d2 = (q - point).magnitude;

            if (d2 < best) {
                best = d2;
                found = q;
            }
        }

        return found;
    }
    
    static void updateBisector(SkeletonVertex v, List<Vector2> centerLine) {
        updateBisector(v, v.prev.pos, v.next.pos, centerLine);
    }
    
    static void updateBisector(SkeletonVertex v, Vector2 prevPos, Vector2 nextPos, List<Vector2> centerLine) {
        
        Vector2 d1 = (v.pos - prevPos);
        Vector2 d2 = (nextPos - v.pos);
        if (d1.magnitude < VectorUtils.EPS|| d2.magnitude < VectorUtils.EPS) {
            v.bisector = Vector2.zero;
            return;
        }
        
        d1 = d1.normalized; 
        d2 = d2.normalized;
        
        Vector2 b = (-d1 + d2);

        if (b.sqrMagnitude < VectorUtils.EPS) {
            Vector2 edge = (nextPos - prevPos).normalized;
            b = new Vector2(-edge.y, edge.x);
        }

        b.Normalize();
        var center = closestPointOnSegments(centerLine, v.pos);
        Vector2 toCenter = (center - v.pos).normalized;

        if (Vector2.Dot(b, toCenter) < 0f) {
            b = -b; 
        }
        
        v.bisector = b;
    }
    
    static List<Segment2> buildAllSegments(List<Vector2> outline, List<Segment2> skeleton) {
        var all = new List<Segment2>();
        
        for (int i = 0; i < outline.Count; i++) {
            Vector2 a = outline[i];
            Vector2 b = outline[(i + 1) % outline.Count];
            all.Add(new Segment2(a, b));
        }
        
        all.AddRange(skeleton);

        return all;
    }
}

public static class PlanarCellsBuilder {
    const int SAFETY_MULT = 5;
    
    static (List<(int, int)> segments, List<Vector2> newSeedPoints) splitSegmentsAtIntersections(List<Segment2> segs, List<Vector2> seedPoints, float eps) {
      
        var pts = new List<Vector2>(seedPoints);
        var interMap = new Dictionary<Vector2,int>();
        for (int i = 0; i < pts.Count; i++) {
            interMap[pts[i]] = i;
        }
        
        var segIntersections = new List<List<int>>();
        for (int i = 0; i < segs.Count; i++) {
            segIntersections.Add(new List<int>());
        }

        for (int i = 0; i < segs.Count; i++) {
            var sa = segs[i].a; 
            var sb = segs[i].b;
            
            Vector2 qSA = VectorUtils.quantize(sa, eps);
            if (!interMap.ContainsKey(qSA)) {
                interMap[qSA] = pts.Count;
                pts.Add(qSA);
            }
            segIntersections[i].Add(interMap[qSA]);
            
            Vector2 qSB = VectorUtils.quantize(sb, eps);
            if (!interMap.ContainsKey(qSB)) {
                interMap[qSB] = pts.Count;
                pts.Add(qSB);
            }
            segIntersections[i].Add(interMap[qSB]);
        }
        
        for (int i = 0; i < segs.Count; i++) {
            for (int j = i+1; j < segs.Count; j++) {
                var intersection = VectorUtils.segmentsIntersect(segs[i].a, segs[i].b, segs[j].a, segs[j].b);
                if (intersection.hit) {
                    Vector2 qHit = VectorUtils.quantize(intersection.hitPos, eps);
                    if (!interMap.ContainsKey(qHit)) {
                        interMap[qHit] = pts.Count;
                        pts.Add(qHit);
                    }
                    segIntersections[i].Add(interMap[qHit]);
                    segIntersections[j].Add(interMap[qHit]);
                }
            }
        }
        
        var segments = new List<(int,int)>();
        for (int i = 0; i < segs.Count; i++) {
            var s = segs[i];
            var list = segIntersections[i];
            var unique = new HashSet<int>(list);
            
            var projectedPoints  = new List<(float t,int id)>();

            float len = (s.b - s.a).magnitude;
            if (len >= VectorUtils.EPS) {
                foreach (var id in unique) {
                    Vector2 p = pts[id];
                    float t = Vector2.Dot(p - s.a, s.b - s.a) / (len*len);
                    projectedPoints .Add((t, id));
                }
                projectedPoints.Sort((x,y) => x.t.CompareTo(y.t));

                for (int k = 1; k < projectedPoints.Count; k++) {
                    int id0 = projectedPoints[k-1].id;
                    int id1 = projectedPoints[k].id;
                    if (id0 != id1 && id0 >= 0 && id1 >= 0) {
                        segments.Add((id0, id1));
                    }
                }
            }
            
        }
        
        return (segments, pts);
    }
    
    static List<List<int>> buildAdjacencyIndexed(List<(int,int)> segs, List<Vector2> points) {
        var adj = new List<List<int>>(points.Count);

        for (int i = 0; i < points.Count; i++) {
            adj.Add(new List<int>());
        }
        
        foreach (var e in segs) {
            int a = e.Item1, b = e.Item2;
            if (!adj[a].Contains(b)) {
                adj[a].Add(b);
            }
            if (!adj[b].Contains(a)) {
                adj[b].Add(a);
            }
        }
        
        for (int i = 0; i < points.Count; i++) {
            int center = i;

            adj[i].Sort((u, v) => {
                Vector2 cu = points[u] - points[center];
                Vector2 cv = points[v] - points[center];

                float au = Mathf.Atan2(cu.y, cu.x);
                float av = Mathf.Atan2(cv.y, cv.x);

                float da = au - av;
                
                if (Mathf.Abs(da) > VectorUtils.EPS)
                    return da < 0 ? -1 : 1;
                
                float lu = cu.sqrMagnitude;
                float lv = cv.sqrMagnitude;
                return lu < lv ? -1 : 1;
            });
        }
        
        return adj;
    }

    static List<List<int>> extractCellsIndexed(List<List<int>> adj) {

        var used = new HashSet<(int,int)>();
        var cellsIdx = new List<List<int>>();

        for (int a = 0; a < adj.Count; a++) {
            foreach (var b in adj[a]) {
                if (!used.Contains((a, b))) {

                    var cell = new List<int>();
                    int prev = a;
                    int curr = b;
                    cell.Add(prev);

                    int safety = adj.Count * SAFETY_MULT;
                    bool closed = false;
                    
                    for (int step = 0; step < safety; step++) {
                        used.Add((prev, curr));
                        cell.Add(curr);

                        var nbrs = adj[curr];
                        if (nbrs == null || nbrs.Count == 0) { 
                            break;
                        }

                        int idx = nbrs.IndexOf(prev);
                        if (idx < 0) {
                            break;
                        }

                        int nextIdx = (idx - 1 + nbrs.Count) % nbrs.Count;
                        int next = nbrs[nextIdx];

                        if (next == a) {
                            used.Add((curr, next));
                            closed = true;
                            break;
                        }

                        if (next == prev) {
                            break;
                        }
                        prev = curr;
                        curr = next;
                    }

                    if (closed && cell.Count >= 3) {
                        cellsIdx.Add(cell);
                    }
                }
            }
        }

        return cellsIdx;
    }
    
    static float signedArea(List<Vector2> poly) {
        float a = 0f;
        int n = poly.Count;

        for (int i = 0; i < n; i++) {
            Vector2 p0 = poly[i];
            Vector2 p1 = poly[(i + 1) % n];
            a += (p0.x * p1.y - p1.x * p0.y);
        }

        return 0.5f * a;
    }
    
    static List<List<Vector2>> filterExternalFace(List<List<Vector2>> cells) {
        if (cells == null || cells.Count == 0)
            return new List<List<Vector2>>();

        int outerIndex = -1;
        float maxAbsArea = float.NegativeInfinity;

        for (int i = 0; i < cells.Count; i++) {
            float a = Mathf.Abs(signedArea(cells[i]));
            if (a > maxAbsArea) {
                maxAbsArea = a;
                outerIndex = i;
            }
        }

        var result = new List<List<Vector2>>();

        for (int i = 0; i < cells.Count; i++) {
            if (i != outerIndex) {
                result.Add(cells[i]);
            }
        }

        return result;
    }
    
    public static List<List<Vector2>> buildCellsFromSkeleton(List<Segment2> skeleton) {
        
        var seedPoints = new List<Vector2>();

        foreach (var s in skeleton) {
            Vector2 qa = VectorUtils.quantize(s.a, VectorUtils.EPS);
            Vector2 qb = VectorUtils.quantize(s.b, VectorUtils.EPS);

            if (!seedPoints.Contains(qa)) {
                seedPoints.Add(qa);
            }

            if (!seedPoints.Contains(qb)) {
                seedPoints.Add(qb);
            }
        }

        var indexedSegs = splitSegmentsAtIntersections(skeleton, seedPoints, VectorUtils.EPS);
        var adj = buildAdjacencyIndexed(indexedSegs.segments, indexedSegs.newSeedPoints);
        var cellsIdx = extractCellsIndexed(adj);
        
        var cells = new List<List<Vector2>>();
        foreach (var ci in cellsIdx) {
            var poly = new List<Vector2>();
            foreach (var idx in ci) {
                poly.Add(indexedSegs.newSeedPoints[idx]);
            }
            cells.Add(poly);
        }

        cells = filterExternalFace(cells);

        foreach (var cell in cells) {
            if (signedArea(cell) > 0) {
                cell.Reverse();
            }
        }
        
        return cells;
    }
    
    public static Vector3 to3(Vector2 v, float y) => new Vector3(v.x, y, v.y);

    public static List<List<Vector3>> cellsIn3D(List<List<Vector2>> cells, float customHeight) {
        List<List<Vector3>> newCells = new List<List<Vector3>>();
        
        for (int i = 0; i < cells.Count; i++) {

            var cellPoints = cells[i];
            Vector3[] points = new Vector3[cellPoints.Count];
            for (int j = 0; j < cellPoints.Count; j++) {
                Vector3 worldP = to3(cellPoints[j], customHeight);
                points[j] = worldP;
            }
    
            newCells.Add(points.ToList());
        }
        return newCells;
    }
}

