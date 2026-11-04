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
// frente del auto hacia +Z. Los prefabs se arman con
// SimBici > Build Vehicle Prefabs (dimensiones desde los limites del modelo).
// Con distancia entre ejes > 0, la pose sale de dos puntos del carril (eje
// delantero y trasero): en las curvas la carroceria se corta hacia adentro
// como un vehiculo real (importa en el bus de 12 m).
// ============================================================

using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class TrafficVehicle : MonoBehaviour
{
    [Header("Dimensiones (m)")]
    [SerializeField] private float lengthM = 4.5f;
    [SerializeField] private float widthM = 1.8f;

    [Header("Modelo")]
    [Tooltip("Tipo para el registro (sedan, hatchback, suv, taxi, bus...).")]
    [SerializeField] private string modelType = "auto";
    [Tooltip("Distancia entre ejes (m). 0 = la pose sale de un solo punto del carril.")]
    [SerializeField, Min(0f)] private float wheelbaseM;
    [Tooltip("Perfil de conductor propio de este vehiculo (p. ej. bus); vacio = el del spawner.")]
    [SerializeField] private DriverProfile profileOverride;

    [Header("Color de carroceria (sorteado al aparecer)")]
    [SerializeField] private bool randomBodyColor;
    [SerializeField] private Renderer[] bodyRenderers = new Renderer[0];
    [SerializeField] private int[] bodyMaterialIndex = new int[0];

    [Header("Ruedas (giro visual)")]
    [SerializeField] private Transform[] wheels = new Transform[0];
    [SerializeField] private float wheelRadiusM = 0.33f;

    public float LengthM => lengthM;
    public float WidthM => widthM;
    public string ModelType => modelType;
    public float WheelbaseM => wheelbaseM;
    public DriverProfile ProfileOverride => profileOverride;
    public bool RandomBodyColor => randomBodyColor;
    /// <summary>Carril en el que estaba antes del actual (para la pose del eje trasero y la cola).</summary>
    public TrafficLane PrevLane { get; set; }
    public Color BodyColor { get; private set; } = Color.white;

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
        PrevLane = null;
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
        ComputePose(out Vector3 p, out Quaternion r);
        rb.MovePosition(p);
        rb.MoveRotation(r);
    }

    /// <summary>
    /// Pose del vehiculo. Sin distancia entre ejes: el punto del carril en S. Con ella: los puntos del carril en
    /// S +- L_e/2 (eje delantero y trasero, siguiendo los carriles siguiente y anterior); la carroceria va en el punto
    /// medio y apunta del eje trasero al delantero.
    /// </summary>
    private void ComputePose(out Vector3 p, out Quaternion r)
    {
        // Solo los vehiculos largos (bus): en un auto el corte es de centimetros y evaluar el spline tres veces por
        // tick cuesta en el hilo principal
        if (wheelbaseM <= 0.01f || lengthM < TwoAxlePoseMinLengthM)
        {
            Lane.GetPose(S, LateralOffset, out p, out r);
            r = Heading(r);
            return;
        }
        Vector3 front = PointAt(S + wheelbaseM * 0.5f), rear = PointAt(S - wheelbaseM * 0.5f);
        p = (front + rear) * 0.5f;
        Vector3 dir = front - rear;
        if (dir.sqrMagnitude > 1e-6f) r = Quaternion.LookRotation(dir.normalized, Vector3.up);
        else Lane.GetPose(S, LateralOffset, out _, out r);
        r = Heading(r);
    }

    /// <summary>Punto del eje de la ruta a una distancia s del inicio del carril actual (puede caer en el anterior o el siguiente).</summary>
    private Vector3 PointAt(float s)
    {
        TrafficLane lane = Lane;
        if (s > lane.LengthM && NextLane != null) { s -= lane.LengthM; lane = NextLane; }
        else if (s < 0f && PrevLane != null) { s += PrevLane.LengthM; lane = PrevLane; }
        lane.GetPose(Mathf.Clamp(s, 0f, lane.LengthM), LateralOffset, out Vector3 pt, out _);
        return pt;
    }

    private static readonly int BodyColorId = Shader.PropertyToID("_BodyColor");
    private const float TwoAxlePoseMinLengthM = 7f;

    /// <summary>Pinta la carroceria (sin instanciar materiales).</summary>
    public void SetBodyColor(Color c)
    {
        BodyColor = c;
        var mpb = new MaterialPropertyBlock();
        for (int i = 0; i < bodyRenderers.Length; i++)
        {
            var r = bodyRenderers[i];
            if (r == null) continue;
            // Malla fusionada con SimBici/VehicleVertexColor: color por instancia (no rompe el GPU instancing)
            r.GetPropertyBlock(mpb);
            mpb.SetColor(BodyColorId, c);
            r.SetPropertyBlock(mpb);
        }
    }

    /// <summary>Visual: giro de las ruedas segun la velocidad.</summary>
    private void Update()
    {
        if (wheels.Length == 0 || Speed < 0.01f) return;
        float deg = Speed * Time.deltaTime / Mathf.Max(wheelRadiusM, 0.05f) * Mathf.Rad2Deg;
        foreach (var w in wheels) if (w != null) w.Rotate(deg, 0f, 0f, Space.Self);
    }

    /// <summary>Durante una maniobra lateral el auto apunta en la direccion en que se mueve (no se desliza de lado).</summary>
    private Quaternion Heading(Quaternion laneRotation) =>
        Mathf.Abs(LatSlope) > 1e-4f ? laneRotation * Quaternion.Euler(0f, Mathf.Atan(LatSlope) * Mathf.Rad2Deg, 0f) : laneRotation;

    /// <summary>Coloca el auto sin interpolacion (al aparecer o tras el precalentamiento).</summary>
    public void SnapPose()
    {
        ComputePose(out Vector3 p, out Quaternion r);
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
