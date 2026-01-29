// LocationData.cs
using UnityEngine;

/// <summary>
/// Represents a physical location in the world that can hold an item
/// and provides a navigation target point.
/// </summary>
public class LocationData : MonoBehaviour
{
    [Header("Location Info")]

    [Tooltip("Codename identifier for this location (ex: C-33, CT, UA5W).")]
    [SerializeField] private string locationCodeName = "";

    [Tooltip("The world object agents should navigate to. If null, this GameObject is used.")]
    [SerializeField] private GameObject targetObject;

    [Tooltip("Optional floor index for multi-floor logic.")]
    [SerializeField] private int floor = 0;

    [Header("Item Slot")]
    [Tooltip("Optional item currently held at this location.")]
    [SerializeField] private GameObject targetItem;

    private void Awake()
    {
        if (targetObject == null)
            targetObject = gameObject;
    }

    private void Start()
    {
        // Register with manager (non-fatal if absent).
#if UNITY_2022_2_OR_NEWER
        ToyFleetManager manager = FindFirstObjectByType<ToyFleetManager>();
#else
        ToyFleetManager manager = FindObjectOfType<ToyFleetManager>();
#endif
        if (manager != null)
        {
            manager.AddLocation(this);
        }
        else
        {
            Debug.LogWarning($"[LocationData] No ToyFleetManager found in scene for '{name}'.");
        }
    }

    // ------------------------------------------------------------------
    // Methods ToyFleetManager already calls (keep these EXACT signatures)
    // ------------------------------------------------------------------

    public string GetLocationCodeName() => locationCodeName;

    public GameObject GetTargetObject()
    {
        if (targetObject == null) targetObject = gameObject;
        return targetObject;
    }

    public GameObject GetItem() => targetItem;

    public void SetItem(GameObject newItem) => targetItem = newItem;

    // Optional convenience (doesn't break anything)
    public int GetFloor() => floor;
}
