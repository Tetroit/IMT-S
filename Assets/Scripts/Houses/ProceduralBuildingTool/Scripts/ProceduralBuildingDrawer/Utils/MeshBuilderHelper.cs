using System.Collections.Generic;
using UnityEngine;

public static class MeshBuilderHelper {
    public static bool isConvexTriangle(Vector3 a, Vector3 b, Vector3 c) {
        Vector3 cross = Vector3.Cross(b - a, c - b);
        return cross.y >= 0f;
    }
    
    public static bool isPointInTriangle(Vector3 point, Vector3 triangleA, Vector3 triangleB, Vector3 triangleC) {
        Vector3 v0 = triangleC - triangleA;
        Vector3 v1 = triangleB - triangleA;
        Vector3 v2 = point - triangleA;
    
        float dot00 = Vector3.Dot(v0, v0);
        float dot01 = Vector3.Dot(v0, v1);
        float dot02 = Vector3.Dot(v0, v2);
        float dot11 = Vector3.Dot(v1, v1);
        float dot12 = Vector3.Dot(v1, v2);
    
        float denom = dot00 * dot11 - dot01 * dot01;
        if (Mathf.Abs(denom) < VectorUtils.EPS) return false;
        float u = (dot11 * dot02 - dot01 * dot12) / denom;
        float v = (dot00 * dot12 - dot01 * dot02) / denom;
        return u >= 0 && v >= 0 && (u + v) <= 1;
    }
    
    private static List<int> triangulatePolygon(List<Vector3> poly) {
        List<int> indices = new List<int>();
        List<int> remaining = new List<int>();
        for (int i = 0; i < poly.Count; i++)
            remaining.Add(i);

        int safety = 0;
        while (remaining.Count > 3 && safety++ < 1000)
        {
            bool earFound = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int prev = remaining[(i - 1 + remaining.Count) % remaining.Count];
                int curr = remaining[i];
                int next = remaining[(i + 1) % remaining.Count];

                Vector3 a = poly[prev];
                Vector3 b = poly[curr];
                Vector3 c = poly[next];

                if (isConvexTriangle(a, b, c)) {
                    bool contains = false;
                    for (int j = 0; j < remaining.Count; j++) {
                        int vi = remaining[j];
                        if (vi == prev || vi == curr || vi == next) continue;
                        if (isPointInTriangle(poly[vi], a, b, c)) {
                            contains = true;
                            break;
                        }
                    }

                    if (!contains) {
                        indices.Add(prev);
                        indices.Add(curr);
                        indices.Add(next);
                        remaining.RemoveAt(i);
                        earFound = true;
                        break;
                    }
                }
            }

            if (!earFound) {
                break;
            }
        }

        if (remaining.Count == 3) {
            indices.Add(remaining[0]);
            indices.Add(remaining[1]);
            indices.Add(remaining[2]);
        }

        return indices;
    }
    
    public static Mesh buildFromTwoLists(List<Vector3> bottomPoints, List<Vector3> topPoints, bool closeRing = true) {

        if (bottomPoints == null || topPoints == null || bottomPoints.Count != topPoints.Count || bottomPoints.Count < 2) {
            return null;
        }

        Mesh mesh = new Mesh();

        int count = closeRing? bottomPoints.Count + 1 : bottomPoints.Count;
        Vector3[] vertices = new Vector3[count * 2];
        int[] triangles = new int[(count - 1) * 6];
        Vector2[] uvs = new Vector2[count * 2];

        var totalD = 0.0f;
        var estimatedTotalDistance = count * .3f;
   
        for (int i = 0; i < count; i++) {
            var curr = bottomPoints[i % bottomPoints.Count];
            var next = bottomPoints[(i+1) % bottomPoints.Count];
            
            vertices[i * 2] = bottomPoints[i % bottomPoints.Count];
            vertices[i * 2 + 1] = topPoints[i % bottomPoints.Count];

            float t = (float)i / (count - 1);
            
            uvs[i * 2] = new Vector2(totalD / estimatedTotalDistance, 0);
            uvs[i * 2 + 1] = new Vector2(totalD / estimatedTotalDistance, 1);
            
            totalD += Vector3.Distance(curr, next);
            
        }

        for (int i = 0; i < count - 1; i++) {
            int idx = i * 6;
            int vIdx = i * 2;

            triangles[idx] = vIdx;
            triangles[idx + 1] = vIdx + 2;
            triangles[idx + 2] = vIdx + 1;
            
            triangles[idx + 3] = vIdx + 1;
            triangles[idx + 4] = vIdx + 2;
            triangles[idx + 5] = vIdx + 3;
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uvs;

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }
    
    public static Mesh buildFromPolygons(List<List<Vector3>> polygons) {
        if (polygons == null || polygons.Count == 0)
            return null;

        Mesh mesh = new Mesh();

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();

        int vertexOffset = 0;
        float uvScale = 1;
        
        foreach (var poly in polygons) {
            if (poly.Count >= 3) {
                vertices.AddRange(poly);
                
                Vector3 p0 = poly[0];
                Vector3 p1 = poly[1];
                Vector3 right = (p1 - p0).normalized; 
                Vector3 up = Vector3.Cross(Vector3.up, right);
                
                foreach (var p in poly) {
                    Vector3 local = p - p0;
                    float u = Vector3.Dot(local, right) / uvScale;
                    float v = Vector3.Dot(local, up) / uvScale;
                    uvs.Add(new Vector2(u, v));
                }
                
                var tris = triangulatePolygon(poly);
                for (int i = 0; i < tris.Count; i++) {
                    tris[i] += vertexOffset;
                }
                triangles.AddRange(tris);
                vertexOffset += poly.Count;
            }
        }

        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.uv = uvs.ToArray();

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }
    
    public static Mesh buildFromPolygon(List<Vector3> polygon) {
        if (polygon == null || polygon.Count == 0)
            return null;

        Mesh mesh = new Mesh();

        List<Vector3> vertices = new List<Vector3>(polygon);
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();
        
        float uvScale = 1;

        if (polygon.Count >= 3) {
            Vector3 p0 = polygon[0];
            Vector3 p1 = polygon[1];
            Vector3 right = (p1 - p0).normalized; 
            Vector3 up = Vector3.Cross(Vector3.up, right);
            
            foreach (var p in polygon) {
                Vector3 local = p - p0;
                float u = Vector3.Dot(local, right) / uvScale;
                float v = Vector3.Dot(local, up) / uvScale;
                uvs.Add(new Vector2(u, v));
            }
            
            var tris = triangulatePolygon(polygon);
            triangles = new List<int>(tris);

        }

        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.uv = uvs.ToArray();

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }
}
    

