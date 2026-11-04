// ============================================================
// TrafficManager.cs
// Motor del trafico. Cada FixedUpdate hace UN paso sincronizado:
//   0. avanza los semaforos (IntersectionController)
//   1. proyecta al ciclista sobre los carriles (para que los autos lo vean)
//   2. spawners generan autos nuevos
//   3. TODOS los autos calculan su aceleracion IDM con el estado actual
//   4. TODOS avanzan (integracion balistica)
//   5. cambios de carril al llegar al final / desaparicion al salir de la red
//   6. se actualizan las poses visuales
// Todo el azar pasa por System.Random con semilla: mismo seed + mismo
// comportamiento del ciclista = mismo trafico.
// Va en un objeto "Traffic" de la escena (uno solo).
// El nivel de trafico de toda la escena se ajusta con flowMultiplier: escala el flujo
// de los spawners y la cantidad objetivo de autos (vehiclesPerLevel x flowMultiplier).
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

[DefaultExecutionOrder(20)]  // despues de BicycleController (0)
public class TrafficManager : MonoBehaviour
{
    [Header("Reproducibilidad")]
    [SerializeField] private int masterSeed = 12345;

    [Header("Nivel de trafico")]
    [Tooltip("Multiplica el flujo de TODOS los spawners (1 = flujos nominales, 0.5 = la mitad, 2 = el doble). " +
             "Cambiar solo este valor para correr la misma escena con distintos niveles de trafico.")]
    [SerializeField, Min(0f)] private float flowMultiplier = 1f;
    [Tooltip("Autos objetivo en la red con Flow Multiplier = 1. Objetivo = esto x Flow Multiplier. " +
             "Los spawners no generan por encima del objetivo y RingExitController regula las salidas.")]
    [SerializeField, Min(0)] private int vehiclesPerLevel = 30;
    [Tooltip("Durante el precalentamiento los spawners generan este multiplo de su flujo, para llenar la red antes de empezar.")]
    [SerializeField, Min(1f)] private float warmUpFlowBoost = 3f;

    [Header("Ciclista")]
    [SerializeField] private BicycleController bike;
    [SerializeField] private float bikeLengthM = 1.8f;
    [Tooltip("Margen extra al ancho de carril para considerar que la bici esta 'en' el carril.")]
    [SerializeField] private float bikeLaneMarginM = 0.3f;

    [Header("Simulacion")]
    [Tooltip("Distancia maxima a la que un auto busca un vehiculo adelante (m).")]
    [SerializeField] private float lookAheadM = 150f;
    [Tooltip("Segundos de trafico simulados antes de empezar, para que la calle no arranque vacia.")]
    [SerializeField] private float warmUpSeconds = 60f;
    [Tooltip("Tras el precalentamiento, se eliminan los autos a menos de esta distancia del ciclista.")]
    [SerializeField] private float clearRadiusAroundBikeM = 20f;

    [Header("Semaforos")]
    [Tooltip("b_safe: en amarillo el auto se detiene si v^2/(2 b_safe) <= distancia a la linea (m/s2).")]
    [SerializeField, Min(0.1f)] private float yellowSafeDecelMps2 = 4f;
    [Tooltip("Retardo de arranque del primer auto de la cola al cambiar a verde.")]
    [SerializeField] private bool startUpDelayEnabled = false;
    [SerializeField] private float startUpDelayMeanS = 1.0f;
    [SerializeField] private float startUpDelaySdS = 0.3f;

    [Header("Giros y derecho de paso")]
    [Tooltip("Anticipacion: frenar antes de entrar a un carril con limite menor (v0_eff = min(v0, sqrt(v_lim^2 + 2 b d))).")]
    [SerializeField] private bool anticipateSpeedLimits = true;
    [Tooltip("La entrada a una interseccion se evalua desde esta distancia a la linea (m).")]
    [SerializeField] private float entryCheckDistanceM = 50f;
    [Tooltip("Un auto que gira cede si el ciclista llega al cruce en menos de esto (s) (right hook).")]
    [SerializeField] private float cyclistYieldTimeS = 4f;
    [Tooltip("Antibloqueo: tras esperar esto (s) por un conflicto, el auto de menor Id entra igual (se registra traffic_deadlock).")]
    [SerializeField] private float deadlockAfterS = 20f;

    public enum AmbientOvertakingMode { Free, Disabled }

    [Header("Adelantamiento al ciclista (T4)")]
    [Tooltip("Free: los autos adelantan con la distancia lateral sorteada. Disabled: siguen al ciclista sin adelantar " +
             "(si va dentro del carril) o pasan sin desviarse (si va por la ciclovia).")]
    [SerializeField] private AmbientOvertakingMode overtakingMode = AmbientOvertakingMode.Free;
    [Tooltip("Mediana de la distancia lateral (m), extremo del manubrio a costado del auto. Beck et al. (2019): 1.73 m.")]
    [SerializeField] private float passingMedianM = 1.73f;
    [Tooltip("Rango intercuartil de la distancia (m). Beck et al. (2019): 1.37-2.24 m. Define sigma de la lognormal.")]
    [SerializeField] private Vector2 passingIqrM = new Vector2(1.37f, 2.24f);
    [SerializeField] private Vector2 passingClampM = new Vector2(0.5f, 3.0f);
    [Tooltip("Medio ancho del manubrio (m). Modelo 3D: 0.548 m de ancho. Ajustar a la bici real.")]
    [SerializeField] private float handlebarHalfWidthM = 0.274f;
    [Tooltip("Aceleracion lateral maxima de la maniobra (m/s2).")]
    [SerializeField] private float maxLateralAccelMps2 = 1.0f;
    [SerializeField] private Vector2 maneuverDurationS = new Vector2(2f, 4f);
    [Tooltip("Angulo maximo entre el auto y su carril durante la maniobra lateral (grados). Un auto no se corre de lado " +
             "sin avanzar: a baja velocidad la maniobra se alarga para no superar este angulo.")]
    [SerializeField] private float maxOvertakeHeadingDeg = 15f;
    [Tooltip("Angulo maximo con el auto casi detenido (grados): a vuelta de rueda se gira mas. Baja linealmente " +
             "hasta maxOvertakeHeadingDeg a 5 m/s. [VERIFICAR]")]
    [SerializeField] private float lowSpeedOvertakeHeadingDeg = 30f;
    [Tooltip("Tiempo a colision minimo con el primer auto de frente para invadir el carril contrario (s). Rasch et al. (2020): 5-7.5 s.")]
    [SerializeField] private float minOncomingTtcS = 5f;
    [Tooltip("Margen sobre la duracion estimada del adelantamiento para el tiempo a colision con el de frente (s).")]
    [SerializeField] private float oncomingTtcMarginS = 2f;
    [Tooltip("Si ya esta en el carril contrario y el de frente queda a menos de esto (s), aborta y vuelve.")]
    [SerializeField] private float abortOncomingTtcS = 2.5f;
    [Tooltip("Diferencia de velocidad minima para adelantar a un ciclista que va dentro de su trayectoria (m/s).")]
    [SerializeField] private float overtakeSpeedDiffMps = 1.5f;
    [Tooltip("Vuelve al centro cuando su parte trasera paso el frente de la bici por esta distancia (m).")]
    [SerializeField] private float returnMarginM = 3f;

    public static TrafficManager Instance { get; private set; }
    public float HandlebarHalfWidthM => handlebarHalfWidthM;
    public int MasterSeed => masterSeed;
    public AmbientOvertakingMode OvertakingMode { get => overtakingMode; set => overtakingMode = value; }

    /// <summary>Multiplicador global del flujo. Setearlo antes de Start para que aplique tambien al precalentamiento.</summary>
    public float FlowMultiplier
    {
        get => flowMultiplier;
        set => flowMultiplier = Mathf.Max(0f, value);
    }

    /// <summary>Cantidad de autos objetivo segun el nivel de trafico.</summary>
    public int TargetVehicles => Mathf.RoundToInt(vehiclesPerLevel * flowMultiplier);
    public bool IsWarmingUp { get; private set; }
    public float SpawnFlowBoost => IsWarmingUp ? warmUpFlowBoost : 1f;

    public float BikeHalfLengthM => bikeLengthM * 0.5f;
    public float BikeSpeedMps => bike != null ? bike.CurrentSpeedMps : 0f;
    public int ActiveCount => vehicles.Count;
    public IReadOnlyList<TrafficVehicle> Vehicles => vehicles;

    private readonly List<TrafficLane> lanes = new List<TrafficLane>();
    private readonly List<TrafficSpawner> spawners = new List<TrafficSpawner>();
    private readonly List<TrafficVehicle> vehicles = new List<TrafficVehicle>();  // orden de Id (determinista)
    private readonly List<TrafficVehicle> toRemove = new List<TrafficVehicle>();
    private readonly Dictionary<TrafficLane, BikeOnLane> bikeOnLane = new Dictionary<TrafficLane, BikeOnLane>();

    // Pool por prefab: los autos que salen de la red se desactivan y se reutilizan
    // (evita Instantiate/Destroy y la basura que generan durante la sesion).
    private readonly Dictionary<TrafficVehicle, Stack<TrafficVehicle>> pools = new Dictionary<TrafficVehicle, Stack<TrafficVehicle>>();

    /// <summary>Ciclista proyectado sobre un carril: posicion S y velocidad a lo largo del carril.</summary>
    private struct BikeOnLane
    {
        public float S;
        public float SpeedAlongMps;  // negativa si la bici va en contra del carril; ~0 si lo cruza
        public float Lateral;        // + = a la derecha del eje del carril
        public bool InBand;          // dentro del ancho del carril (+ margen): el auto lo considera "en su carril"
        public float HalfAcrossM;    // medio ancho que ocupa la bici a lo ancho del carril (segun su orientacion)
    }

    /// <summary>Distancia lateral maxima a la que se registra al ciclista respecto de un carril (m).</summary>
    private const float BikeNearLateralM = 6f;

    private readonly List<IntersectionController> intersections = new List<IntersectionController>();
    private readonly List<StopLine> stopLines = new List<StopLine>();

    private readonly List<TrafficVehicle> longWaiters = new List<TrafficVehicle>();
    private readonly Dictionary<TrafficLane, List<float>> pedOnLane = new Dictionary<TrafficLane, List<float>>();
    private readonly List<RingExitController> exitControllers = new List<RingExitController>();
    private RiderRoute riderRoute;

    private System.Random routingRng;
    private System.Random startUpRng;
    private System.Random overtakeRng;
    private readonly List<TrafficVehicle> opposingOvertakers = new List<TrafficVehicle>();
    private int nextId = 1;
    private bool ignoreBike;
    private double simTimeS;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogError("[TrafficManager] Hay mas de un TrafficManager en la escena.");
            enabled = false;
            return;
        }
        Instance = this;

        // Orden por nombre: FindObjectsByType no garantiza orden, y el orden
        // afecta el uso de numeros aleatorios -> reproducibilidad.
        lanes.AddRange(FindObjectsByType<TrafficLane>(FindObjectsSortMode.None));
        lanes.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        spawners.AddRange(FindObjectsByType<TrafficSpawner>(FindObjectsSortMode.None));
        spawners.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        routingRng = CreateRng(0);
        startUpRng = CreateRng(1000);
        overtakeRng = CreateRng(2000);

        // Semaforos: los avanza este manager dentro de su paso (sincronizados con los autos)
        intersections.AddRange(FindObjectsByType<IntersectionController>(FindObjectsSortMode.None));
        intersections.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        foreach (var ic in intersections)
        {
            ic.SetDrivenExternally(true);
            ic.SuppressEvents = warmUpSeconds > 0f;
            var controller = ic;
            ic.OnPhaseChanged += (approach, phase) => OnSignalChanged(controller, approach, phase);
        }
        foreach (var lane in lanes)
            if (lane.TryGetComponent(out StopLine sl) && !sl.IsCyclistApproach && sl.IsSignalized) stopLines.Add(sl);

        exitControllers.AddRange(FindObjectsByType<RingExitController>(FindObjectsSortMode.None));
        exitControllers.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        foreach (var ec in exitControllers) ec.Resolve();

        // Conectores: de que carril de llegada salen (para el calculo de llegadas en conflicto)
        foreach (var lane in lanes)
            foreach (var n in lane.NextLanes)
                if (n.lane != null && n.lane.IsConnector) n.lane.EntryLane = lane;

        // Conectores hermanos: hasta donde se superponen sus trayectorias (antes se suponia 6 m; con las lineas de
        // detencion mas atras los conectores son mas largos y un recto y un giro comparten mas tramo)
        foreach (var lane in lanes)
        {
            if (lane.IsConnector) continue;
            foreach (var a in lane.NextLanes)
                foreach (var b in lane.NextLanes)
                    if (a.lane != null && b.lane != null && a.lane != b.lane && a.lane.IsConnector && b.lane.IsConnector)
                        siblingOverlap[(a.lane, b.lane)] = SiblingOverlapLength(a.lane, b.lane);
        }

        // Ciclista en la red: para que los giros le cedan el paso
        riderRoute = FindFirstObjectByType<RiderRoute>();
        var network = riderRoute != null ? riderRoute.Network : null;
        foreach (var lane in lanes)
            if (lane.BikeConflicts != null) lane.BikeConflicts.Build(network);
    }

    private void Start()
    {
        foreach (var sp in spawners)
        {
            if (sp.IsValid(out string err)) sp.Init(this);
            else Debug.LogError($"[TrafficManager] Spawner '{sp.name}' invalido: {err}");
        }

        if (warmUpSeconds > 0f) WarmUp();

        EventBus.Publish("traffic_start", "traffic", string.Format(CultureInfo.InvariantCulture,
            "seed={0};flow_multiplier={1};target_vehicles={2};lanes={3};spawners={4};intersections={5};vehicles_after_warmup={6}",
            masterSeed, flowMultiplier, TargetVehicles, lanes.Count, spawners.Count, intersections.Count, vehicles.Count));

        foreach (var ic in intersections)
        {
            ic.SuppressEvents = false;
            ic.PublishCurrentState("start");
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>RNG independiente por consumidor: agregar un spawner no altera la secuencia de los otros.</summary>
    public System.Random CreateRng(int offset) => CreateRng(masterSeed, offset);

    public static System.Random CreateRng(int seed, int offset)
    {
        unchecked { return new System.Random(seed * 486187739 + offset * 16777619); }
    }

    // ------------------------------------------------------------
    // Bucle principal
    // ------------------------------------------------------------
    private void FixedUpdate()
    {
        Step(Time.fixedDeltaTime, applyPoses: true);
    }

    private void WarmUp()
    {
        // El ciclista no "existe" durante el precalentamiento: si no, los autos
        // harian cola detras de la bici detenida durante un minuto.
        ignoreBike = true;
        IsWarmingUp = true;
        float dt = Time.fixedDeltaTime;
        int steps = Mathf.CeilToInt(warmUpSeconds / dt);
        for (int i = 0; i < steps; i++) Step(dt, applyPoses: false);
        IsWarmingUp = false;
        ignoreBike = false;

        // Sacar autos que quedaron encima o muy cerca del ciclista
        if (bike != null)
        {
            Vector3 b = bike.RiderPosition;
            foreach (var v in vehicles)
            {
                v.Lane.GetPose(v.S, v.LateralOffset, out Vector3 p, out _);
                if ((p - b).sqrMagnitude < clearRadiusAroundBikeM * clearRadiusAroundBikeM) toRemove.Add(v);
            }
            FlushRemovals();
        }

        foreach (var v in vehicles) v.SnapPose();
    }

    private void Step(float dt, bool applyPoses)
    {
        simTimeS += dt;
        foreach (var ic in intersections) ic.Tick(dt);
        foreach (var ec in exitControllers) ec.Tick(dt, this);

        UpdateBikeProjection();
        UpdatePedestrianProjection();

        foreach (var sp in spawners) sp.Tick(dt, this);

        foreach (var lane in lanes) SortLane(lane);

        // Adelantamientos: decidir, avanzar o terminar la maniobra lateral de cada auto
        UpdateOvertaking(dt);

        // Fase 1: aceleraciones con el estado actual (sin mover a nadie)
        foreach (var v in vehicles)
        {
            v.Params.desiredSpeedMps = DesiredSpeed(v);
            FindLeader(v, out float gap, out float leaderSpeed, out string leaderName);
            v.LastGapM = gap;
            v.LastLeader = leaderName;
            float dv = float.IsPositiveInfinity(gap) ? 0f : v.Speed - leaderSpeed;
            v.Accel = IntelligentDriverModel.Acceleration(v.Params, v.Speed, gap, dv);
        }
        ResolveDeadlocks();

        // Fase 2: integrar y resolver cambios de carril
        foreach (var v in vehicles)
        {
            float speed = v.Speed;
            v.S += IntelligentDriverModel.IntegrateBallistic(ref speed, v.Accel, dt);
            v.Speed = speed;

            while (v.S >= v.Lane.LengthM)
            {
                float overflow = v.S - v.Lane.LengthM;
                TrafficLane from = v.Lane;
                from.Vehicles.Remove(v);

                // Paso la linea de detencion: la decision en amarillo y el retardo de ese acceso ya no aplican
                if (from.StopLine != null)
                {
                    if (v.YellowDecisionLine == from.StopLine) v.YellowDecisionLine = null;
                    if (v.StartHoldLine == from.StopLine) v.StartHoldLine = null;
                }

                // Derecho de paso: lo decidido para este carril/conector ya no aplica
                if (v.CommittedConnector == from) v.CommittedConnector = null;
                if (v.StoppedAtLineOf == from) v.StoppedAtLineOf = null;

                if (v.NextLane == null) { toRemove.Add(v); break; }

                v.Lane = v.NextLane;
                v.S = overflow;
                v.NextLane = v.NextNextLane;
                v.NextNextLane = v.NextLane != null ? v.NextLane.PickNext(routingRng) : null;
                v.Lane.Vehicles.Insert(0, v);  // recien entrado: es el de S mas bajo

                if (v.Lane.IsConnector)
                {
                    v.ForceEntry = false;
                    v.WaitingSinceS = -1.0;
                }
            }
        }
        FlushRemovals();

        if (applyPoses)
            foreach (var v in vehicles) v.ApplyPose();
    }

    // ------------------------------------------------------------
    // Lider: auto de adelante o ciclista, buscando a traves de carriles
    // ------------------------------------------------------------
    // Paso peatonal mas cercano adelante encontrado en la ultima busqueda de lider (frente -> punto de detencion)
    private float nearestCwStopM, nearestCwWidthM;

    private void FindLeader(TrafficVehicle v, out float gap, out float leaderSpeed, out string leaderName)
    {
        nearestCwStopM = float.PositiveInfinity;
        FindLeaderCore(v, out gap, out leaderSpeed, out leaderName);

        // No quedar detenido sobre un paso peatonal por la cola: si el lider es un auto lento y detenerse detras de el
        // dejaria al auto encima del paso, se detiene antes del paso (como se espera de un conductor)
        if (!float.IsPositiveInfinity(nearestCwStopM) && gap > nearestCwStopM && leaderSpeed < 1.5f &&
            leaderName.StartsWith("auto#"))
        {
            float restFront = gap - v.Params.minGapM;                 // donde quedaria su frente
            float farEdge = nearestCwStopM + 1f + nearestCwWidthM;     // borde lejano del paso
            if (restFront - v.LengthM < farEdge + 0.5f)
            {
                gap = nearestCwStopM + LineObstacleShift(v);
                leaderSpeed = 0f;
                leaderName = "paso_libre";
            }
        }
    }

    private void FindLeaderCore(TrafficVehicle v, out float gap, out float leaderSpeed, out string leaderName)
    {
        gap = float.PositiveInfinity;
        leaderSpeed = 0f;
        leaderName = "";
        v.LastStopLineLeader = null;

        TrafficLane lane = v.Lane;
        TrafficLane next = v.NextLane;
        float offset = 0f;        // distancia acumulada desde el inicio de 'lane' hasta el auto
        float selfFrontS = v.S + v.LengthM * 0.5f;
        bool first = true;
        int guard = 0;

        // Un auto que adelanta invadiendo este carril cuenta como obstaculo que viene de frente
        OncomingOvertakerObstacle(v, ref gap, ref leaderSpeed, ref leaderName);

        while (lane != null && guard++ < 8)
        {
            // Vehiculo mas cercano adelante en este carril
            var list = lane.Vehicles;
            for (int i = 0; i < list.Count; i++)
            {
                var other = list[i];
                if (other == v) continue;
                float otherRear = offset + other.S - other.LengthM * 0.5f;
                if (first && other.S <= v.S) continue;   // esta detras
                float g = otherRear - selfFrontS;
                if (g < gap) { gap = g; leaderSpeed = other.Speed; leaderName = other.Label; }
                break;  // lista ordenada: el primero adelante es el mas cercano
            }

            // Conectores hermanos (salen del mismo carril de llegada): en sus primeros metros las
            // trayectorias se superponen, asi que un auto ahi cuenta como si estuviera en este carril
            if (lane.IsConnector && lane.EntryLane != null)
            {
                foreach (var sib in lane.EntryLane.NextLanes)
                {
                    if (sib.lane == null || sib.lane == lane) continue;
                    foreach (var other in sib.lane.Vehicles)
                    {
                        if (other.S > SiblingOverlap(lane, sib.lane)) break;  // lista ordenada por S
                        if (first && other.S <= v.S) continue;          // esta detras
                        float g = offset + other.S - other.LengthM * 0.5f - selfFrontS;
                        if (g < gap) { gap = Mathf.Max(g, 0f); leaderSpeed = other.Speed; leaderName = other.Label; }
                    }
                }
            }

            // Ciclista en este carril. Su velocidad es la componente a lo largo del carril:
            // si va en contra (negativa) o cruza (~0), el auto frena antes que con la rapidez escalar.
            if (!ignoreBike && bikeOnLane.TryGetValue(lane, out BikeOnLane bk) && (!first || bk.S > v.S))
            {
                float g = offset + bk.S - BikeHalfLengthM - selfFrontS;
                if (g > -v.LengthM && g < gap && BikeIsObstacle(v, bk, g))
                {
                    gap = Mathf.Max(g, 0f); leaderSpeed = bk.SpeedAlongMps; leaderName = "ciclista";
                    // Bici detenida que va a adelantar: quedarse a la distancia necesaria para salir despues
                    if (overtakingMode == AmbientOvertakingMode.Free && Mathf.Abs(bk.SpeedAlongMps) < StoppedBikeSpeedMps &&
                        v.OvertakeState == TrafficVehicle.OvertakePhase.None)
                        gap = Mathf.Max(g - Mathf.Max(WaitGapBehindStoppedBike(v, bk) - v.Params.minGapM, 0f), 0f);
                }
            }

            // Pasos peatonales que cruzan este carril: detenerse antes si hay que ceder al peaton
            foreach (var (cw, sCw, uCw) in lane.Crosswalks)
            {
                float dEdge = offset + sCw - cw.WidthM * 0.5f - 1f - selfFrontS;  // 1 m antes del borde del cruce
                // Se evalua hasta que el frente del auto sale por el otro borde: un auto que ya entro al cruce
                // tambien se detiene si un peaton entra a su franja (antes lo ignoraba y podia atropellarlo)
                if (dEdge < -(cw.WidthM + 1f) || dEdge >= gap) continue;
                if (dEdge < 0f)
                {
                    // Ya sobre el cruce: solo se detiene por un peaton delante de su frente; termina de pasar y lo libera
                    if (cw.PedestrianAheadOf(v.transform, v.LengthM, v.WidthM)) { gap = 0f; leaderSpeed = 0f; leaderName = "peaton"; }
                    continue;
                }
                if (dEdge < nearestCwStopM) { nearestCwStopM = dEdge; nearestCwWidthM = cw.WidthM; }
                if (cw.CarMustYield(lane, uCw, lane.LaneWidthM * 0.5f, dEdge, v.Speed, v.LengthM,
                                    v.Params.comfortDecelMps2, v.Params.minGapM))
                {
                    // El IDM queda a s0 del obstaculo; se corre para que el frente quede en el punto de detencion
                    // (1 m antes del borde)
                    gap = dEdge + LineObstacleShift(v);
                    leaderSpeed = 0f;
                    leaderName = "peaton";
                }
            }

            // Peatones en la calzada fuera de un paso (cruce scriptado)
            if (pedOnLane.TryGetValue(lane, out var pedSs))
            {
                foreach (float ps in pedSs)
                {
                    if (first && ps <= v.S) continue;
                    float g = offset + ps - 0.4f - selfFrontS;
                    if (g > -v.LengthM && g < gap) { gap = Mathf.Max(g, 0f); leaderSpeed = 0f; leaderName = "peaton"; }
                }
            }

            // Linea de detencion al final de este carril: obstaculo detenido de largo 0 si hay que parar
            StopLine line = lane.StopLine;
            if (line != null && line.IsSignalized)
            {
                float d = offset + lane.LengthM - selfFrontS;   // del frente del auto a la linea
                float dObs = d + LineObstacleShift(v);
                if (d >= 0f && dObs < gap && MustStopAt(v, line, d))
                {
                    gap = dObs;
                    leaderSpeed = 0f;
                    leaderName = "semaforo";
                    v.LastStopLineLeader = line;
                }
            }

            // Carril siguiente de la ruta (los dos primeros ya estan elegidos)
            TrafficLane following = first ? next
                                  : lane == next ? v.NextNextLane
                                  : (lane.NextLanes.Count == 1 ? lane.NextLanes[0].lane : null);

            // Entrada a una interseccion: PARE, ceder el paso, cruce del ciclista, salida libre
            if (following != null && following.IsConnector && v.CommittedConnector != following)
            {
                float d = offset + lane.LengthM - selfFrontS;
                if (d >= -0.5f && d < gap && d < entryCheckDistanceM)
                {
                    string reason = EntryBlockReason(v, lane, following, d);
                    if (reason != null)
                    {
                        gap = Mathf.Min(gap, Mathf.Max(d, 0f) + LineObstacleShift(v));
                        leaderSpeed = 0f;
                        leaderName = reason;
                    }
                    else if (d <= BrakingDistance(v) + 1f || (v.Speed < 0.5f && d < 3f))
                    {
                        v.CommittedConnector = following;  // decidido: ya no se re-evalua
                    }
                }
            }

            // Dentro de la interseccion: no llegar a un punto de conflicto al que otro auto llega antes
            if (lane.IsConnector && lane.Conflicts.Count > 0)
            {
                float d = ConflictPointObstacle(v, lane, offset, selfFrontS);
                if (d < gap)
                {
                    gap = d;
                    leaderSpeed = 0f;
                    leaderName = "cruce";
                }
            }

            // Autos que acaban de entrar a un carril siguiente (p. ej. un conector) con la cola todavia sobre este:
            // si este carril ya dio un obstaculo (linea, espera) el bucle termina aqui y no los veria
            foreach (var nl in lane.NextLanes)
            {
                if (nl.lane == null) continue;
                foreach (var o in nl.lane.Vehicles)
                {
                    if (o.S - o.LengthM * 0.5f >= 0f) break;                // lista ordenada por S
                    if (o == v) continue;
                    float g = offset + lane.LengthM + o.S - o.LengthM * 0.5f - selfFrontS;
                    if (g > -v.LengthM && g < gap) { gap = Mathf.Max(g, 0f); leaderSpeed = o.Speed; leaderName = o.Label; }
                }
            }

            if (!float.IsPositiveInfinity(gap)) return;

            // Seguir buscando en el siguiente carril de la ruta
            offset += lane.LengthM;
            if (offset - v.S > lookAheadM) return;

            lane = following;
            first = false;
        }
    }

    // ------------------------------------------------------------
    // Velocidad deseada con anticipacion de limites menores (curvas, giros)
    // ------------------------------------------------------------
    private float DesiredSpeed(TrafficVehicle v)
    {
        float v0 = v.Lane.SpeedLimitMps * v.SpeedFactor;
        if (!anticipateSpeedLimits) return v0;

        float b = Mathf.Max(v.Params.comfortDecelMps2, 0.5f);
        float d = v.Lane.LengthM - v.S;  // el limite cambia cuando el centro del auto entra al carril
        if (v.NextLane != null)
        {
            v0 = TrafficRules.AnticipatedDesiredSpeed(v0, v.NextLane.SpeedLimitMps * v.SpeedFactor, d, b);
            if (v.NextNextLane != null)
                v0 = TrafficRules.AnticipatedDesiredSpeed(v0, v.NextNextLane.SpeedLimitMps * v.SpeedFactor,
                                                          d + v.NextLane.LengthM, b);
        }
        return v0;
    }

    /// <summary>Largo inicial (m) en que los conectores que salen del mismo carril se consideran superpuestos.</summary>
    private const float SiblingOverlapM = 6f;
    private readonly Dictionary<(TrafficLane, TrafficLane), float> siblingOverlap = new Dictionary<(TrafficLane, TrafficLane), float>();

    private float SiblingOverlap(TrafficLane lane, TrafficLane sib) =>
        siblingOverlap.TryGetValue((lane, sib), out float d) ? d : SiblingOverlapM;

    /// <summary>
    /// Distancia desde el inicio hasta donde dos conectores hermanos quedan separados lo suficiente para que dos autos
    /// no se toquen (centros a mas de 2.5 m), mas un largo de auto de margen.
    /// </summary>
    private static float SiblingOverlapLength(TrafficLane a, TrafficLane b)
    {
        float len = Mathf.Min(a.LengthM, b.LengthM);
        for (float s = 0f; s <= len; s += 0.5f)
        {
            a.GetPose(s, 0f, out Vector3 pa, out _);
            b.GetPose(s, 0f, out Vector3 pb, out _);
            pa.y = pb.y = 0f;
            if ((pa - pb).sqrMagnitude > 2.5f * 2.5f) return Mathf.Max(SiblingOverlapM, s + 4.5f);
        }
        return len;
    }

    private float BrakingDistance(TrafficVehicle v) => v.Speed * v.Speed / (2f * yellowSafeDecelMps2);

    // ------------------------------------------------------------
    // Derecho de paso: puede el auto v entrar al conector 'conn' desde el final de 'entry'?
    // Devuelve null si puede, o el motivo por el que espera en la linea.
    // ------------------------------------------------------------
    private string EntryBlockReason(TrafficVehicle v, TrafficLane entry, TrafficLane conn, float distanceToLineM)
    {
        StopLine line = entry.StopLine;
        bool minor = line != null && (line.Control == ApproachControl.Stop || line.Control == ApproachControl.Yield);

        // PARE: detencion completa en la linea antes de evaluar la brecha
        if (line != null && line.Control == ApproachControl.Stop && v.StoppedAtLineOf != entry)
        {
            if (v.Speed < 0.3f && distanceToLineM < 3f) v.StoppedAtLineOf = entry;
            else return "pare";
        }

        // No bloquear la interseccion: debe haber espacio para el auto en la salida
        if (!HasRoomOnExit(v, conn)) return "bloqueo";

        float gapNeeded = v.CriticalGapS + (minor ? v.MinorStreetExtraGapS : 0f);
        foreach (var k in conn.Conflicts)
        {
            if (k.other == null) continue;
            foreach (var other in vehicles)
            {
                if (other == v) continue;
                float dist = DistanceToPoint(other, k.other, k.sOther, out bool inside);
                if (float.IsNaN(dist)) continue;

                if (inside)
                {
                    float rearToEnd = k.sOtherEnd - (other.S - other.LengthM * 0.5f);
                    if (rearToEnd < -0.5f) continue;             // ya salio de la zona con toda su longitud
                    // Esta dentro de la zona o casi: ni el antibloqueo entra (antes ForceEntry lo ignoraba y chocaban)
                    if (dist < 1f) return "conflicto";
                }
                if (v.ForceEntry) continue;                     // antibloqueo: se salta la cesion, no la ocupacion
                if (k.mode == ConflictMode.OccupancyOnly)
                {
                    if (inside) return "conflicto";
                    continue;
                }
                if (!TrafficRules.GapAccepted(TrafficRules.TimeToReach(dist, other.Speed), gapNeeded)) return "ceder";
            }
        }

        // Giros que cruzan el camino del ciclista que sigue derecho (right hook)
        if (conn.BikeConflicts != null && riderRoute != null && !ignoreBike && bike != null)
        {
            float t = conn.BikeConflicts.BikeTimeToConflict(riderRoute, bike.CurrentSpeedMps, 0f);
            if (t < cyclistYieldTimeS) return "ciclista_cruce";
        }
        return null;
    }

    /// <summary>
    /// Para un conector de la ruta de v: distancia (desde el frente de v) a la que debe detenerse para
    /// no llegar a un punto de conflicto al que otro auto, YA DENTRO de su conector, llega antes.
    /// Empate (menos de 0.3 s): cede quien tiene modo Yield; con igual modo, pasa el de menor Id.
    /// El orden es simetrico, asi dos autos nunca se esperan mutuamente. PositiveInfinity si no hay.
    /// </summary>
    private float ConflictPointObstacle(TrafficVehicle v, TrafficLane conn, float offset, float selfFrontS)
    {
        const float tie = 0.3f, stopBeforeM = 1.0f;
        float best = float.PositiveInfinity;
        foreach (var k in conn.Conflicts)
        {
            if (k.other == null || k.other.Vehicles.Count == 0) continue;
            float distV = offset + k.sSelf - selfFrontS;
            if (distV < 0f) continue;  // v ya esta en el punto: seguir y despejarlo
            float tV = distV / Mathf.Max(v.Speed, 1f);

            foreach (var o in k.other.Vehicles)
            {
                if (o.S - o.LengthM * 0.5f > k.sOtherEnd + 0.5f) continue;  // ya salio de la zona con toda su longitud
                float distO = k.sOther - (o.S + o.LengthM * 0.5f);
                // Antibloqueo: solo cede a quien ya esta fisicamente en la zona
                if (v.ForceEntry) { if (distO <= 0f) best = Mathf.Min(best, Mathf.Max(distV - stopBeforeM, 0f)); continue; }
                float tO = distO <= 0f ? 0f : distO / Mathf.Max(o.Speed, 1f);

                bool yieldNow;
                if (tO < tV - tie) yieldNow = true;
                else if (tO > tV + tie) yieldNow = false;
                else
                {
                    ConflictMode modeO = ModeOf(k.other, conn);
                    yieldNow = k.mode != modeO ? k.mode == ConflictMode.Yield : o.Id < v.Id;
                }
                if (yieldNow) best = Mathf.Min(best, Mathf.Max(distV - stopBeforeM, 0f));
            }
        }
        return best;
    }

    private static ConflictMode ModeOf(TrafficLane conn, TrafficLane other)
    {
        foreach (var k in conn.Conflicts) if (k.other == other) return k.mode;
        return ConflictMode.OccupancyOnly;
    }

    /// <summary>
    /// Distancia del frente de 'other' al punto sD del conector D, siguiendo su ruta (NaN si no va hacia D).
    /// inside = ya esta dentro de D. Si aun no eligio conector pero viene al carril de llegada de D,
    /// se cuenta igual (conservador).
    /// </summary>
    private static float DistanceToPoint(TrafficVehicle other, TrafficLane D, float sD, out bool inside)
    {
        float front = other.S + other.LengthM * 0.5f;
        inside = other.Lane == D;
        if (inside) return sD - front;

        float rest = other.Lane.LengthM - front;
        if (other.NextLane == D) return rest + sD;
        if (other.NextLane != null && other.NextNextLane == D) return rest + other.NextLane.LengthM + sD;

        TrafficLane entry = D.EntryLane;
        if (entry != null && other.NextLane != null && other.NextNextLane == entry)
            return rest + other.NextLane.LengthM + entry.LengthM + sD;
        return float.NaN;
    }

    /// <summary>Hay espacio en la salida del conector (y en el carril que le sigue) para el largo del auto + s0.</summary>
    private bool HasRoomOnExit(TrafficVehicle v, TrafficLane conn)
    {
        if (conn.NextLanes.Count == 0 || conn.NextLanes[0].lane == null) return true;
        TrafficLane exit = conn.NextLanes[0].lane;
        float needed = v.LengthM + v.Params.minGapM;
        // Si a la salida hay un paso peatonal, debe caber entero pasado el paso (no quedar detenido encima)
        bool crosswalkAhead = conn.Crosswalks.Count > 0;
        foreach (var (cw, sCw, _) in exit.Crosswalks)
            if (sCw < 20f) { crosswalkAhead = true; needed = Mathf.Max(needed, sCw + cw.WidthM * 0.5f + v.LengthM + v.Params.minGapM); }
        float offset = 0f;

        for (int depth = 0; depth < 3 && exit != null && offset < needed; depth++)
        {
            // Una bici detenida dentro del carril de salida ocupa espacio como un auto: si no, el auto entraba al cruce,
            // quedaba detenido detras de ella en el conector y bloqueaba a los demas movimientos
            // (salvo que ya este corriendose para adelantarla)
            bool passing = v.OvertakeState == TrafficVehicle.OvertakePhase.Shifting || v.OvertakeState == TrafficVehicle.OvertakePhase.Holding;
            if (!ignoreBike && !passing && bikeOnLane.TryGetValue(exit, out BikeOnLane bk) && bk.InBand &&
                Mathf.Abs(bk.SpeedAlongMps) < StoppedBikeSpeedMps && offset + bk.S - BikeHalfLengthM < needed)
                return false;
            if (exit.Vehicles.Count > 0)
            {
                var last = exit.Vehicles[0];  // el de S mas bajo = el ultimo de la cola
                float rear = offset + last.S - last.LengthM * 0.5f;
                // Si la cola avanza se puede entrar; salvo que haya un paso peatonal: si la cola se detiene,
                // el auto quedaria encima del paso
                return rear >= needed || (last.Speed > 2f && !crosswalkAhead);
            }
            offset += exit.LengthM;
            exit = exit.NextLanes.Count == 1 ? exit.NextLanes[0].lane : null;
        }
        return true;
    }

    /// <summary>Antibloqueo: si alguien lleva mucho esperando por un conflicto, entra el de menor Id.</summary>
    private void ResolveDeadlocks()
    {
        longWaiters.Clear();
        foreach (var v in vehicles)
        {
            bool waitingConflict = v.Speed < 0.1f &&
                                   (v.LastLeader == "ceder" || v.LastLeader == "conflicto" || v.LastLeader == "cruce");
            if (!waitingConflict) { v.WaitingSinceS = -1.0; continue; }
            if (v.WaitingSinceS < 0.0) v.WaitingSinceS = simTimeS;
            else if (simTimeS - v.WaitingSinceS > deadlockAfterS && !v.ForceEntry) longWaiters.Add(v);
        }
        if (longWaiters.Count == 0) return;

        TrafficVehicle chosen = longWaiters[0];  // 'vehicles' esta en orden de Id
        chosen.ForceEntry = true;
        if (!ignoreBike)  // durante el precalentamiento no se registra
            EventBus.Publish("traffic_deadlock", chosen.Label, string.Format(CultureInfo.InvariantCulture,
                "lane={0};next={1};waited_s={2:F1};reason={3};waiting_vehicles={4};resolution=lowest_id_enters",
                chosen.Lane.name, chosen.NextLane != null ? chosen.NextLane.name : "", simTimeS - chosen.WaitingSinceS,
                chosen.LastLeader, longWaiters.Count));
    }

    // ------------------------------------------------------------
    // Semaforos
    // ------------------------------------------------------------
    /// <summary>
    /// Distancia a la que el frente del auto queda detenido antes de una linea de detencion (m). El IDM se
    /// detiene a s0 (~2 m) de un obstaculo; la linea se trata como un obstaculo s0 - margen mas adelante para
    /// que el auto quede justo antes de la linea pintada y no 2 m atras. [VERIFICAR] con observacion.
    /// </summary>
    private const float LineStopMarginM = 0.5f;

    private static float LineObstacleShift(TrafficVehicle v) => Mathf.Max(v.Params.minGapM - LineStopMarginM, 0f);

    private bool MustStopAt(TrafficVehicle v, StopLine line, float distanceToLineM)
    {
        SignalState s = line.Controller.GetState(line.Approach);
        switch (s.Phase)
        {
            case SignalPhase.Green:
                if (v.YellowDecisionLine == line) v.YellowDecisionLine = null;  // ciclo nuevo
                return v.StartHoldLine == line && simTimeS < v.StartHoldUntilS;

            case SignalPhase.Yellow:
                // Se decide UNA vez por acceso y se mantiene, para que no oscile
                if (v.YellowDecisionLine != line)
                {
                    v.YellowDecisionLine = line;
                    v.YellowDecisionStop = SignalRules.ShouldStopOnYellow(v.Speed, distanceToLineM, yellowSafeDecelMps2);
                }
                return v.YellowDecisionStop;

            default:  // Rojo: para, salvo que en amarillo haya decidido seguir y aun no cruce
                return !(v.YellowDecisionLine == line && !v.YellowDecisionStop);
        }
    }

    /// <summary>Al pasar a verde, el primer auto detenido en la linea puede tardar en arrancar (opcional).</summary>
    private void OnSignalChanged(IntersectionController controller, Approach approach, SignalPhase phase)
    {
        if (!startUpDelayEnabled || phase != SignalPhase.Green) return;

        foreach (var line in stopLines)
        {
            if (line.Controller != controller || line.Approach != approach) continue;

            // Primero de la cola = auto detenido cuyo lider era esta linea (orden de Id: determinista)
            TrafficVehicle first = null;
            foreach (var v in vehicles)
                if (v.LastStopLineLeader == line && v.Speed < 0.5f && v.LastGapM < 10f &&
                    (first == null || v.LastGapM < first.LastGapM))
                    first = v;
            if (first == null) continue;

            float delay = Mathf.Max(0f, startUpDelayMeanS + startUpDelaySdS * DriverProfile.StandardNormal(startUpRng));
            first.StartHoldLine = line;
            first.StartHoldUntilS = simTimeS + delay;
        }
    }

    // ------------------------------------------------------------
    // Ciclista proyectado sobre los carriles
    // ------------------------------------------------------------
    private void UpdateBikeProjection()
    {
        bikeOnLane.Clear();
        if (bike == null || ignoreBike) return;

        Vector3 b = bike.RiderPosition;
        Vector3 bikeFwd = bike.transform.forward;
        bikeFwd.y = 0f;
        bikeFwd.Normalize();
        float bikeSpeed = bike.CurrentSpeedMps;

        foreach (var lane in lanes)
        {
            lane.Project(b, out float s, out float lateral);
            bool near = s > 0.01f && s < lane.LengthM - 0.01f && Mathf.Abs(lateral) <= BikeNearLateralM;
            if (!near) continue;

            lane.GetPose(s, 0f, out _, out Quaternion laneRot);
            Vector3 laneFwd = laneRot * Vector3.forward;
            laneFwd.y = 0f;
            laneFwd.Normalize();
            float along = bikeSpeed * Vector3.Dot(bikeFwd, laneFwd);
            // Lo que ocupa la bici a lo ancho del carril: con la bici atravesada, su largo (antes solo se miraba
            // el punto del ciclista y una rueda podia quedar dentro del carril sin que el auto la viera)
            float cos = Mathf.Abs(Vector3.Dot(bikeFwd, laneFwd));
            float halfAcross = Mathf.Max(handlebarHalfWidthM, BikeHalfLengthM * Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos)) + handlebarHalfWidthM * cos);
            bool inBand = Mathf.Abs(lateral) - halfAcross <= lane.LaneWidthM * 0.5f + bikeLaneMarginM - handlebarHalfWidthM;
            bikeOnLane[lane] = new BikeOnLane { S = s, SpeedAlongMps = along, Lateral = lateral, InBand = inBand, HalfAcrossM = halfAcross };
        }
    }

    /// <summary>Peatones en la calzada fuera de un paso peatonal (cruce scriptado), proyectados sobre los carriles cercanos.</summary>
    private void UpdatePedestrianProjection()
    {
        foreach (var l in pedOnLane.Values) l.Clear();
        var pm = PedestrianManager.Instance;
        if (pm == null) return;
        foreach (var p in pm.Agents)
        {
            if (!p.OnRoad) continue;
            Vector3 pos = p.Position;
            foreach (var lane in lanes)
            {
                if (!lane.WorldBounds.Contains(pos)) continue;
                lane.Project(pos, out float s, out float lateral);
                if (s <= 0.01f || s >= lane.LengthM - 0.01f || Mathf.Abs(lateral) > lane.LaneWidthM * 0.5f + 0.8f) continue;
                if (!pedOnLane.TryGetValue(lane, out var list)) pedOnLane[lane] = list = new List<float>();
                list.Add(s);
            }
        }
    }

    public bool TryGetBikeOnLane(TrafficLane lane, out float s, out float speedAlongMps)
    {
        bool found = bikeOnLane.TryGetValue(lane, out BikeOnLane bk) && bk.InBand;
        s = bk.S;
        speedAlongMps = bk.SpeedAlongMps;
        return found;
    }

    // ------------------------------------------------------------
    // Alta y baja de vehiculos
    // ------------------------------------------------------------
    public TrafficVehicle SpawnVehicle(TrafficVehicle prefab, TrafficLane lane, float s, float speed,
                                       IdmParams p, float speedFactor)
    {
        TrafficVehicle v;
        if (pools.TryGetValue(prefab, out var pool) && pool.Count > 0)
        {
            v = pool.Pop();
            v.gameObject.SetActive(true);
        }
        else
        {
            v = Instantiate(prefab, transform);
            v.Prefab = prefab;
        }

        v.ResetForSpawn(nextId++);
        v.name = $"{prefab.name}_{v.Id}";
        v.Lane = lane;
        v.S = s;
        v.Speed = speed;
        v.Params = p;
        v.SpeedFactor = speedFactor;
        v.NextLane = lane.PickNext(routingRng);
        v.NextNextLane = v.NextLane != null ? v.NextLane.PickNext(routingRng) : null;
        v.SnapPose();

        lane.Vehicles.Insert(0, v);
        vehicles.Add(v);
        return v;
    }

    private void FlushRemovals()
    {
        foreach (var v in toRemove)
        {
            v.Lane?.Vehicles.Remove(v);
            vehicles.Remove(v);
            ReturnToPool(v);
        }
        toRemove.Clear();
    }

    private void ReturnToPool(TrafficVehicle v)
    {
        v.Lane = null;
        v.NextLane = null;
        v.gameObject.SetActive(false);

        if (!pools.TryGetValue(v.Prefab, out var pool))
        {
            pool = new Stack<TrafficVehicle>();
            pools.Add(v.Prefab, pool);
        }
        pool.Push(v);
    }

    /// <summary>Insertion sort por S: las listas casi siempre ya estan ordenadas, asi que es O(n).</summary>
    private static void SortLane(TrafficLane lane)
    {
        var list = lane.Vehicles;
        for (int i = 1; i < list.Count; i++)
        {
            var key = list[i];
            int j = i - 1;
            while (j >= 0 && list[j].S > key.S) { list[j + 1] = list[j]; j--; }
            list[j + 1] = key;
        }
    }

    // ------------------------------------------------------------
    // Adelantamiento al ciclista (T4)
    // ------------------------------------------------------------

    /// <summary>
    /// Free: el ciclista es obstaculo si esta en la trayectoria fisica del auto (segun su posicion lateral),
    /// o si va dentro del carril y el auto todavia no lo adelanta (lo sigue hasta poder hacerlo).
    /// Disabled: si va dentro del carril (comportamiento previo a T4).
    /// </summary>
    private bool BikeIsObstacle(TrafficVehicle v, BikeOnLane bk, float gapToBikeM)
    {
        if (overtakingMode == AmbientOvertakingMode.Disabled) return bk.InBand;

        float now = SideClearance(v, bk, v.LateralOffset);
        switch (v.OvertakeState)
        {
            case TrafficVehicle.OvertakePhase.Shifting:
            case TrafficVehicle.OvertakePhase.Holding:
                // No la pasa hasta tener la distancia lateral planificada (o la que permite una maniobra parcial)
                float need = v.OvertakeManeuver == "partial" ? SideClearance(v, bk, v.LatTo) - 0.05f
                           : float.IsNaN(v.PlannedPassingDistanceM) ? MinPassClearanceM
                           : v.PlannedPassingDistanceM - 0.05f;
                // Bici detenida: se la pasa como a un obstaculo quieto, con una distancia minima de 1 m
                if (Mathf.Abs(bk.SpeedAlongMps) < StoppedBikeSpeedMps) need = Mathf.Min(need, StoppedBikeClearanceM);
                // La maniobra avanza con el avance del auto: cuenta la posicion lateral que tendra al llegar a la bici
                // (si no, un auto detenido detras no podria correrse ni avanzar)
                // Solo cuenta el tramo que excede su distancia de frenado comodo: si no alcanza, todavia puede detenerse
                float brake = v.Speed * v.Speed / (2f * Mathf.Max(v.Params.comfortDecelMps2, 0.5f));
                return SideClearance(v, bk, LateralWhenReaching(v, gapToBikeM - brake)) < Mathf.Max(need, MinPassClearanceM);
            case TrafficVehicle.OvertakePhase.Returning:
                // Si la bici sigue adelante, cuenta tambien donde va a quedar el auto al volver
                return Mathf.Min(now, SideClearance(v, bk, v.LatTo)) < MinPassClearanceM;
            default:
                // Sin maniobra: la sigue si va dentro del carril o si no hay espacio lateral seguro
                return bk.InBand || now < MinPassClearanceM;
        }
    }

    private const float StoppedBikeSpeedMps = 0.5f;
    private const float PullOutSpeedMps = 3f;
    private const float MaxWaitBehindStoppedBikeM = 10f;

    /// <summary>Pendiente lateral maxima segun la velocidad: lowSpeedOvertakeHeadingDeg detenido, maxOvertakeHeadingDeg desde 5 m/s.</summary>
    private float SlopeMax(float speed) =>
        Mathf.Tan(Mathf.Lerp(lowSpeedOvertakeHeadingDeg, maxOvertakeHeadingDeg, Mathf.Clamp01(speed / 5f)) * Mathf.Deg2Rad);

    /// <summary>
    /// Distancia a la que un auto se detiene detras de una bici detenida que piensa adelantar: la que necesita para
    /// salir despues (largo minimo de la maniobra a baja velocidad), no s0. Pegado a la bici ya no podria correrse.
    /// </summary>
    private float WaitGapBehindStoppedBike(TrafficVehicle v, BikeOnLane bk)
    {
        float planned = float.IsNaN(v.PlannedPassingDistanceM) ? passingMedianM : v.PlannedPassingDistanceM;
        float target = OvertakeRules.RequiredCarLateral(bk.Lateral, Mathf.Max(handlebarHalfWidthM, bk.HalfAcrossM),
                                                        v.WidthM * 0.5f, planned);
        float delta = Mathf.Abs(Mathf.Min(target, 0f) - v.LateralOffset);
        return Mathf.Min(OvertakeRules.MinManeuverLength(delta, SlopeMax(0f)), MaxWaitBehindStoppedBikeM);
    }
    private const float StoppedBikeClearanceM = 1.0f;

    /// <summary>
    /// Posicion lateral que tendra el auto despues de avanzar 'aheadM' metros siguiendo su maniobra en curso
    /// (dx/ds = ritmo / v; a baja velocidad, 1 / L_min).
    /// </summary>
    private float LateralWhenReaching(TrafficVehicle v, float aheadM)
    {
        if (v.LatDurationS <= 0f) return v.LateralOffset;
        float delta = v.LatTo - v.LatFrom;
        float slopeMax = SlopeMax(v.Speed);
        float lMin = OvertakeRules.MinManeuverLength(delta, slopeMax);
        float dxds = v.Speed > 0.5f ? OvertakeRules.LateralProgressRate(v.Speed, v.LatDurationS, delta, slopeMax) / v.Speed
                                    : (lMin > 1e-4f ? 1f / lMin : 1f);
        float x = Mathf.Min(1f, v.LatProgress + Mathf.Max(aheadM, 0f) * dxds);
        return v.LatFrom + delta * OvertakeRules.Quintic(x);
    }

    /// <summary>
    /// Un adelantamiento que sale del carril (contrario o lado izquierdo de un sentido unico) debe poder terminarse
    /// -pasar la bici, el margen y volver- antes de la proxima interseccion. Si no, el auto podia quedar detenido en
    /// el carril contrario esperando el semaforo y bloquear a los que vienen de frente.
    /// </summary>
    private bool CanCompleteBeforeIntersection(TrafficVehicle v, float lateral, float gapAhead, float passSpeed, float duration)
    {
        float returnLen = Mathf.Max(OvertakeRules.MinManeuverLength(lateral, SlopeMax(passSpeed)), passSpeed * duration);
        float needed = gapAhead + v.LengthM + 2f * BikeHalfLengthM + returnMarginM + returnLen;
        return OvertakeRoomAhead(v, passSpeed) >= needed;
    }

    /// <summary>
    /// Espacio para completar un adelantamiento: hasta el proximo conector; o, si el auto sigue recto y puede
    /// cruzar sin detenerse (prioridad, o semaforo en verde con tiempo de llegar + 2 s), tambien el conector y la
    /// salida (puede terminar de volver dentro del cruce).
    /// </summary>
    private static float OvertakeRoomAhead(TrafficVehicle v, float passSpeed)
    {
        if (v.Lane.IsConnector) return 0f;
        float d = v.Lane.LengthM - (v.S + v.LengthM * 0.5f);
        TrafficLane entry = v.Lane, lane = v.NextLane;
        for (int i = 0; i < 3 && lane != null; i++)
        {
            if (lane.IsConnector)
            {
                if (lane.Movement != MovementKind.Straight) return d;
                StopLine line = entry.StopLine;
                if (line != null && (line.Control == ApproachControl.Stop || line.Control == ApproachControl.Yield)) return d;
                if (line != null && line.IsSignalized)
                {
                    SignalState st = line.Controller.GetState(line.Approach);
                    if (st.Phase != SignalPhase.Green || d / Mathf.Max(passSpeed, 1f) + 2f > st.TimeToChangeS) return d;
                }
                float through = d + lane.LengthM;
                if (lane.NextLanes.Count > 0 && lane.NextLanes[0].lane != null) through += lane.NextLanes[0].lane.LengthM;
                return through;
            }
            d += lane.LengthM;
            entry = lane;
            lane = i == 0 ? v.NextNextLane : (lane.NextLanes.Count == 1 ? lane.NextLanes[0].lane : null);
        }
        return float.PositiveInfinity;
    }

    private const float ReverseSpeedMps = 0.8f;

    /// <summary>
    /// Auto detenido pegado a una bici detenida (p. ej., el ciclista freno justo delante): pegado no puede
    /// correrse para adelantarla. Si hay espacio detras, retrocede despacio hasta la distancia que necesita.
    /// </summary>
    private void ReverseForRoom(TrafficVehicle v, float dt)
    {
        if (v.Speed > 0.05f || v.LastLeader != "ciclista") return;
        if (!bikeOnLane.TryGetValue(v.Lane, out BikeOnLane bk) || !bk.InBand ||
            Mathf.Abs(bk.SpeedAlongMps) >= StoppedBikeSpeedMps || bk.S <= v.S) return;

        float rear = v.S - v.LengthM * 0.5f;
        float realGap = bk.S - BikeHalfLengthM - (v.S + v.LengthM * 0.5f);
        if (realGap >= WaitGapBehindStoppedBike(v, bk) - 0.3f || rear < 1f) return;
        foreach (var o in v.Lane.Vehicles)
            if (o != v && o.S < v.S && rear - (o.S + o.LengthM * 0.5f) < o.Params.minGapM + 1f) return;  // sin espacio detras
        v.S -= ReverseSpeedMps * dt;
    }

    /// <summary>Velocidad con que el auto pasa a la bici (estima el tramo de vuelta): cerca de la deseada si la bici va en marcha.</summary>
    private static float PassSpeed(TrafficVehicle v, BikeOnLane bk) =>
        Mathf.Abs(bk.SpeedAlongMps) < StoppedBikeSpeedMps ? Mathf.Max(v.Speed, 4f)
                                                          : Mathf.Max(v.Speed, 0.8f * v.Params.desiredSpeedMps);

    /// <summary>Distancia lateral libre entre el costado del auto (en carLateral) y el extremo del manubrio.</summary>
    private float SideClearance(TrafficVehicle v, BikeOnLane bk, float carLateral)
    {
        float carHalf = v.WidthM * 0.5f;
        float half = Mathf.Max(handlebarHalfWidthM, bk.HalfAcrossM);
        return bk.Lateral >= carLateral
            ? OvertakeRules.Clearance(bk.Lateral, carLateral, half, carHalf)
            : (carLateral - carHalf) - (bk.Lateral + half);
    }

    /// <summary>Distancia lateral minima con la que un auto pasa junto a la bici sin tratarla como obstaculo (m).</summary>
    private const float MinPassClearanceM = 0.5f;

    private void UpdateOvertaking(float dt)
    {
        opposingOvertakers.Clear();
        foreach (var v in vehicles)
        {
            AdvanceLateral(v, dt);
            if (overtakingMode == AmbientOvertakingMode.Free && !ignoreBike && bike != null) { DecideOvertake(v); ReverseForRoom(v, dt); }
            else if (v.OvertakeState == TrafficVehicle.OvertakePhase.Shifting || v.OvertakeState == TrafficVehicle.OvertakePhase.Holding)
                StartLateral(v, 0f, TrafficVehicle.OvertakePhase.Returning);

            if (v.OvertakeManeuver == "opposing" && v.OvertakeState != TrafficVehicle.OvertakePhase.None)
                opposingOvertakers.Add(v);
        }
    }

    /// <summary>
    /// Avanza el perfil quintico de la maniobra lateral en curso. El avance esta acoplado al avance
    /// longitudinal (un auto no se corre de lado sin avanzar): por tick dx = min(dt/T, ds/L_min), con
    /// L_min el largo minimo para no superar maxOvertakeHeadingDeg (OvertakeRules.MinManeuverLength).
    /// Detenido, la maniobra se congela. LatSlope = dLat/ds orienta el auto (TrafficVehicle.ApplyPose).
    /// </summary>
    private void AdvanceLateral(TrafficVehicle v, float dt)
    {
        if (v.LatDurationS <= 0f) { v.LatSlope = 0f; return; }
        float delta = v.LatTo - v.LatFrom;
        float slopeMax = SlopeMax(v.Speed);
        float dxdt = OvertakeRules.LateralProgressRate(v.Speed, v.LatDurationS, delta, slopeMax);
        v.LatProgress += dxdt * dt;
        float x = v.LatProgress;
        v.LateralOffset = v.LatFrom + delta * OvertakeRules.Quintic(x);
        v.LatSlope = OvertakeRules.LateralSlope(v.Speed, v.LatDurationS, delta, slopeMax, x);
        if (x < 1f) return;

        v.LateralOffset = v.LatTo;
        v.LatSlope = 0f;
        v.LatDurationS = 0f;
        if (v.OvertakeState == TrafficVehicle.OvertakePhase.Shifting) v.OvertakeState = TrafficVehicle.OvertakePhase.Holding;
        else if (v.OvertakeState == TrafficVehicle.OvertakePhase.Returning) v.OvertakeState = TrafficVehicle.OvertakePhase.None;
    }

    private void StartLateral(TrafficVehicle v, float target, TrafficVehicle.OvertakePhase phase)
    {
        v.LatFrom = v.LateralOffset;
        v.LatTo = target;
        v.LatStartS = simTimeS;
        v.LatProgress = 0f;
        v.LatDurationS = OvertakeRules.ManeuverDuration(target - v.LateralOffset, maxLateralAccelMps2,
                                                        maneuverDurationS.x, maneuverDurationS.y);
        v.OvertakeState = phase;
    }

    private void DecideOvertake(TrafficVehicle v)
    {
        // Ciclista respecto de la ruta del auto: carril actual o los siguientes (los carriles de una
        // interseccion son cortos y la bici puede estar varios carriles mas adelante)
        float offset = 0f;
        TrafficLane lane = v.Lane;
        BikeOnLane bk = default;
        bool found = false;
        bool acrossIntersection = false;   // entre el auto y la bici hay un conector
        for (int i = 0; i < 4 && lane != null; i++)
        {
            if (bikeOnLane.TryGetValue(lane, out bk)) { found = true; break; }
            if (lane.IsConnector) acrossIntersection = true;
            offset += lane.LengthM;
            if (offset - v.S > 60f) break;
            lane = i == 0 ? v.NextLane : i == 1 ? v.NextNextLane
                 : (lane.NextLanes.Count == 1 ? lane.NextLanes[0].lane : null);
        }
        if (!found)
        {
            // Durante una maniobra, no volver al centro hasta haberla pasado o alejarse (en planta)
            if (v.OvertakeState == TrafficVehicle.OvertakePhase.Shifting || v.OvertakeState == TrafficVehicle.OvertakePhase.Holding)
            {
                v.Lane.GetPose(v.S, v.LateralOffset, out Vector3 cp, out Quaternion cr);
                Vector3 d = bike.RiderPosition - cp; d.y = 0f;
                float along = Vector3.Dot(d, cr * Vector3.forward);
                bool passed = along < -(v.LengthM * 0.5f + BikeHalfLengthM + returnMarginM);
                if (!passed && d.magnitude < 40f) return;  // sigue desplazado
            }
            EndEncounter(v);
            return;
        }

        float carHalfLen = v.LengthM * 0.5f;
        float bikeFrontS = offset + bk.S + BikeHalfLengthM;
        float bikeRearS = offset + bk.S - BikeHalfLengthM;
        float carFront = v.S + carHalfLen, carRear = v.S - carHalfLen;
        float gapAhead = bikeRearS - carFront;           // > 0: la bici esta adelante
        float passedBy = carRear - bikeFrontS;           // > 0: el auto ya la paso

        // Ya la paso: volver al centro y cerrar el encuentro
        if (passedBy > returnMarginM)
        {
            if (v.OvertakeState == TrafficVehicle.OvertakePhase.Shifting || v.OvertakeState == TrafficVehicle.OvertakePhase.Holding)
                StartLateral(v, 0f, TrafficVehicle.OvertakePhase.Returning);
            return;
        }

        // Abortar si invade el contrario y el de frente se acerca demasiado, pero SOLO si aun no alcanza
        // a la bici: si ya va a su lado, volver al carril la encerraria; completa la pasada (el de frente
        // lo ve como obstaculo y frena, ver OncomingOvertakerObstacle)
        if (v.OvertakeManeuver == "opposing" &&
            (v.OvertakeState == TrafficVehicle.OvertakePhase.Shifting || v.OvertakeState == TrafficVehicle.OvertakePhase.Holding))
        {
            float ttcNow = OncomingTtc(v);
            // ... y solo si alcanza a detenerse detras de la bici antes de volver a su carril
            float stopDist = v.Speed * v.Speed / (2f * yellowSafeDecelMps2) + v.Params.minGapM;
            if (ttcNow < abortOncomingTtcS && gapAhead > stopDist)
            {
                v.OvertakeManeuver = "aborted";
                StartLateral(v, 0f, TrafficVehicle.OvertakePhase.Returning);
                EventBus.Publish("overtake_abort", v.Label, string.Format(CultureInfo.InvariantCulture,
                    "source=ambient;oncoming_ttc_s={0:F2};lateral_m={1:F2};lane={2}", ttcNow, v.LateralOffset, v.Lane.name));
            }
            return;
        }

        // Maniobra en curso. Excepcion: volvio (o abortaba) y quedo detenido con la bici detenida adelante; volviendo
        // no puede moverse de lado sin avanzar, asi que puede decidir de nuevo desde donde esta
        bool stuckReturning = v.OvertakeState == TrafficVehicle.OvertakePhase.Returning && v.Speed < 0.3f &&
                              gapAhead > 0f && Mathf.Abs(bk.SpeedAlongMps) < StoppedBikeSpeedMps;
        if (v.OvertakeState != TrafficVehicle.OvertakePhase.None && !stuckReturning) return;
        // No se adelanta dentro de intersecciones ni a traves de ellas (bici justo despues del cruce: espera)
        if (lane.IsConnector || v.Lane.IsConnector || acrossIntersection) return;

        float decisionRange = Mathf.Max(15f, v.Speed * 3.5f);
        if (gapAhead < 0f || gapAhead > decisionRange) return;

        // Solo se adelanta a un ciclista que va en el mismo sentido y a la derecha del auto (o dentro de su carril).
        // Uno a la izquierda o de frente no es un adelantamiento: si se cruza en la trayectoria, el IDM frena.
        if (bk.SpeedAlongMps < -0.5f || (!bk.InBand && bk.Lateral < 0f)) return;

        // Distancia deseada: se sortea una vez por encuentro
        if (float.IsNaN(v.PlannedPassingDistanceM))
        {
            float sigma = OvertakeRules.SigmaFromIqr(passingIqrM.x, passingIqrM.y);
            v.PlannedPassingDistanceM = OvertakeRules.SamplePassingDistance(overtakeRng, passingMedianM, sigma,
                                                                            passingClampM.x, passingClampM.y);
            v.OvertakeManeuver = "none";
            v.OvertakeOncomingTtcS = float.PositiveInfinity;
        }

        float carHalfW = v.WidthM * 0.5f;
        float target = OvertakeRules.RequiredCarLateral(bk.Lateral, handlebarHalfWidthM, carHalfW, v.PlannedPassingDistanceM);
        if (target >= 0f) { v.OvertakeManeuver = "none"; return; }  // la geometria ya da la distancia

        // Si la bici va dentro del carril, primero la sigue t_follow y solo adelanta si es mas rapido
        // Bici detenida: se decide de inmediato (seguirla t_follow solo hacia que el auto quedara pegado a ella)
        if (bk.InBand && Mathf.Abs(bk.SpeedAlongMps) >= StoppedBikeSpeedMps)
        {
            bool following = v.LastLeader == "ciclista";
            if (following && v.FollowingBikeSinceS < 0.0) v.FollowingBikeSinceS = simTimeS;
            if (!following) v.FollowingBikeSinceS = -1.0;
            bool waitedEnough = following && simTimeS - v.FollowingBikeSinceS >= v.FollowBeforeOvertakeS;
            bool faster = v.Params.desiredSpeedMps - bk.SpeedAlongMps > overtakeSpeedDiffMps;
            if (!waitedEnough || !faster) return;
        }

        float need = -target;
        TrafficLane cur = v.Lane;
        float ownRoom = (cur.IsOneWay ? cur.LaneWidthM * 0.5f : cur.OpposingOffsetM * 0.5f) - carHalfW - 0.1f;
        float oneWayRoom = cur.LeftCurbM - carHalfW - 0.3f;

        if (need <= ownRoom) { BeginOvertake(v, target, "in_lane", bk, gapAhead); return; }
        if (cur.IsOneWay)
        {
            if (need <= oneWayRoom && CanCompleteBeforeIntersection(v, need, gapAhead, PassSpeed(v, bk),
                    OvertakeRules.ManeuverDuration(need, maxLateralAccelMps2, maneuverDurationS.x, maneuverDurationS.y)))
            { BeginOvertake(v, target, "one_way", bk, gapAhead); return; }
            StartPartialOrFollow(v, bk, -oneWayRoom, gapAhead);
            return;
        }

        // Doble sentido: invadir el contrario solo con brecha suficiente con el trafico de frente
        float rel = Mathf.Max(v.Params.desiredSpeedMps - Mathf.Max(bk.SpeedAlongMps, 0f), 1f);
        float duration = OvertakeRules.ManeuverDuration(need, maxLateralAccelMps2, maneuverDurationS.x, maneuverDurationS.y);
        // Duracion real: a baja velocidad la maniobra se alarga para no superar el angulo maximo (AdvanceLateral).
        // Se estima con la velocidad que tendra al salir (>= 3 m/s): con la velocidad actual de un auto detenido
        // daba ~50 s y nunca encontraba brecha con el trafico de frente
        float vOut = Mathf.Max(v.Speed, PullOutSpeedMps);
        float effective = 1f / OvertakeRules.LateralProgressRate(vOut, duration, need, SlopeMax(vOut));
        // Tiempo para recorrer el adelantamiento: a la velocidad relativa, o (si arranca lento) acelerando desde v
        float passDist = gapAhead + 2f * carHalfLen + 2f * BikeHalfLengthM + returnMarginM;
        float acc = Mathf.Max(v.Params.maxAccelMps2, 0.5f);
        float tAccel = (-v.Speed + Mathf.Sqrt(v.Speed * v.Speed + 2f * acc * passDist)) / acc;
        // El desplazamiento inicial ocurre mientras avanza (no se suma); despues, la vuelta a su carril
        float passTime = Mathf.Max(Mathf.Max(passDist / rel, tAccel), effective) + duration;
        float required = Mathf.Max(minOncomingTtcS, passTime + oncomingTtcMarginS);
        float ttc = OncomingTtc(v);
        v.OvertakeOncomingTtcS = ttc;
        if (need <= cur.OpposingOffsetM && ttc >= required &&
            CanCompleteBeforeIntersection(v, need, gapAhead, PassSpeed(v, bk), duration))
        {
            BeginOvertake(v, target, "opposing", bk, gapAhead);
            return;
        }
        StartPartialOrFollow(v, bk, -ownRoom, gapAhead);
    }

    /// <summary>
    /// No cabe la distancia deseada: si la bici esta en la trayectoria (dentro del carril) la sigue;
    /// si va fuera (ciclovia), pasa corriendose lo que permite su carril.
    /// </summary>
    private void StartPartialOrFollow(TrafficVehicle v, BikeOnLane bk, float maxLeft, float gapAhead)
    {
        if (bk.InBand) return;  // la sigue con el IDM (BikeIsObstacle) y vuelve a evaluar en el proximo tick
        if (maxLeft >= -0.05f) { v.OvertakeManeuver = "none"; return; }
        BeginOvertake(v, maxLeft, "partial", bk, gapAhead);
    }

    /// <summary>Inicia la maniobra lateral y registra la decision (overtake_start).</summary>
    private void BeginOvertake(TrafficVehicle v, float target, string maneuver, BikeOnLane bk, float gapAhead)
    {
        v.OvertakeManeuver = maneuver;
        StartLateral(v, target, TrafficVehicle.OvertakePhase.Shifting);
        float expected = OvertakeRules.Clearance(bk.Lateral, target, handlebarHalfWidthM, v.WidthM * 0.5f);
        EventBus.Publish("overtake_start", v.Label, string.Format(CultureInfo.InvariantCulture,
            "source=ambient;maneuver={0};planned_m={1:F2};expected_m={2:F2};target_lateral_m={3:F2};bike_lateral_m={4:F2};" +
            "bike_in_lane={5};gap_ahead_m={6:F1};car_speed_mps={7:F2};bike_speed_mps={8:F2};oncoming_ttc_s={9:F2};" +
            "duration_s={10:F2};lane={11}",
            maneuver, v.PlannedPassingDistanceM, expected, target, bk.Lateral, bk.InBand ? 1 : 0, gapAhead, v.Speed,
            bk.SpeedAlongMps, v.OvertakeOncomingTtcS, v.LatDurationS, v.Lane.name));
    }

    /// <summary>El auto ya no esta cerca del ciclista: termina el encuentro (la proxima vez se sortea otra distancia).</summary>
    private void EndEncounter(TrafficVehicle v)
    {
        if (v.OvertakeState == TrafficVehicle.OvertakePhase.Shifting || v.OvertakeState == TrafficVehicle.OvertakePhase.Holding)
            StartLateral(v, 0f, TrafficVehicle.OvertakePhase.Returning);
        if (v.OvertakeState == TrafficVehicle.OvertakePhase.None)
        {
            v.PlannedPassingDistanceM = float.NaN;
            v.FollowingBikeSinceS = -1.0;
        }
    }

    /// <summary>Tiempo a colision con el primer auto que viene de frente por el carril contrario (geometria en planta).</summary>
    private float OncomingTtc(TrafficVehicle v)
    {
        v.Lane.GetPose(v.S, v.LateralOffset, out Vector3 p, out Quaternion r);
        Vector3 f = r * Vector3.forward; f.y = 0f; f.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, f);
        float lanes = v.Lane.OpposingOffsetM > 0f ? v.Lane.OpposingOffsetM : 4f;
        float best = float.PositiveInfinity;
        TrafficLane opp = v.Lane.OpposingLane;
        if (opp != null)
        {
            // Por el trazado del carril contrario (sirve tambien en curvas, donde la busqueda en linea recta perdia
            // autos): los que estan en el antes de nuestra posicion y los que van a entrar a el
            opp.Project(p, out float sOpp, out _);
            foreach (var o in vehicles)
            {
                if (o == v) continue;
                float along;
                if (o.Lane == opp) along = sOpp - o.S;
                else if (o.NextLane == opp) along = sOpp + (o.Lane.LengthM - o.S);
                else if (o.NextNextLane == opp && o.NextLane != null) along = sOpp + (o.Lane.LengthM - o.S) + o.NextLane.LengthM;
                else continue;
                if (along <= 0f || along > 250f) continue;
                float ov = o.Lane == opp ? o.Speed : Mathf.Max(o.Speed, PullOutSpeedMps);
                float ttc = OvertakeRules.OncomingTtc(along - (v.LengthM + o.LengthM) * 0.5f, v.Speed, ov);
                if (ttc < best) best = ttc;
            }
        }
        foreach (var o in vehicles)
        {
            if (o == v) continue;
            o.Lane.GetPose(o.S, o.LateralOffset, out Vector3 po, out Quaternion ro);
            Vector3 d = po - p; d.y = 0f;
            float along = Vector3.Dot(d, f);
            if (along <= 0f || along > 200f) continue;
            // Autos que van a entrar al carril contrario desde una interseccion (aun no van de frente ni en su franja)
            bool entering = opp != null && o.Lane != opp && (o.NextLane == opp || o.NextNextLane == opp);
            if (!entering)
            {
                Vector3 fo = ro * Vector3.forward; fo.y = 0f;
                if (Vector3.Dot(fo.normalized, f) > -0.5f) continue;   // no viene de frente
                float lat = Vector3.Dot(d, right);                       // carril contrario: ~ -lanes
                if (Mathf.Abs(lat + lanes) > 2.5f) continue;
            }
            float ttc = OvertakeRules.OncomingTtc(along - (v.LengthM + o.LengthM) * 0.5f, v.Speed,
                                                  entering ? Mathf.Max(o.Speed, PullOutSpeedMps) : o.Speed);
            if (ttc < best) best = ttc;
        }
        return best;
    }

    /// <summary>Un auto que adelanta por el carril de v, en sentido contrario, es un obstaculo que se acerca.</summary>
    private void OncomingOvertakerObstacle(TrafficVehicle v, ref float gap, ref float leaderSpeed, ref string leaderName)
    {
        // El que adelanta por el carril contrario tambien frena ante cualquiera que venga de frente por su trayectoria
        // (p. ej. uno que entro a ese carril desde una interseccion despues de decidir)
        bool selfOvertaking = v.OvertakeManeuver == "opposing" && v.OvertakeState != TrafficVehicle.OvertakePhase.None &&
                              v.LateralOffset < -0.5f;
        if (opposingOvertakers.Count == 0 && !selfOvertaking) return;
        IReadOnlyList<TrafficVehicle> candidates = selfOvertaking ? vehicles : opposingOvertakers;
        v.Lane.GetPose(v.S, v.LateralOffset, out Vector3 p, out Quaternion r);
        Vector3 f = r * Vector3.forward; f.y = 0f; f.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, f);
        foreach (var w in candidates)
        {
            if (w == v) continue;
            w.Lane.GetPose(w.S, w.LateralOffset, out Vector3 pw, out Quaternion rw);
            Vector3 fw = rw * Vector3.forward; fw.y = 0f;
            if (Vector3.Dot(fw.normalized, f) > -0.5f) continue;
            Vector3 d = pw - p; d.y = 0f;
            float along = Vector3.Dot(d, f);
            if (along <= 0f) continue;
            if (Mathf.Abs(Vector3.Dot(d, right)) > (v.WidthM + w.WidthM) * 0.5f + 0.5f) continue;  // no ocupa su trayectoria
            float g = along - (v.LengthM + w.LengthM) * 0.5f;
            if (g < gap) { gap = Mathf.Max(g, 0f); leaderSpeed = -w.Speed; leaderName = selfOvertaking ? "de_frente" : "adelanta_de_frente"; }
        }
    }
}
