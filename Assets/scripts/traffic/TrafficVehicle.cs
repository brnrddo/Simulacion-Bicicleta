// ============================================================
// TrafficVehicle.cs
// Estado de un auto de trafico. NO se mueve solo: TrafficManager
// actualiza a todos los autos en un unico paso sincronizado (todos
// calculan su aceleracion con el estado del tick anterior y despues
// todos avanzan), que es como se integra correctamente un modelo de
// seguimiento vehicular.
// Va en la raiz del prefab del auto, junto con un Rigidbody y un
// BoxCollider (NO trigger) en la layer Vehicle.
// Convencion del prefab: pivot al CENTRO del auto a nivel del suelo,
// frente del auto hacia +Z.
// ============================================================

using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class TrafficVehicle : MonoBehaviour
{
    [Header("Dimensiones (m)")]
    [SerializeField] private float lengthM = 4.5f;
    [SerializeField] private float widthM = 1.8f;

    public float LengthM => lengthM;
    public float WidthM => widthM;

    // Estado de simulacion (lo escribe TrafficManager)
    public int Id { get; private set; }
    public string Label { get; private set; } = "";  // "auto#<Id>", cacheado para no generar basura por tick
    public TrafficVehicle Prefab { get; set; }       // prefab de origen, para devolverlo a su pool
    public TrafficLane Lane { get; set; }
    public TrafficLane NextLane { get; set; }
    /// <summary>Carril despues de NextLane: se elige con un paso mas de anticipacion para saber
    /// el giro antes de llegar al carril de llegada (que es corto).</summary>
    public TrafficLane NextNextLane { get; set; }
    public float S { get; set; }              // distancia del CENTRO del auto desde el inicio del carril
    public float Speed { get; set; }          // m/s
    public float Accel { get; set; }          // m/s2
    public float LateralOffset { get; set; }  // m, + = derecha (lo usaran los adelantamientos)
    public float SpeedFactor { get; set; }    // velocidad deseada / limite del carril
    public IdmParams Params;

    // Semaforo: decision en amarillo (una vez por acceso) y retardo de arranque en verde
    public StopLine YellowDecisionLine { get; set; }
    public bool YellowDecisionStop { get; set; }
    public StopLine StartHoldLine { get; set; }
    public double StartHoldUntilS { get; set; }
    public StopLine LastStopLineLeader { get; set; }  // linea que fue su lider en el ultimo tick (null si no)

    // Derecho de paso (T3)
    public float CriticalGapS { get; set; }              // brecha critica del conductor (s)
    public float MinorStreetExtraGapS { get; set; }      // extra al salir de un PARE/CEDA (s)
    public TrafficLane CommittedConnector { get; set; }  // ya decidio entrar a este conector: no se re-evalua
    public TrafficLane StoppedAtLineOf { get; set; }     // carril en cuya linea ya hizo la detencion del PARE
    public double WaitingSinceS { get; set; } = -1.0;    // esperando para entrar desde (s de simulacion), -1 = no
    public bool ForceEntry { get; set; }                 // antibloqueo: entra aunque no haya brecha

    // Adelantamiento al ciclista (T4). LateralOffset (+ = derecha) es la posicion lateral respecto del eje del carril.
    public enum OvertakePhase { None, Shifting, Holding, Returning }
    public OvertakePhase OvertakeState { get; set; }
    public float PlannedPassingDistanceM { get; set; } = float.NaN;  // D sorteada para este encuentro (NaN = aun no)
    public string OvertakeManeuver { get; set; } = "none";            // none / in_lane / one_way / opposing / partial
    public float OvertakeOncomingTtcS { get; set; } = float.PositiveInfinity;
    public float FollowBeforeOvertakeS { get; set; } = 2f;            // t_follow del conductor (s)
    public double FollowingBikeSinceS { get; set; } = -1.0;
    public float LatFrom { get; set; }
    public float LatTo { get; set; }
    public double LatStartS { get; set; }
    public float LatDurationS { get; set; }
    public float LatProgress { get; set; }                            // 0..1 a lo largo de la maniobra lateral
    public float LatSlope { get; set; }                               // dLat/ds de la maniobra: orienta el auto

    // Diagnostico del ultimo tick
    public float LastGapM { get; set; } = float.PositiveInfinity;
    public string LastLeader { get; set; } = "";

    private Rigidbody rb;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.useGravity = false;
    }

    /// <summary>Limpia el estado al sacarlo del pool (o al crearlo) con un Id nuevo.</summary>
    public void ResetForSpawn(int id)
    {
        Id = id;
        Label = "auto#" + id;
        Lane = null;
        NextLane = null;
        S = 0f;
        Speed = 0f;
        Accel = 0f;
        LateralOffset = 0f;
        SpeedFactor = 1f;
        Params = default;
        YellowDecisionLine = null;
        YellowDecisionStop = false;
        StartHoldLine = null;
        StartHoldUntilS = 0.0;
        LastStopLineLeader = null;
        NextNextLane = null;
        CriticalGapS = 4.5f;
        MinorStreetExtraGapS = 2f;
        CommittedConnector = null;
        StoppedAtLineOf = null;
        WaitingSinceS = -1.0;
        ForceEntry = false;
        OvertakeState = OvertakePhase.None;
        PlannedPassingDistanceM = float.NaN;
        OvertakeManeuver = "none";
        OvertakeOncomingTtcS = float.PositiveInfinity;
        FollowBeforeOvertakeS = 2f;
        FollowingBikeSinceS = -1.0;
        LatFrom = LatTo = 0f;
        LatStartS = 0.0;
        LatDurationS = 0f;
        LatProgress = 0f;
        LatSlope = 0f;
        LastGapM = float.PositiveInfinity;
        LastLeader = "";
    }

    /// <summary>Mueve el auto a su pose actual en el carril (cada tick).</summary>
    public void ApplyPose()
    {
        Lane.GetPose(S, LateralOffset, out Vector3 p, out Quaternion r);
        rb.MovePosition(p);
        rb.MoveRotation(Heading(r));
    }

    /// <summary>Durante una maniobra lateral el auto apunta en la direccion en que se mueve (no se desliza de lado).</summary>
    private Quaternion Heading(Quaternion laneRotation) =>
        Mathf.Abs(LatSlope) > 1e-4f ? laneRotation * Quaternion.Euler(0f, Mathf.Atan(LatSlope) * Mathf.Rad2Deg, 0f) : laneRotation;

    /// <summary>Coloca el auto sin interpolacion (al aparecer o tras el precalentamiento).</summary>
    public void SnapPose()
    {
        Lane.GetPose(S, LateralOffset, out Vector3 p, out Quaternion r);
        r = Heading(r);
        rb.position = p;
        rb.rotation = r;
        transform.SetPositionAndRotation(p, r);
    }

    private void OnDrawGizmosSelected()
    {
        // Etiqueta de diagnostico en la vista Scene
#if UNITY_EDITOR
        UnityEditor.Handles.Label(transform.position + Vector3.up * 2.2f,
            $"#{Id} v={Speed * 3.6f:F0} km/h a={Accel:F1}\ngap={LastGapM:F1} m ({LastLeader})");
#endif
    }
}
