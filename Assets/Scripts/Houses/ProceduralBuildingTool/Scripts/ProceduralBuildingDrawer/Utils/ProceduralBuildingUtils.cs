using System.Collections.Generic;
using UnityEngine;

public static class ProceduralBuildingUtils {
    
    public static float lineLenght(List<Vector3> input) {
        if (input == null || input.Count <= 1) 
            return 0;

        float len = 0;
        for (int i = 1; i < input.Count; i++) {
            Vector3 a = input[i - 1];
            Vector3 b = input[i];
            len += Vector3.Distance(a, b);
        }
        return len;
    }

    public static (float total, List<float> partials) computePartialLengths(List<Vector3> input) {
        float total = 0f;
        List<float> partials = new List<float>();
        for (int i = 1; i < input.Count; i++) {
            float len = Vector3.Distance(input[i - 1], input[i]);
            partials.Add(len);
            total += len;
        }
        return (total, partials);
    }
    
    public static List<Vector3> simpleResampleLinear(List<Vector3> input, int numPoints) {
        var outPts = new List<Vector3>();
        if (input == null || input.Count < 2 || numPoints < 2)
            return outPts;

        Vector3 start = input[0];
        Vector3 end = input[^1];
    
        for (int i = 0; i < numPoints; i++) {
            float t = i / (float)(numPoints - 1);
            Vector3 p = Vector3.Lerp(start, end, t);
            outPts.Add(p);
        }

        return outPts;
    }
    
    public static List<Vector3> resampleByCount(List<Vector3> input, int numPoints) {
        var outPts = new List<Vector3>();
        if (input == null || input.Count < 2 || numPoints < 2)
            return outPts;
        
        (float totalLen, List<float> segLens) = computePartialLengths(input);

        float step = totalLen / (numPoints - 1);
        outPts.Add(input[0]);
        float distAccum = 0f;
        int segIdx = 0;

        for (int i = 1; i < numPoints - 1; i++) {
            float targetDist = i * step;

            while (segIdx < segLens.Count - 1 && distAccum + segLens[segIdx] < targetDist)
                distAccum += segLens[segIdx++];

            float t = (targetDist - distAccum) / segLens[segIdx];
            Vector3 p = Vector3.Lerp(input[segIdx], input[segIdx + 1], t);
            outPts.Add(p);
        }

        outPts.Add(input[input.Count - 1]);
        return outPts;
    }

    public static List<Vector3> resamplePolyline(List<Vector3> input, float step) {
        var outPts = new List<Vector3>();
        if (input == null || input.Count == 0) 
            return outPts;
        
        outPts.Add(input[0]);
        float acc = 0f;
        for (int i = 1; i < input.Count; i++) {
            Vector3 a = input[i - 1];
            Vector3 b = input[i];
            float segLen = Vector3.Distance(a, b);
            if (segLen == 0) 
                continue;
            
            Vector3 dir = (b - a) / segLen;
            float t = step - acc;
            
            while (t <= segLen) {
                Vector3 p = a + dir * t;
                outPts.Add(p);
                segLen -= t;
                a = p;
                t = step;
            }
            acc = segLen; 
        }
        
        if(acc >= step)    
            outPts.Add(input[input.Count - 1]);
        
        return outPts;
    }
    
    public static void moveLine(List<Vector3> line, int movedIndex, Vector3 newPosition) {
        Vector3 oldPos = line[movedIndex];
        Vector3 delta = newPosition - oldPos;
        
        for (int i = 0; i < line.Count; i++) {
            float t = ((float)i / (line.Count - 1));
            if (movedIndex == 0) {
                t =  1f - t;
            }
            line[i] += delta * t;                        
        }
    }
    
    public static List<Vector2> computeNormals(List<Vector3> poly) {
        var normals = new List<Vector2>(poly.Count);
        if (poly.Count < 2) {
            foreach (var p in poly) 
                normals.Add(Vector2.up);
            return normals;
        }

        for (int i = 0; i < poly.Count; i++) {
            Vector2 prev, next;
            Vector2 pi = VectorUtils.to2(poly[i]);
            if (i == 0) 
                prev = VectorUtils.to2(poly[i + 1]) - pi;
            else 
                prev = pi - VectorUtils.to2(poly[i - 1]);

            if (i == poly.Count - 1) 
                next = pi - VectorUtils.to2(poly[i - 1]);
            else 
                next = VectorUtils.to2(poly[i + 1]) - pi;

            prev.Normalize();
            next.Normalize();


            Vector2 tangent = (prev + next).normalized;
            if (tangent.magnitude < 0.0001f)
                tangent = new Vector2(-prev.y, prev.x);
            
            Vector2 normal = new Vector2(-tangent.y, tangent.x).normalized;
            normals.Add(normal);
        }
        return normals;
    }
    
    public static bool lineLineIntersection(Vector2 p1, Vector2 d1, Vector2 p2, Vector2 d2, out Vector2 hit) {
        hit = Vector2.zero;
        float denom = d1.x * d2.y - d1.y * d2.x;
        if (Mathf.Abs(denom) < VectorUtils.EPS) 
            return false; 
        
        Vector2 dp = p2 - p1;
        float t = (dp.x * d2.y - dp.y * d2.x) / denom;
        hit = p1 + d1 * t;
        return true;
    }

    public static List<Vector3> offsetPolyline(List<Vector3> poly, float offsetDistance) {
        var normals = computeNormals(poly);
        float y = poly.Count > 0 ? poly[0].y : 0f;
        var offsetPts = new List<Vector3>(poly.Count);

        for (int i = 0; i < poly.Count; i++) {
            Vector2 n = normals[i];
            Vector2 pi = VectorUtils.to2(poly[i]);
            Vector2 off = pi + n * offsetDistance;
            offsetPts.Add(VectorUtils.to3(off, y));
        }

        var refined = new List<Vector3>();
        for (int i = 0; i < poly.Count; i++) {
            if (i == 0 || i == poly.Count - 1) {
                refined.Add(offsetPts[i]);
                continue;
            }
            
            Vector2 a1 = VectorUtils.to2(offsetPts[i - 1]);
            Vector2 a2 = VectorUtils.to2(offsetPts[i]);
            Vector2 d1 = (a2 - a1).normalized;

            Vector2 b1 = VectorUtils.to2(offsetPts[i]);
            Vector2 b2 = VectorUtils.to2(offsetPts[i + 1]);
            Vector2 d2 = (b2 - b1).normalized;

            if (lineLineIntersection(a1, d1, b1, d2, out Vector2 hit)) {
                refined.Add(VectorUtils.to3(hit, offsetPts[i].y));
            }
            else {
                Vector2 mid = (VectorUtils.to2(offsetPts[i]) + VectorUtils.to2(offsetPts[i + 1]) + VectorUtils.to2(offsetPts[i - 1])) / 3f;
                refined.Add(VectorUtils.to3(mid, offsetPts[i].y));
            }
        }
        return refined;
    }
    
    public static List<Vector3> buildCap(Vector3 prev, Vector3 next) {
        
        var cap = new List<Vector3>();
        cap.Add(prev);
        cap.Add(next);

        return cap;
    }
    
    public static (List<List<Vector3>> sections, List<List<Vector3>> caps) buildSectionsWithCaps(List<Vector3> left, List<Vector3> right, float angleThreshold = 30, float distance = 1f) {
        
        if (left.Count < 2 || right.Count < 2)
            return (new List<List<Vector3>>(), new List<List<Vector3>>());
        
        var poly = new List<Vector3>();
        
        float verticalDist = Vector3.Distance(left[0], right[0]);
        int verticalSegmentsStart = Mathf.Max(3, Mathf.CeilToInt(verticalDist / distance));

        for (int i = 0; i <= verticalSegmentsStart; i++) {
            float t = i / (float)verticalSegmentsStart;
            poly.Add(Vector3.Lerp(right[0], left[0], t));

        }
        
        for (int i = 1; i < left.Count-1; i++)
            poly.Add(left[i]);
        
        verticalDist = Vector3.Distance(left[^1], right[^1]);
        int verticalSegmentsEnd = Mathf.Max(3, Mathf.CeilToInt(verticalDist / distance));

        for (int i = 0; i <= verticalSegmentsEnd; i++) {
            float t = i / (float)verticalSegmentsEnd;
            poly.Add(Vector3.Lerp(left[^1], right[^1], t));
        }

        for (int i = right.Count - 2; i >= 1; i--)
            poly.Add(right[i]);
        
        
        var controlPoints = new HashSet<Vector3> { left[0], left[^1], right[0], right[^1] };
        
        
        return buildSectionsWithCaps(poly, controlPoints, angleThreshold, distance);
    }

    static (List<List<Vector3>> sections, List<List<Vector3>> caps)  buildSectionsWithCaps(List<Vector3> poly, HashSet<Vector3> controlPoints, float angleThreshold = 30, float distance = 1f) {
        var sections = new List<List<Vector3>>();
        var caps = new List<List<Vector3>>();
        int startIdx = 0;
        Vector3 lastCapAdded = poly[0];
        
        bool missingFirstCap = false;
        for (int i = 0; i < poly.Count; i++) {
            Vector3 prev = poly[(i - 1 + poly.Count) % poly.Count];
            Vector3 curr = poly[i];
            Vector3 next = poly[(i + 1) % poly.Count];
        
            Vector3 dir1 = (prev - curr).normalized;
            Vector3 dir2 = (curr - next).normalized;
            
            float angle = Vector3.Angle(dir1, dir2);
            float distPrevToNext = Vector3.Distance(prev, next);
            
            bool isControlPoint = controlPoints?.Contains(curr) ?? false;
            bool addCap = angle > angleThreshold;
            bool splitSection = addCap || isControlPoint;
            
            if (splitSection) {
                var section = poly.GetRange(startIdx, i - startIdx);
                if (!addCap) {
                    section.Add(curr);
                    if (i == 0) {
                        missingFirstCap = true;
                    }
                }
                if (section.Count > 1) {
                    sections.Add(section);
                }
                startIdx = i;

                if (addCap) {
                    startIdx += 1;
                    
                    if (curr == lastCapAdded && i != 0) {
                        prev = lastCapAdded;
                    }

                    var cap = buildCap(prev, next);
                    caps.Add(cap);
                    lastCapAdded = next;
                }
            }
        }

        if (startIdx < poly.Count - 1) {
            var lastSection = poly.GetRange(startIdx, poly.Count - startIdx);
            if (missingFirstCap) {
                lastSection.Add(poly[0]);
            }
            sections.Add(lastSection);
        }

        return (sections, caps);
    }
    
    
    public static (int, int, Vector2)? findAnySelfIntersection(List<Vector3> poly, float margin) {
        int n = poly.Count;
        if (n < 4) return null;
        for (int i = 0; i < n - 1; i++) {
            Vector2 a1 = VectorUtils.to2(poly[i]);
            Vector2 a2 = VectorUtils.to2(poly[i + 1]);
            for (int j = i + 2; j < n - 1; j++) {
                if (i == 0 && j == n - 2) 
                    continue;
                
                Vector2 b1 = VectorUtils.to2(poly[j]);
                Vector2 b2 = VectorUtils.to2(poly[j + 1]);
                var intersection = VectorUtils.segmentsIntersect(a1, a2, b1, b2, margin);
                if (intersection.hit)
                    return (i, j, intersection.hitPos);
            }
        }
        return null;
    }
    
    public static List<Vector3> removeFirstLoop(List<Vector3> poly, float margin) {
        var f = findAnySelfIntersection(poly, margin);
        if (f == null) 
            return poly;
        var (i, j, hit) = f.Value;
        
        var outp = new List<Vector3>();
        for (int k = 0; k <= i; k++) {
            outp.Add(poly[k]);
        }
        outp.Add(VectorUtils.to3(hit, poly[0].y));
        for (int k = j + 1; k < poly.Count; k++) {
            outp.Add(poly[k]);
        }
        return outp;
    }
    
    public static List<Vector3> repairSelfIntersections(List<Vector3> poly, float margin = 0,  int maxIter = 10) {
        var cur = new List<Vector3>(poly);
        for (int it = 0; it < maxIter; it++) {
            var newp = removeFirstLoop(cur, margin);
            if (newp.Count == cur.Count) 
                break;
            cur = newp;
        }
        return cur;
    }
    
    public static Vector3 sampleAlong(List<Vector3> section, float totalLen, float distance, bool verbose = false) {
        if (distance <= 0) {
            if(verbose)
                Debug.Log("id : 0");
            return section[0];
        }
        if (distance >= totalLen) {
            if(verbose)
                Debug.Log("id : last1");
            return section[^1];
        }
        
        float partialLen = 0;
        for (int i = 1; i < section.Count; i++) {
            var part = Vector3.Distance(section[i], section[i - 1]);
            partialLen += part;
            if (partialLen >= distance) {
                float prevPartial = partialLen - part;
                float t = (distance - prevPartial) / (partialLen - prevPartial);
                if(verbose)
                    Debug.Log("id : " + (t<.5? i-1 : i) + " t: " + t);
                return Vector3.Lerp(section[i - 1], section[i], t);
            }
            
        }
       
        if (verbose) {
            Debug.Log("PP " + partialLen + " : " + totalLen + " : " + distance);
            Debug.Log("id : last2 ");
        }
        var endPoint = section[^2];
        var d = distance - partialLen;
        var dir = (section[^1] - section[^2]).normalized;
        endPoint += dir * d;
        return endPoint;
    }
    
    public static (List<List<Vector3>> newSections, List<List<Vector3>> newCaps) offsetPolygonSmooth(List<List<Vector3>> sections, float offset) {
        List<int> sectionLengths = new List<int>();
        List<Vector3> poly = new List<Vector3>();
        for (int i = 0; i < sections.Count; i++) {
            var lineSection = sections[i];
            poly.AddRange(lineSection);
            sectionLengths.Add(lineSection.Count);
        }

        int n = poly.Count;
        var result = new List<Vector3>(n);
        float minCapSize = .5f;

        for (int i = 0; i < n; i++) {
            Vector3 prev = poly[(i - 1 + n) % n];
            Vector3 curr = poly[i];
            Vector3 next = poly[(i + 1) % n];

            Vector3 dirPrev = (curr - prev).normalized;
            Vector3 dirNext = (next - curr).normalized;
            
            Vector3 nPrev = Vector3.Cross(Vector3.up, dirPrev).normalized;
            Vector3 nNext = Vector3.Cross(Vector3.up, dirNext).normalized;

   
            Vector3 nAvg = ((nPrev + nNext)/2.0f).normalized;
            
            if (i > 0) {
                var newPoint = curr - nAvg * offset;
                var avg = (result[i - 1] + newPoint) / 2.0f;
                if (Vector3.Distance(result[i - 1], newPoint) < minCapSize) {
                    result[i - 1] = avg;
                    result.Add(avg);
                }
                else {
                    result.Add(newPoint);
                }
            }
            else {
                result.Add(curr - nAvg * offset);
            }
        }

        result = repairSelfIntersections(result);
        
        List<List<Vector3>> newSections = new List<List<Vector3>>();
        List<List<Vector3>> newCaps = new List<List<Vector3>>();
        
        int index = 0;
        int length = 0;
        for (int i = 0; i < sectionLengths.Count; i++) {
            length = sectionLengths[i];
            if (result.Count > index + length) {
                var tmpSection = result.GetRange(index, length);
                tmpSection = resampleByCount(tmpSection, tmpSection.Count);
                newSections.Add(tmpSection);
                index += (length);
            } else {
                var tmpSection = new List<Vector3>();
                for (int j = index; j < result.Count; j++) {
                    tmpSection.Add(result[j]);
                }
                tmpSection = resampleByCount(tmpSection, tmpSection.Count);
                newSections.Add(tmpSection);
            }
        }

        for (int i = 0; i < newSections.Count-1; i++) {
            var a = newSections[i][^1];
            var b = newSections[i+1][0];
            
            Vector3 dirA = (newSections[i][^2] - newSections[i][^1]).normalized;
            Vector3 dirB = (newSections[i+1][0] - newSections[i+1][1]).normalized;
          
            float angle = Vector3.Angle(dirA, dirB);

            if (Vector3.Distance(a, b) > minCapSize * 1.5f ) {
                var tmpCap = new List<Vector3> { a, b };
                newCaps.Add(tmpCap);
            } else {
                newSections[i+1].RemoveAt(0);
                newSections[i+1].Insert(0, a);
                newSections[i+1] = resampleByCount(newSections[i+1], newSections[i+1].Count);
            }
        }
        
        var lastA = newSections[^1][^1];
        var lastB = newSections[0][0];
        var lastCap = new List<Vector3> { lastA, lastB };
        newCaps.Add(lastCap);
        
        return (newSections, newCaps);
    }

    public static List<Vector3> getCenterLine(List<Vector3> left, List<Vector3> right, bool removeFirstAndLast = true) {
        var centers = new List<Vector3>();
        if (left.Count != right.Count) {
            right = resampleByCount(right, left.Count);
        }

        int startIndex = 0;
        int endIndex = left.Count;
        
        
        if (removeFirstAndLast) {
            startIndex = 3;
            endIndex = left.Count - 3;
        }

        if (endIndex < startIndex) {
            startIndex = 0;
            endIndex = left.Count;        }

        for (int i = startIndex; i < endIndex; i++) {
            var pointL = left[i];
            var pointR = right[i];
            centers.Add((pointL + pointR) / 2.0f);
        }
        return centers;
    }

    public static List<Vector3> mergeSections(List<List<Vector3>> sections) {
        List<Vector3> poly = new List<Vector3>();
        for (int i = 0; i < sections.Count; i++) {
            var lineSection = sections[i];
            poly.AddRange(lineSection);
        }
        return poly;
    }
    
    public static List<Vector3> getSectionsAnchorsPoints(List<List<Vector3>> sections) {
        List<Vector3> poly = new List<Vector3>();
        for (int i = 0; i < sections.Count; i++) {
            var lineSection = sections[i];
            poly.Add(lineSection[0]);
            poly.Add(lineSection[^1]);
        }
        return poly;
    }
}
