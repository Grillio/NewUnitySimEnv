using UnityEngine;

/// <summary>
/// ChargingStation
/// - Represents a location robots can reserve for charging
/// - On game start, finds all PatientTransporters in the scene
///   and registers them with ToyFleetManager:
///     • Robots   → AddChargingSpot(transporter)
///     • Humans   → AddBreakRoom(transporter)
/// </summary>
public class ChargingStation : MonoBehaviour
{
    [Header("Station State")]
    [SerializeField] private bool reserved = false;

    public bool IsReserved
    {
        get => reserved;
        set => reserved = value;
    }

    private void Start()
    {
        RegisterTransporters();
    }

    private void RegisterTransporters()
    {
        ToyFleetManager manager = ToyFleetManager.I;

        if (manager == null)
        {
            Debug.LogWarning("[ChargingStation] No ToyFleetManager found in scene.");
            return;
        }

        PatientTransporter[] transporters = FindObjectsOfType<PatientTransporter>();

        foreach (var transporter in transporters)
        {
            if (transporter == null) continue;

            if (transporter.IsRobotic)
            {
                //manager.AddChargingSpot(transporter);
                Debug.Log($"[ChargingStation] Registered ROBOT '{transporter.name}' to charging system.");
            }
            else
            {
                //manager.AddBreakRoom(transporter);
                Debug.Log($"[ChargingStation] Registered HUMAN '{transporter.name}' to break system.");
            }
        }
    }
}
