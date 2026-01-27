using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Singleton utility for calculating NavMesh paths,
/// total length, floor (Y) difference, and a segmented waypoint list
/// spaced by a fixed step (e.g., 2m) while ALWAYS preserving corners.
/// Must exist once in the scene.
/// </summary>
public class NavMeshPathUtility : MonoBehaviour
{
    public static NavMeshPathUtility I { get; private set; }

    private void Awake()
    {
        if (I != null && I != this)
        {
            Destroy(gameObject);
            return;
        }
        I = this;
    }

    // ------------------------------------------------------------
    // Public API (as requested)
    // ------------------------------------------------------------

    /// <summary>
    /// Takes two Components (uses their transforms).
    /// Builds a segmented path: points every "step" meters along the path,
    /// but also includes every NavMesh corner even if it falls off-step.
    /// </summary>
    public bool TryGetSegmentedPath(
        Component from,
        Component to,
        out Vector3[] waypoints,
        out float totalDistanceMeters,
        out float floorDifferenceMeters,
        float stepMeters = 2f,
        int areaMask = NavMesh.AllAreas,
        bool sampleToNavMesh = true,
        float sampleRadius = 2f)
    {
        if (from == null || to == null)
        {
            waypoints = null;
            totalDistanceMeters = 0f;
            floorDifferenceMeters = 0f;
            return false;
        }

        return TryGetSegmentedPath(
            from.transform.position,
            to.transform.position,
            out waypoints,
            out totalDistanceMeters,
            out floorDifferenceMeters,
            stepMeters,
            areaMask,
            sampleToNavMesh,
            sampleRadius);
    }

    /// <summary>
    /// Takes a Transform and a Component (uses component's transform).
    /// </summary>
    public bool TryGetSegmentedPath(
        Transform from,
        Component to,
        out Vector3[] waypoints,
        out float totalDistanceMeters,
        out float floorDifferenceMeters,
        float stepMeters = 2f,
        int areaMask = NavMesh.AllAreas,
        bool sampleToNavMesh = true,
        float sampleRadius = 2f)
    {
        if (from == null || to == null)
        {
            waypoints = null;
            totalDistanceMeters = 0f;
            floorDifferenceMeters = 0f;
            return false;
        }

        return TryGetSegmentedPath(
            from.position,
            to.transform.position,
            out waypoints,
            out totalDistanceMeters,
            out floorDifferenceMeters,
            stepMeters,
            areaMask,
            sampleToNavMesh,
            sampleRadius);
    }

    /// <summary>
    /// Core version: takes two world points.
    /// Returns:
    /// - waypoints: the segmented path points to follow
    /// - totalDistanceMeters: full path length along corners
    /// - floorDifferenceMeters: |endY - startY| after optional NavMesh sampling
    /// </summary>
    public bool TryGetSegmentedPath(
        Vector3 pointA,
        Vector3 pointB,
        out Vector3[] waypoints,
        out float totalDistanceMeters,
        out float floorDifferenceMeters,
        float stepMeters = 2f,
        int areaMask = NavMesh.AllAreas,
        bool sampleToNavMesh = true,
        float sampleRadius = 2f)
    {
        waypoints = null;
        totalDistanceMeters = 0f;
        floorDifferenceMeters = 0f;

        if (stepMeters <= 0.001f)
            stepMeters = 2f;

        // Sample endpoints onto NavMesh if requested
        if (sampleToNavMesh)
        {
            if (!TrySample(pointA, sampleRadius, areaMask, out pointA))
                return false;

            if (!TrySample(pointB, sampleRadius, areaMask, out pointB))
                return false;
        }

        // Compute path
        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(pointA, pointB, areaMask, path))
            return false;

        if (path.status != NavMeshPathStatus.PathComplete)
            return false;

        // Metrics
        totalDistanceMeters = ComputePathLength(path);
        floorDifferenceMeters = Mathf.Abs(pointB.y - pointA.y);

        // Segmentation
        waypoints = BuildSegmentedWaypoints(path.corners, stepMeters);
        return waypoints != null && waypoints.Length >= 2;
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------

    public float ComputePathLength(NavMeshPath path)
    {
        if (path == null || path.corners == null || path.corners.Length < 2)
            return 0f;

        float length = 0f;
        var corners = path.corners;

        for (int i = 1; i < corners.Length; i++)
            length += Vector3.Distance(corners[i - 1], corners[i]);

        return length;
    }

    public bool TrySample(Vector3 point, float radius, int mask, out Vector3 sampled)
    {
        if (NavMesh.SamplePosition(point, out NavMeshHit hit, radius, mask))
        {
            sampled = hit.position;
            return true;
        }

        sampled = point;
        return false;
    }

    /// <summary>
    /// Returns points spaced by stepMeters along the polyline,
    /// ALWAYS including every corner and the final corner.
    /// </summary>
    private static Vector3[] BuildSegmentedWaypoints(Vector3[] corners, float stepMeters)
    {
        if (corners == null || corners.Length < 2)
            return corners;

        var pts = new List<Vector3>(corners.Length * 2);

        // Always start at the first corner
        pts.Add(corners[0]);

        for (int ci = 0; ci < corners.Length - 1; ci++)
        {
            Vector3 a = corners[ci];
            Vector3 b = corners[ci + 1];

            Vector3 ab = b - a;
            float segLen = ab.magnitude;

            if (segLen <= 0.0001f)
            {
                // Still preserve the corner if it's distinct
                AddIfNotDuplicate(pts, b);
                continue;
            }

            Vector3 dir = ab / segLen;

            // Walk forward from 'a' toward 'b' by stepMeters,
            // but do NOT skip adding 'b' (corner) at the end.
            float traveled = stepMeters;

            while (traveled < segLen)
            {
                Vector3 p = a + dir * traveled;
                AddIfNotDuplicate(pts, p);
                traveled += stepMeters;
            }

            // Always include the next corner
            AddIfNotDuplicate(pts, b);
        }

        return pts.ToArray();
    }

    private static void AddIfNotDuplicate(List<Vector3> pts, Vector3 p, float eps = 0.001f)
    {
        if (pts.Count == 0)
        {
            pts.Add(p);
            return;
        }

        Vector3 last = pts[pts.Count - 1];
        if ((last - p).sqrMagnitude > eps * eps)
            pts.Add(p);
    }
}
