using UnityEngine;

/// <summary>
/// Represents a physical location in the world that can hold an item
/// and provides a navigation target point.
/// </summary>
public class LocationData : MonoBehaviour
{
    [Header("Location Info")]

    [Tooltip("Codename identifier for this location (ex: C-33, CT, UA5W).")]
    [SerializeField] private string locationCodeName;

    [Tooltip("The world object agents should navigate to.")]
    [SerializeField] public GameObject targetObject;

    [SerializedField] public int floor;

    private void Start()
    {
        ToyFleetManager manager = FindObjectOfType<ToyFleetManager>();
        if (manager != null)
        {
            manager.AddLocation(this);
        }
        else
        {
            Debug.LogWarning($"[LocationData] No ToyFleetManager found in scene for '{name}'.");
        }
    }

    public string GetLocationCodeName() => locationCodeName;

    public GameObject GetTargetObject() => targetObject;

    public GameObject GetItem() => targetItem;

    public void SetItem(GameObject newItem)
    {
        targetItem = newItem;
    }
}
