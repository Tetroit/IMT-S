using System.Collections.Generic;
using UnityEngine;

namespace ProceduralGeneration.ImageProcessing
{

/// <summary>
/// Attach to an empty GameObject. Set imagePath to a segmented PNG on disk
/// and roadColor to whatever color represents roads in that segmentation map.
/// Press Play (or call Build() from your own code) to extract the graph.
/// </summary>
public class RoadNetworkBuilder : MonoBehaviour
{
    [Header("Input")]
    [Tooltip("Path relative to Assets to the segmented PNG, e.g. for ../Assets/data/segmentation.png provide \"data/segmentation.png\"")]
    public string imagePath;

    [Tooltip("The color that represents 'road' pixels in the segmentation map.")]
    public Color roadColor = Color.white;

    [Range(0f, 1f)]
    public float colorTolerance = 0.15f;

    [Header("Simplification")]
    [Tooltip("Douglas-Peucker epsilon in source pixels. Higher = straighter/fewer points.")]
    public float simplifyEpsilon = 1.5f;

    [Header("Cleanup")]
    [Tooltip("Dangling dead-end edges shorter than this (source pixels) are pruned as skeletonization noise. 0 disables.")]
    public float minSpurLength = 4f;
    [Tooltip("Some loose segments are redundant and will be pruned if their total length of edges is smaller to keep the graph clean")]
    public float minSubgraphSize = 10f;

    [Tooltip("Junction nodes closer than this (source pixels) are merged into one, collapsing dense intersection clusters. 0 disables.")]
    public float junctionMergeRadius = 3f;

    [Tooltip("Minimum 0-1 match score required to bridge two dangling road ends across a gap (based on their distance, how close each lands to the other's straight-line extension, and how well their directions align). 0 disables reconnection.")]
    [Range(0f, 1f)]
    public float reconnectMinScore = 0f;
    [Tooltip("How large can bridged gaps between roads be")]
    [Min(0f)]
    public float reconnectMaxDistanceFac = 16f;

    [Header("Smoothing")]
    [Tooltip("Number of Chaikin corner-cutting passes applied to each road. 0 = no smoothing (straight simplified segments).")]
    [Range(0, 4)]
    public int smoothingIterations = 2;

    [Tooltip("Chaikin cut ratio per iteration. Higher = more rounded corners.")]
    [Range(0.01f, 0.49f)]
    public float smoothingStrength = 0.25f;

    [Header("Placement")]
    [Tooltip("Scale applied when converting pixel coordinates to world units.")]
    public float pixelToWorldScale = 0.1f;

    [Tooltip("If true, spawns a LineRenderer per edge at runtime.")]
    public bool drawWithLineRenderers = true;
    [Tooltip("This will lag a lot")]
    public bool drawGizmos = false;

    public Material lineMaterial;
    public float lineWidth = 0.15f;

    public RoadGraph Graph { get; private set; }

    private readonly List<LineRenderer> _spawnedLines = new List<LineRenderer>();

    private void Start()
    {
        Build();
    }

    [ContextMenu("Build Road Graph")]
    public void Build()
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            Debug.LogError("RoadNetworkBuilder: imagePath is empty.");
            return;
        }

        Graph = RoadGraphExtractor.ExtractFromPng(
            $"{Application.dataPath}/{imagePath}",
            roadColor,
            colorTolerance,
            simplifyEpsilon,
            pixelToWorldScale,
            minSpurLength,
            junctionMergeRadius,
            smoothingIterations,
            smoothingStrength,
            reconnectMinScore,
            reconnectMaxDistanceFac,
            minSubgraphSize);

        Debug.Log($"RoadNetworkBuilder: extracted {Graph.Nodes.Count} nodes, {Graph.Edges.Count} edges.");

        if (drawWithLineRenderers)
            SpawnLineRenderers();
    }

    private void SpawnLineRenderers()
    {
        foreach (var lr in _spawnedLines)
            if (lr != null)
            {
                if (Application.isPlaying)
                    Destroy(lr.gameObject);
                else
                    DestroyImmediate(lr.gameObject);
            }
        _spawnedLines.Clear();

        foreach (var edge in Graph.Edges)
        {
            var go = new GameObject($"Edge_{edge.Id}");
            go.transform.SetParent(transform, false);

            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = edge.Points.Count;
            for (int i = 0; i < edge.Points.Count; i++)
            {
                Vector2 p = edge.Points[i];
                lr.SetPosition(i, transform.TransformPoint(new Vector3(p.x, 0f, p.y)));
            }

            lr.widthMultiplier = lineWidth;
            lr.useWorldSpace = true;
            if (lineMaterial != null) lr.material = lineMaterial;

            _spawnedLines.Add(lr);
        }
    }

    // Editor-time visualization even without pressing Play, once Graph has been built once.
    private void OnDrawGizmos()
    {
        if (Graph == null) return;
        if (!drawGizmos) return;

        Gizmos.color = Color.yellow;
        foreach (var edge in Graph.Edges)
        {
            for (int i = 0; i < edge.Points.Count - 1; i++)
            {
                Vector2 a = edge.Points[i];
                Vector2 b = edge.Points[i + 1];
                Gizmos.DrawLine(
                    transform.TransformPoint(new Vector3(a.x, 0f, a.y)),
                    transform.TransformPoint(new Vector3(b.x, 0f, b.y)));
            }
        }

        Gizmos.color = Color.red;
        foreach (var node in Graph.Nodes)
        {
            Vector3 pos = transform.TransformPoint(new Vector3(node.Position.x, 0f, node.Position.y));
            Gizmos.DrawSphere(pos, lineWidth * 2f);
        }
    }
}
}