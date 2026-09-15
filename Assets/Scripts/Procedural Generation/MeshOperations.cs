using System.Collections.Generic;
using UnityEngine;

namespace ProceduralGeneration
{
    public static class MeshOperations
    {
        public static Mesh CreateMesh(Vector3[] points, Vector3[] normals, Vector2[] uvs)
        {
            Mesh result = new Mesh();
            result.vertices = points;
            result.normals = normals;
            result.uv = uvs;

            return result;
        }

        public static void AddQuad(
            IReadOnlyList<Vector3> points, 
            List<Vector3> verts, 
            List<int> tris, 
            List<Vector3> normals = null, 
            List<Vector2> uvs = null)
        {
            int next = verts.Count;

            verts.AddRange(points);

            tris.Add(next + 0);
            tris.Add(next + 1);
            tris.Add(next + 2);

            tris.Add(next + 0);
            tris.Add(next + 2);
            tris.Add(next + 3);

            if (normals != null)
            {
                Vector3 normal = Vector3.Cross(
                    points[1] - points[0],
                    points[2] - points[0]
                ).normalized;

                normals.Add(normal);
                normals.Add(normal);
                normals.Add(normal);
                normals.Add(normal);
            }

            if (uvs != null)
            {
                uvs.Add(new Vector2(0, 0));
                uvs.Add(new Vector2(1, 0));
                uvs.Add(new Vector2(1, 1));
                uvs.Add(new Vector2(0, 1));
            }
        }
    }
}