using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NavigationMapper
/// - Generates a NavMeshPath from point A to point B
/// - Returns the path + total path length (meters)
///
/// Caller decides how to convert distance to time (speed, accel, etc).
/// </summary>
[DisallowMultipleComponent]
public sealed class NavigationMapper : MonoBehaviour
{
    [SerializeField] private bool samplePointsToNavMesh = true;
    [SerializeField] private float sampleRadius = 2.0f;
    [SerializeField] private int defaultAreaMask = NavMesh.AllAreas;

    /// <summary>
    /// Generates a NavMeshPath from A->B.
    ///
    /// Parameters are caller-controlled:
    /// - areaMask: which NavMesh areas are allowed (pass agent.areaMask if you have an agent)
    /// - sampleToNavMesh/sampleRadius: optionally project points onto NavMesh first
    ///
    /// Returns true if a complete path was found.
    /// </summary>
    public bool TryGetPath(
        Vector3 pointA,
        Vector3 pointB,
        out NavMeshPath path,
        out float pathLengthMeters,
        int? areaMaskOverride = null,
        bool? sampleToNavMeshOverride = null,
        float? sampleRadiusOverride = null)
    {
        path = new NavMeshPath();
        pathLengthMeters = 0f;

        int mask = areaMaskOverride ?? defaultAreaMask;
        bool doSample = sampleToNavMeshOverride ?? samplePointsToNavMesh;
        float radius = sampleRadiusOverride ?? sampleRadius;

        if (doSample)
        {
            if (!TrySample(pointA, radius, mask, out pointA)) return false;
            if (!TrySample(pointB, radius, mask, out pointB)) return false;
        }

        bool ok = NavMesh.CalculatePath(pointA, pointB, mask, path);
        if (!ok || path.status != NavMeshPathStatus.PathComplete) return false;

        pathLengthMeters = ComputePathLength(path);
        return true;
    }

    /// <summary>
    /// Convenience overload if you already have a NavMeshAgent (uses agent.areaMask).
    /// </summary>
    public bool TryGetPath(
        Vector3 pointA,
        Vector3 pointB,
        out NavMeshPath path,
        out float pathLengthMeters,
        NavMeshAgent agent,
        bool? sampleToNavMeshOverride = null,
        float? sampleRadiusOverride = null)
    {
        int mask = (agent != null) ? agent.areaMask : defaultAreaMask;
        return TryGetPath(pointA, pointB, out path, out pathLengthMeters, mask, sampleToNavMeshOverride, sampleRadiusOverride);
    }

    /// <summary>
    /// Returns the NavMesh path length (sum of segment lengths).
    /// </summary>
    public static float ComputePathLength(NavMeshPath path)
    {
        if (path == null) return 0f;

        Vector3[] corners = path.corners;
        if (corners == null || corners.Length < 2) return 0f;

        float length = 0f;
        for (int i = 1; i < corners.Length; i++)
            length += Vector3.Distance(corners[i - 1], corners[i]);

        return length;
    }

    /// <summary>
    /// Samples a point onto the NavMesh. Returns false if no nearby NavMesh found.
    /// </summary>
    public static bool TrySample(Vector3 point, float radius, int mask, out Vector3 sampled)
    {
        if (NavMesh.SamplePosition(point, out NavMeshHit hit, radius, mask))
        {
            sampled = hit.position;
            return true;
        }

        sampled = point;
        return false;
    }

#if UNITY_EDITOR
    [ContextMenu("Quick Test (A=this pos, B=this pos + forward*5)")]
    private void QuickTest()
    {
        Vector3 a = transform.position;
        Vector3 b = transform.position + transform.forward * 5f;

        bool ok = TryGetPath(a, b, out NavMeshPath p, out float d);

        Debug.Log(ok
            ? $"[NavigationMapper] Path corners={p.corners.Length}, length={d:F2}m"
            : "[NavigationMapper] No complete path found.");
    }
#endif
}
