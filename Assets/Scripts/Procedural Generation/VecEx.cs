using System;
using UnityEngine;

namespace ProceduralGeneration
{
    public static class VecEx
    {
        public static Vector2 Abs(Vector2 v)
        {
            return new Vector2(Mathf.Abs(v.x), Mathf.Abs(v.y));
        }
        public static Vector3 Abs(Vector3 v)
        {
            return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        }
        public static Vector2Int Abs(Vector2Int v)
        {
            return new Vector2Int(Mathf.Abs(v.x), Mathf.Abs(v.y));
        }
        public static Vector3Int Abs(Vector3Int v)
        {
            return new Vector3Int(Math.Abs(v.x), Math.Abs(v.y),  Math.Abs(v.z));
        }
    }
}