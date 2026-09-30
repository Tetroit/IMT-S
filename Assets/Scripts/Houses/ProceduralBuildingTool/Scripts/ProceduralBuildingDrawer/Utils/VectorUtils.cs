using UnityEngine;

public static class VectorUtils {
   public const float EPS = 1e-6f;
    
    public static Vector2 to2(Vector3 v) => new Vector2(v.x, v.z);
    public static Vector3 to3(Vector2 v, float y) => new Vector3(v.x, y, v.y);
    
    static float crossProduct(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    
    public static (bool hit, Vector2 hitPos) segmentsIntersect(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2, float margin = 0) {
        Vector2 r = a2 - a1;
        Vector2 s = b2 - b1;

        float rxs = crossProduct(r, s);
        float qpxr = crossProduct(b1 - a1, r);

        if (Mathf.Abs(rxs) < EPS && Mathf.Abs(qpxr) < EPS) {
            return (false, Vector2.zero);
        }

        if (Mathf.Abs(rxs) < EPS)
            return (false, Vector2.zero); 

        float t = crossProduct(b1 - a1, s) / rxs;
        float u = crossProduct(b1 - a1, r) / rxs;

        if (t >= 0f && t <= 1f && u >= 0f && u <= 1f) {
            Vector2 hit = a1 + r * t;
            Vector2 dirA = r.normalized;
            Vector2 shiftedHit = hit - dirA * margin;
            return (true, shiftedHit);
        }

        return (false, Vector2.zero);
    }
    
    public static Vector2 quantize(Vector2 v, float eps) {
        float rx = Mathf.Round(v.x / eps) * eps;
        float ry = Mathf.Round(v.y / eps) * eps;
        return new Vector2(rx, ry);
    }
}
