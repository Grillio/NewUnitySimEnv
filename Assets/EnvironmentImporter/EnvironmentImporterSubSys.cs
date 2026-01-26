using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// EnvironmentImporterSubSys
/// --------------------------------
/// Reads mesh data and groups vertices that lie on the same Y level
/// (within a configurable tolerance).
///
/// Supports:
/// - MeshFilter (static meshes)
/// - SkinnedMeshRenderer (baked pose)
///
/// Intended use:
/// - Environment analysis
/// - Floor / slice detection
/// - Height-based clustering
/// </summary>
[DisallowMultipleComponent]
public sealed class EnvironmentImporterSubSys : MonoBehaviour
{
    [Header("Mesh Source")]
    [SerializeField] private MeshFilter meshFilter;
    [SerializeField] private SkinnedMeshRenderer skinnedMeshRenderer;

    [Header("Y-Level Grouping")]
    [Tooltip("Vertices whose Y differs by <= tolerance are treated as the same level.")]
    [SerializeField, Min(0f)] private float tolerance = 0.001f;

    [Tooltip("If true, Y values are evaluated in WORLD space.")]
    [SerializeField] private bool useWorldSpace = false;

    // Quantized Y bucket → vertex indices
    private readonly Dictionary<int, List<int>> yBuckets = new();

    // Representative Y (average) per bucket
    private readonly Dictionary<int, float> bucketRepresentativeY = new();

    private Vector3[] vertices = Array.Empty<Vector3>();

    // --------------------------------------------------
    // Public API
    // --------------------------------------------------

    [ContextMenu("Rebuild Environment Mesh Data")]
    public void Rebuild()
    {
        yBuckets.Clear();
        bucketRepresentativeY.Clear();

        Mesh mesh = GetMesh();
        if (mesh == null)
        {
            Debug.LogError("[EnvironmentImporterSubSys] No mesh found.", this);
            return;
        }

        if (skinnedMeshRenderer != null)
        {
            var baked = new Mesh();
            skinnedMeshRenderer.BakeMesh(baked);
            vertices = baked.vertices;
            DestroyImmediate(baked);
        }
        else
        {
            vertices = mesh.vertices;
        }

        if (vertices.Length == 0)
            return;

        float step = Mathf.Max(tolerance, 1e-6f);

        for (int i = 0; i < vertices.Length; i++)
        {
            float y = useWorldSpace
                ? transform.TransformPoint(vertices[i]).y
                : vertices[i].y;

            int key = Mathf.RoundToInt(y / step);

            if (!yBuckets.TryGetValue(key, out var list))
            {
                list = new List<int>(32);
                yBuckets[key] = list;
                bucketRepresentativeY[key] = y;
            }
            else
            {
                bucketRepresentativeY[key] =
                    (bucketRepresentativeY[key] * list.Count + y) / (list.Count + 1);
            }

            list.Add(i);
        }

        Debug.Log(
            $"[EnvironmentImporterSubSys] Built {yBuckets.Count} Y-level groups " +
            $"from {vertices.Length} vertices.",
            this
        );
    }

    /// <summary>
    /// Returns all Y-level groups keyed by representative Y value.
    /// </summary>
    public Dictionary<float, List<int>> GetYLevelGroups()
    {
        var result = new Dictionary<float, List<int>>(yBuckets.Count);
        foreach (var kvp in yBuckets)
            result[bucketRepresentativeY[kvp.Key]] = kvp.Value;

        return result;
    }

    /// <summary>
    /// Returns vertex indices whose Y is within tolerance of queryY.
    /// </summary>
    public List<int> GetVerticesAtY(float queryY)
    {
        if (yBuckets.Count == 0)
            Rebuild();

        float step = Mathf.Max(tolerance, 1e-6f);
        int key = Mathf.RoundToInt(queryY / step);

        var results = new List<int>(128);

        AddBucket(key - 1);
        AddBucket(key);
        AddBucket(key + 1);

        results.RemoveAll(idx =>
        {
            float y = useWorldSpace
                ? transform.TransformPoint(vertices[idx]).y
                : vertices[idx].y;

            return Mathf.Abs(y - queryY) > tolerance;
        });

        return results;

        void AddBucket(int k)
        {
            if (yBuckets.TryGetValue(k, out var list))
                results.AddRange(list);
        }
    }

    // --------------------------------------------------
    // Internals
    // --------------------------------------------------

    private Mesh GetMesh()
    {
        if (skinnedMeshRenderer != null)
            return skinnedMeshRenderer.sharedMesh;

        if (meshFilter == null)
            meshFilter = GetComponent<MeshFilter>();

        return meshFilter != null ? meshFilter.sharedMesh : null;
    }
}
