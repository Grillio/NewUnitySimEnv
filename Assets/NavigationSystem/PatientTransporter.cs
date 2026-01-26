using UnityEngine;

/// <summary>
/// Simple transporter component that tracks movement speeds and task timing.
/// </summary>
public class PatientTransporter : MonoBehaviour
{
    [Header("Movement Speeds")]
    [SerializeField] private float transportationSpeed = 1.5f;

    [Header("Mounting Durations")]
    [SerializeField] private float mountDurationSeconds = 5f;
    [SerializeField] private float unmountDurationSeconds = 5f;

    [Header("Task Timing")]
    [SerializeField] private float timeToCompleteCurrentTasks = 0f;

    public float TransportationSpeed => transportationSpeed;
    public float MountDurationSeconds => mountDurationSeconds;
    public float UnmountDurationSeconds => unmountDurationSeconds;
    public float TimeToCompleteCurrentTasks => timeToCompleteCurrentTasks;

    // -----------------------------
    // NEW: last queued task dropoff
    // -----------------------------
    private bool hasLastDropoff;
    private Vector3 lastDropoffPosition;

    /// <summary>
    /// “Where will I be when my current queue is done?”
    /// If I have queued tasks, that’s the last drop-off; otherwise, it’s my current position.
    /// </summary>
    public Vector3 GetQueueFinalPosition()
    {
        return (timeToCompleteCurrentTasks > 0f && hasLastDropoff)
            ? lastDropoffPosition
            : transform.position;
    }

    /// <summary>
    /// Estimate total time (seconds) to complete a new task:
    /// queued time + (queueFinal->newStart) + mount + (newStart->newEnd) + unmount
    /// </summary>
    public float CalculateEstimatedTimeToCompleteWithReposition(
        Vector3 newStartWorld,
        Vector3 newEndWorld,
        NavMeshPathUtility nav,
        int areaMask = UnityEngine.AI.NavMesh.AllAreas)
    {
        if (transportationSpeed <= 0f) return float.PositiveInfinity;
        if (nav == null) return float.PositiveInfinity;

        Vector3 fromForReposition = GetQueueFinalPosition();

        // leg 1: finish current queue -> new task start
        if (!nav.TryGetPath(fromForReposition, newStartWorld, out _, out float repositionDist, areaMask))
            return float.PositiveInfinity;

        // leg 2: new task start -> new task end
        if (!nav.TryGetPath(newStartWorld, newEndWorld, out _, out float taskDist, areaMask))
            return float.PositiveInfinity;

        float repositionTime = repositionDist / transportationSpeed;
        float taskTime = taskDist / transportationSpeed;

        return timeToCompleteCurrentTasks
             + repositionTime
             + mountDurationSeconds
             + taskTime
             + unmountDurationSeconds;
    }

    /// <summary>
    /// Adds the new task onto the queue, INCLUDING reposition from last drop-off to new start.
    /// Also updates lastDropoffPosition to this task’s end.
    /// </summary>
    public bool AddTaskEstimateToQueueWithReposition(
        Vector3 newStartWorld,
        Vector3 newEndWorld,
        NavMeshPathUtility nav,
        int areaMask = UnityEngine.AI.NavMesh.AllAreas)
    {
        if (transportationSpeed <= 0f) return false;
        if (nav == null) return false;

        Vector3 fromForReposition = GetQueueFinalPosition();

        if (!nav.TryGetPath(fromForReposition, newStartWorld, out _, out float repositionDist, areaMask))
            return false;

        if (!nav.TryGetPath(newStartWorld, newEndWorld, out _, out float taskDist, areaMask))
            return false;

        float repositionTime = repositionDist / transportationSpeed;
        float taskTime = taskDist / transportationSpeed;

        timeToCompleteCurrentTasks += repositionTime + mountDurationSeconds + taskTime + unmountDurationSeconds;

        // update “final position” for the queue to this task’s drop-off
        lastDropoffPosition = newEndWorld;
        hasLastDropoff = true;

        return true;
    }

    public void TickTaskTimer(float deltaSeconds)
    {
        timeToCompleteCurrentTasks = Mathf.Max(0f, timeToCompleteCurrentTasks - Mathf.Max(0f, deltaSeconds));

        // Optional: when queue finishes, clear lastDropoff so GetQueueFinalPosition returns transform.position again
        if (timeToCompleteCurrentTasks <= 0f)
            hasLastDropoff = false;
    }
}
