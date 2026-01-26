using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Singleton utility for calculating NavMesh paths and their total length.
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

    /// <summary>
    /// Calculates a NavMesh path between two world positions.
    /// Returns true if a complete path exists.
    /// </summary>
    public bool TryGetPath(
        Vector3 pointA,
        Vector3 pointB,
        out NavMeshPath path,
        out float pathLengthMeters,
        int areaMask = NavMesh.AllAreas,
        bool sampleToNavMesh = true,
        
        float sampleRadius = 2f)
    {
        path = new NavMeshPath();
        pathLengthMeters = 0f;

        if (sampleToNavMesh)
        {
            if (!TrySample(pointA, sampleRadius, areaMask, out pointA))
                return false;

            if (!TrySample(pointB, sampleRadius, areaMask, out pointB))
                return false;
        }

        if (!NavMesh.CalculatePath(pointA, pointB, areaMask, path))
            return false;

        if (path.status != NavMeshPathStatus.PathComplete)
            return false;

        pathLengthMeters = ComputePathLength(path);
        return true;
    }

    public bool TryGetPath(
        Vector3 pointA,
        Vector3 pointB,
        NavMeshAgent agent,
        out NavMeshPath path,
        out float pathLengthMeters,
        bool sampleToNavMesh = true,
        float sampleRadius = 2f)
    {
        int mask = agent != null ? agent.areaMask : NavMesh.AllAreas;

        return TryGetPath(pointA, pointB, out path, out pathLengthMeters, mask, sampleToNavMesh, sampleRadius);
    }

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
}
