// ============================================================
// PedestrianManager.cs
// Peatones ambientales (T6). Cada FixedUpdate avanza a todos los peatones
// sobre caminos del NavMesh (areas "Vereda" y "Cruce"; la calzada no es
// caminable, asi que solo cruzan por los pasos peatonales):
//   - Cantidad = densidad (peatones / 100 m de vereda) x largo de vereda / 100.
//     La densidad es configurable por condicion e independiente del trafico.
//   - Velocidad propia N(1.35, 0.20) m/s en [0.9, 1.8] (Knoblauch et al., 1996).
//   - Destinos al azar sobre las veredas (30-200 m); al llegar, eligen otro
//     (a veces se detienen 2-8 s). Nunca aparecen ni desaparecen a la vista.
//   - Pasos con semaforo: solo empiezan con "caminar" si alcanzan a terminar.
//     Pasos cebra: empiezan si los autos que se acercan pueden detenerse.
//   - Ceden al ciclista si lo tienen justo adelante (no los scriptados).
//   - Movimiento con el modelo de fuerza social (SocialForce): impulso hacia
//     la esquina siguiente del camino + repulsion de los otros peatones, del
//     ciclista y de los autos junto al cruce. La posicion se ajusta al NavMesh
//     (no salen de la vereda) y nadie pisa la calzada de un paso sin permiso.
//     Esperar o ceder = velocidad deseada 0 (el modelo los separa igual).
// Todo el azar con System.Random con semilla (TrafficManager.CreateRng).
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;

[DefaultExecutionOrder(15)]  // antes de TrafficManager (20): los autos ven a los peatones ya movidos
public class PedestrianManager : MonoBehaviour
{
    [Header("Densidad (configurable por condicion)")]
    [Tooltip("Peatones por cada 100 m de vereda.")]
    [SerializeField, Min(0f)] private float pedestriansPer100m = 2f;
    [Tooltip("Largo total de vereda (m). Lo calcula SimBici > Build Pedestrian Setup.")]
    [SerializeField] private float sidewalkLengthM = 0f;
    [Tooltip("Zonas de vereda donde aparecer y elegir destinos (las calcula el setup).")]
    [SerializeField] private List<Bounds> sidewalkAreas = new List<Bounds>();

    [Header("Avatares")]
    [SerializeField] private List<PedestrianAgent> prefabs = new List<PedestrianAgent>();

    [Header("Marcha (Knoblauch et al., 1996)")]
    [SerializeField] private float speedMeanMps = PedestrianRules.SpeedMean;
    [SerializeField] private float speedSdMps = PedestrianRules.SpeedSd;
    [SerializeField] private Vector2 speedRangeMps = new Vector2(PedestrianRules.SpeedMin, PedestrianRules.SpeedMax);
    [SerializeField] private Vector2 destinationDistanceM = new Vector2(30f, 200f);
    [SerializeField, Range(0f, 1f)] private float idleProbability = 0.3f;
    [Tooltip("Probabilidad de aceptar un destino que obliga a cruzar la calzada (1 = cualquier destino). Con valores " +
             "bajos los peatones pasean mas por su lado de la calle y no se acumulan en los pocos pasos peatonales. [VERIFICAR]")]
    [SerializeField, Range(0f, 1f)] private float crossingProbability = 0.35f;
    [SerializeField] private Vector2 idleDurationS = new Vector2(2f, 8f);

    [Header("Cruces")]
    [Tooltip("Paso cebra: el peaton no cruza frente a un auto que llega antes de esto (s) y no alcanza a frenar.")]
    [SerializeField] private float zebraPedestrianGapS = 4f;
    [Tooltip("Desaceleracion comoda con la que un auto puede detenerse ante un paso cebra (m/s2).")]
    [SerializeField] private float carComfortDecelMps2 = 2.5f;

    [Header("Ciclista")]
    [SerializeField] private float yieldToBikeAheadM = 2.5f;
    [SerializeField] private float noSpawnNearBikeM = 25f;

    [Header("Fuerza social (Helbing & Molnar, 1995; eliptica II de Johansson et al., 2007)")]
    [SerializeField] private bool useSocialForce = true;
    [SerializeField] private float sfmTauS = 0.5f;
    [Tooltip("Intensidad de la repulsion entre peatones (m/s2). [VERIFICAR]")]
    [SerializeField] private float sfmA = 3f;
    [Tooltip("Alcance de la repulsion (m). [VERIFICAR]")]
    [SerializeField] private float sfmB = 0.3f;
    [Tooltip("Anticipacion de la especificacion eliptica II (s).")]
    [SerializeField] private float sfmAnticipationS = 1f;
    [Tooltip("Anisotropia: peso de lo que esta detras (1 = igual que adelante).")]
    [SerializeField, Range(0f, 1f)] private float sfmLambda = 0.3f;
    [Tooltip("Preferencia por esquivar hacia la derecha (componente tangencial).")]
    [SerializeField] private float sfmKappa = 0.3f;
    [SerializeField] private float sfmRangeM = 4f;
    [Tooltip("Radio del ciclista para los peatones (m).")]
    [SerializeField] private float bikeRadiusM = 0.6f;

    [Header("Reproducibilidad")]
    [SerializeField] private int seedOffset = 3000;

    public static PedestrianManager Instance { get; private set; }
    public IReadOnlyList<PedestrianAgent> Agents => agents;
    public IReadOnlyList<Crosswalk> Crosswalks => crosswalks;
    public float PedestriansPer100m { get => pedestriansPer100m; set => pedestriansPer100m = Mathf.Max(0f, value); }
    public int TargetCount => Mathf.RoundToInt(pedestriansPer100m * sidewalkLengthM / 100f);
    public double SimTimeS => simTimeS;
    public int WalkableMask { get; private set; }

    private readonly List<PedestrianAgent> agents = new List<PedestrianAgent>();
    private readonly List<Crosswalk> crosswalks = new List<Crosswalk>();
    private System.Random rng;
    private BicycleController bike;
    private int nextId = 1;
    private double simTimeS;
    private NavMeshPath path;
    private float totalAreaWeight;
    // Triangulos del NavMesh en el area "Vereda", con su area acumulada: puntos uniformes sobre la vereda real
    private readonly List<Vector3> walkTris = new List<Vector3>();
    private readonly List<float> walkTrisCum = new List<float>();
    private SocialForce.Params sfm;
    private const float CellM = 4f;
    private readonly Dictionary<long, List<PedestrianAgent>> grid = new Dictionary<long, List<PedestrianAgent>>();
    private readonly List<List<PedestrianAgent>> gridPool = new List<List<PedestrianAgent>>();
    private Vector3 bikePrevPos;
    private Vector2 bikeVel;

    private void Awake()
    {
        if (Instance != null && Instance != this) { enabled = false; return; }
        Instance = this;
        crosswalks.AddRange(FindObjectsByType<Crosswalk>(FindObjectsSortMode.None));
        crosswalks.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        path = new NavMeshPath();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Start()
    {
        int seed = TrafficManager.Instance != null ? TrafficManager.Instance.MasterSeed : 12345;
        rng = TrafficManager.CreateRng(seed, seedOffset);
        bike = FindFirstObjectByType<BicycleController>();

        int vereda = NavMesh.GetAreaFromName("Vereda"), cruce = NavMesh.GetAreaFromName("Cruce");
        WalkableMask = (vereda >= 0 ? 1 << vereda : 0) | (cruce >= 0 ? 1 << cruce : 0);
        if (WalkableMask == 0 || prefabs.Count == 0 || sidewalkAreas.Count == 0)
        {
            Debug.LogWarning("[PedestrianManager] Falta el NavMesh (areas Vereda/Cruce), los prefabs o las zonas de vereda: sin peatones.");
            enabled = false;
            return;
        }
        foreach (var b in sidewalkAreas) totalAreaWeight += AreaWeight(b);
        BuildWalkTriangles(vereda);
        sfm = new SocialForce.Params
        {
            tauS = sfmTauS, a = sfmA, b = sfmB, anticipationS = sfmAnticipationS, lambda = sfmLambda,
            kappa = sfmKappa, maxSpeedFactor = 1.3f, range = sfmRangeM,
        };
        if (bike != null) bikePrevPos = bike.RiderPosition;

        int target = TargetCount;
        int guard = 0;
        while (agents.Count < target && guard++ < target * 10)
        {
            if (!RandomSidewalkPoint(out Vector3 p)) continue;
            if (bike != null && (p - bike.RiderPosition).sqrMagnitude < noSpawnNearBikeM * noSpawnNearBikeM) continue;
            Spawn(prefabs[rng.Next(prefabs.Count)], p, scripted: false);
        }

        EventBus.Publish("pedestrians_start", "pedestrians", string.Format(CultureInfo.InvariantCulture,
            "count={0};per_100m={1:F2};sidewalk_m={2:F0};crosswalks={3}", agents.Count, pedestriansPer100m, sidewalkLengthM, crosswalks.Count));
    }

    // ------------------------------------------------------------
    // Alta de peatones (ambientales o scriptados)
    // ------------------------------------------------------------
    public PedestrianAgent Spawn(PedestrianAgent prefab, Vector3 position, bool scripted)
    {
        var a = Instantiate(prefab, position, Quaternion.identity, transform);
        a.Id = nextId++;
        a.Label = "peaton#" + a.Id;
        a.name = $"{prefab.name}_{a.Id}";
        a.Prefab = prefab;
        a.Scripted = scripted;
        a.DesiredSpeedMps = PedestrianRules.SampleWalkingSpeed(rng, speedMeanMps, speedSdMps, speedRangeMps.x, speedRangeMps.y);
        a.Snap(position, Vector3.forward);
        agents.Add(a);
        return a;
    }

    public PedestrianAgent RandomPrefab() => prefabs.Count > 0 ? prefabs[rng.Next(prefabs.Count)] : null;
    public System.Random Rng => rng;

    /// <summary>Un peaton scriptado termina su evento: vuelve a ser ambiental desde donde quedo.</summary>
    public void Release(PedestrianAgent a)
    {
        a.Scripted = false;
        a.Corners = null;
        a.Crossing = null;
        a.WaitingAt = null;
        // Si quedo fuera de las veredas (p. ej. en la calzada), desaparece cuando el ciclista ya no lo ve
        a.DespawnWhenUnseen = !NavMesh.SamplePosition(a.Position, out _, 1.0f, WalkableMask);
    }

    // ------------------------------------------------------------
    // Paso
    // ------------------------------------------------------------
    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        simTimeS += dt;
        foreach (var cw in crosswalks) { cw.OnCrossing.Clear(); cw.Waiting.Clear(); }
        if (bike != null)
        {
            Vector3 bp = bike.RiderPosition;
            bikeVel = new Vector2(bp.x - bikePrevPos.x, bp.z - bikePrevPos.z) / dt;
            if (bikeVel.sqrMagnitude > 400f) bikeVel = Vector2.zero;   // teletransporte
            bikePrevPos = bp;
        }
        BuildGrid();

        foreach (var a in agents)
        {
            if (a.Scripted) { if (a.Crossing != null) a.Crossing.OnCrossing.Add(a); continue; }
            if (a.DespawnWhenUnseen) { HideIfUnseen(a); continue; }
            Step(a, dt);
        }
        for (int i = agents.Count - 1; i >= 0; i--)
            if (!agents[i].gameObject.activeSelf) agents.RemoveAt(i);
    }

    /// <summary>Peaton fuera de las veredas: se desactiva cuando esta lejos y detras del ciclista.</summary>
    private void HideIfUnseen(PedestrianAgent a)
    {
        a.SpeedMps = 0f;
        if (bike == null) { a.gameObject.SetActive(false); return; }
        Vector3 rel = a.Position - bike.RiderPosition; rel.y = 0f;
        Vector3 f = bike.transform.forward; f.y = 0f;
        if (rel.magnitude > 25f && Vector3.Dot(rel, f) < 0f) a.gameObject.SetActive(false);
    }

    private void Step(PedestrianAgent a, float dt)
    {
        Vector3 pos = a.Position;
        a.SpeedMps = 0f;

        if (a.WaitUntilS > simTimeS) { a.State = "idle"; return; }
        if (a.Corners == null || a.CornerIndex >= a.Corners.Length)
        {
            if (!NewDestination(a, pos)) a.WaitUntilS = simTimeS + 2.0;
            return;
        }

        Vector3 target = a.Corners[a.CornerIndex];
        Vector3 to = target - pos; to.y = 0f;
        float dist = to.magnitude;
        // Con la fuerza social el peaton no pasa exacto por la esquina: tolerancia mayor, y si ya ve la esquina
        // siguiente sin obstaculos en el NavMesh, salta la actual (si no, vuelve atras a buscarla cuando lo empujan)
        if (dist < (useSocialForce ? 0.5f : 0.15f)) { a.CornerIndex++; return; }
        if (useSocialForce && a.CornerIndex + 1 < a.Corners.Length && dist < 3f &&
            !NavMesh.Raycast(pos, a.Corners[a.CornerIndex + 1], out _, WalkableMask))
        { a.CornerIndex++; return; }
        Vector3 dir = to / dist;
        float speed = a.DesiredSpeedMps;

        // Pasos peatonales: esperar en la cuneta hasta poder cruzar
        if (a.Crossing != null)
        {
            if (a.Crossing.IsOnRoadPart(pos, 0.3f)) { a.Crossing.OnCrossing.Add(a); a.State = "crossing"; }
            else a.Crossing = null;
        }
        if (a.Crossing == null)
        {
            Vector3 probe = pos + dir * 0.6f;
            foreach (var cw in crosswalks)
            {
                if (!cw.IsOnRoadPart(probe) || cw.IsOnRoadPart(pos)) continue;
                bool allowed = cw.CrosswalkKind == Crosswalk.Kind.Signalized
                    ? PedestrianRules.CanStartSignalized(cw.PedState.Phase, cw.PedState.TimeToChangeS, cw.ClearanceS,
                                                         cw.LengthM, a.DesiredSpeedMps)
                    : ZebraClear(cw);
                if (allowed) { a.Crossing = cw; a.WaitingAt = null; cw.OnCrossing.Add(a); a.State = "crossing"; }
                else
                {
                    a.WaitingAt = cw;
                    cw.Waiting.Add(a);
                    a.State = cw.CrosswalkKind == Crosswalk.Kind.Signalized ? "waiting_signal" : "waiting_zebra";
                    Hold(a, pos, dir, dt);
                    return;
                }
                break;
            }
        }

        // Ceder al ciclista que tiene justo adelante
        if (bike != null)
        {
            Vector3 rel = bike.RiderPosition - pos; rel.y = 0f;
            float along = Vector3.Dot(rel, dir);
            if (along > 0f && along < yieldToBikeAheadM && Mathf.Abs(Vector3.Cross(dir, rel).y) < 1.0f)
            {
                a.State = "yielding_bike";
                Hold(a, pos, dir, dt);
                return;
            }
        }

        // En el cruce: no caminar hacia un auto que quedo en su camino
        if (a.Crossing != null && CarInPath(pos, dir, a.RadiusM))
        {
            a.State = "yielding_car";
            Hold(a, pos, dir, dt);
            return;
        }

        a.State = a.Crossing != null ? "crossing" : "walking";
        if (useSocialForce) { MoveSocialForce(a, pos, dir, speed, dt); return; }

        // Avanzar hacia la esquina siguiente del camino (sin fuerza social)
        float step = Mathf.Min(speed * dt, dist);
        Vector3 next = Vector3.MoveTowards(pos, target, step);
        a.SpeedMps = (next - pos).magnitude / dt;
        a.State = a.Crossing != null ? "crossing" : "walking";
        next.y = SurfaceY(next);
        a.MoveTo(next, dir, dt);
    }

    /// <summary>Paso cebra: ningun auto que se acerca deja de poder detenerse antes del cruce.</summary>
    private const float CarStopBeforeEdgeM = 1.5f;

    // ------------------------------------------------------------
    // Fuerza social
    // ------------------------------------------------------------

    /// <summary>Esperar o ceder: velocidad deseada 0 (con la fuerza social igual se separa de los demas).</summary>
    private void Hold(PedestrianAgent a, Vector3 pos, Vector3 dir, float dt)
    {
        if (useSocialForce) { MoveSocialForce(a, pos, dir, 0f, dt); a.StuckS = 0f; }
        else a.MoveTo(pos, dir, dt);
    }

    private const float WaitingToCrossingWeight = 0.3f;   // lo que siente el que cruza de los que esperan
    private const float CrossingToWaitingWeight = 2f;     // lo que siente el que espera de los que cruzan

    private static long CellKey(Vector3 p) =>
        ((long)Mathf.FloorToInt(p.x / CellM) << 32) ^ (uint)Mathf.FloorToInt(p.z / CellM);

    private void BuildGrid()
    {
        foreach (var l in grid.Values) { l.Clear(); gridPool.Add(l); }
        grid.Clear();
        if (!useSocialForce) return;
        foreach (var a in agents)
        {
            if (!a.gameObject.activeInHierarchy) continue;
            long k = CellKey(a.Position);
            if (!grid.TryGetValue(k, out var list))
            {
                list = gridPool.Count > 0 ? gridPool[gridPool.Count - 1] : new List<PedestrianAgent>();
                if (gridPool.Count > 0) gridPool.RemoveAt(gridPool.Count - 1);
                grid[k] = list;
            }
            list.Add(a);
        }
    }

    private static Vector2 XZ(Vector3 v) => new Vector2(v.x, v.z);

    /// <summary>Un paso del modelo de fuerza social y ajuste al NavMesh.</summary>
    private void MoveSocialForce(PedestrianAgent a, Vector3 pos, Vector3 dir, float desiredSpeed, float dt)
    {
        Vector2 p = XZ(pos), vel = a.Velocity;
        Vector2 e = XZ(dir); e = e.sqrMagnitude > 1e-6f ? e.normalized : Vector2.zero;
        Vector2 heading = vel.sqrMagnitude > 0.01f ? vel : e;
        Vector2 acc = SocialForce.Driving(vel, e, desiredSpeed, sfm.tauS);

        // Otros peatones (celdas vecinas de la grilla)
        int cx = Mathf.FloorToInt(pos.x / CellM), cz = Mathf.FloorToInt(pos.z / CellM);
        for (int ix = cx - 1; ix <= cx + 1; ix++)
            for (int iz = cz - 1; iz <= cz + 1; iz++)
            {
                if (!grid.TryGetValue(((long)ix << 32) ^ (uint)iz, out var list)) continue;
                foreach (var n in list)
                {
                    if (n == a) continue;
                    Vector2 nv = n.Scripted ? XZ(n.transform.forward) * n.SpeedMps : n.Velocity;
                    // Los que esperan en la cuneta le abren paso a los que vienen cruzando (si no, el grupo que
                    // espera es una pared y el que cruza queda atrapado en la calzada)
                    float w = 1f;
                    if (a.Crossing != null && n.WaitingAt != null) w = WaitingToCrossingWeight;
                    else if (a.WaitingAt != null && n.Crossing != null) w = CrossingToWaitingWeight;
                    acc += w * SocialForce.Repulsion(p, vel, heading, XZ(n.Position), nv, a.RadiusM + n.RadiusM, sfm);
                }
            }

        // Ciclista
        if (bike != null)
            acc += SocialForce.Repulsion(p, vel, heading, XZ(bike.RiderPosition), bikeVel, a.RadiusM + bikeRadiusM, sfm);

        // Autos junto al cruce: repulsion desde el punto mas cercano de su rectangulo
        if (a.Crossing != null || a.WaitingAt != null)
        {
            var tm = TrafficManager.Instance;
            if (tm != null)
                foreach (var v in tm.Vehicles)
                {
                    Vector3 d3 = v.transform.position - pos; d3.y = 0f;
                    if (d3.sqrMagnitude > 49f) continue;
                    Vector3 local = v.transform.InverseTransformPoint(pos);
                    local.x = Mathf.Clamp(local.x, -v.WidthM * 0.5f, v.WidthM * 0.5f);
                    local.z = Mathf.Clamp(local.z, -v.LengthM * 0.5f, v.LengthM * 0.5f);
                    Vector3 closest = v.transform.TransformPoint(local);
                    Vector2 cv = XZ(v.transform.forward) * v.Speed;
                    acc += SocialForce.Repulsion(p, vel, heading, XZ(closest), cv, a.RadiusM, sfm);
                }
        }

        float maxSpeed = sfm.maxSpeedFactor * Mathf.Max(a.DesiredSpeedMps, 0.5f);
        vel = SocialForce.Integrate(vel, acc, dt, maxSpeed);
        if (desiredSpeed <= 0f && vel.sqrMagnitude < 0.0025f) vel = Vector2.zero;   // quieto

        Vector3 next = pos + new Vector3(vel.x, 0f, vel.y) * dt;
        // Sin permiso para cruzar: no pisar la calzada de un paso
        if (a.Crossing == null)
            foreach (var cw in crosswalks)
                if (cw.IsOnRoadPart(next) && !cw.IsOnRoadPart(pos)) { next = pos; vel = Vector2.zero; break; }
        // Quedarse sobre lo caminable (veredas y cruces): rayo sobre el NavMesh desde la posicion actual; si cruza un
        // borde, se detiene en el y se desliza a lo largo de el. (Ajustar al punto mas cercano del NavMesh fallaba en
        // la rampa del paso junto a la vereda: el punto mas cercano en 3D quedaba atras y el peaton retrocedia.)
        if (next != pos && NavMesh.Raycast(pos, next, out NavMeshHit hit, WalkableMask))
        {
            Vector2 n = XZ(hit.normal);
            n = n.sqrMagnitude > 1e-6f ? n.normalized : -vel.normalized;
            float into = Vector2.Dot(vel, n);
            if (into < 0f) vel -= into * n;                          // quitar la componente hacia el borde
            Vector3 stop = hit.position + new Vector3(n.x, 0f, n.y) * 0.02f;
            next = new Vector3(stop.x, next.y, stop.z) + new Vector3(vel.x, 0f, vel.y) * dt;
            if (NavMesh.Raycast(stop, next, out _, WalkableMask)) next = new Vector3(stop.x, next.y, stop.z);
        }
        next.y = SurfaceY(next);

        a.Velocity = vel;
        a.SpeedMps = vel.magnitude;
        Vector3 face = a.SpeedMps > 0.15f ? new Vector3(vel.x, 0f, vel.y) : dir;
        a.MoveTo(next, face, dt);

        // Trabado (queriendo avanzar sin avanzar): recalcular el camino
        if (desiredSpeed > 0f && a.SpeedMps < 0.15f) a.StuckS += dt; else a.StuckS = 0f;
        if (a.StuckS > 4f)
        {
            a.StuckS = 0f;
            if (NavMesh.CalculatePath(next, a.Destination, WalkableMask, path) && path.status == NavMeshPathStatus.PathComplete &&
                path.corners.Length >= 2)
            { a.Corners = path.corners; a.CornerIndex = 1; }
            else a.Corners = null;   // elegira otro destino
        }
    }

    /// <summary>Altura a la que camina el peaton: la del asfalto sobre la calzada de un cruce; si no, la del NavMesh (vereda).</summary>
    private float SurfaceY(Vector3 p)
    {
        foreach (var cw in crosswalks)
            if (cw.IsOnRoadPart(p)) return cw.RoadY;
        // Al volver a la vereda desde la calzada la interpolacion queda baja: tomar la altura del NavMesh
        return UnityEngine.AI.NavMesh.SamplePosition(p, out UnityEngine.AI.NavMeshHit hit, 0.6f, UnityEngine.AI.NavMesh.AllAreas)
            ? hit.position.y : p.y;
    }

    /// <summary>Hay un auto justo adelante en el camino del peaton (p. ej. detenido sobre el cruce)?</summary>
    private static bool CarInPath(Vector3 pos, Vector3 dir, float radius)
    {
        var tm = TrafficManager.Instance;
        if (tm == null) return false;
        Vector3 probe = pos + dir * (radius + 0.5f);
        foreach (var v in tm.Vehicles)
        {
            Vector3 d = v.transform.position - probe; d.y = 0f;
            if (d.sqrMagnitude > 36f) continue;
            Vector3 local = v.transform.InverseTransformPoint(probe);
            if (Mathf.Abs(local.x) <= v.WidthM * 0.5f + 0.2f && Mathf.Abs(local.z) <= v.LengthM * 0.5f + 0.2f) return true;
        }
        return false;
    }

    private bool ZebraClear(Crosswalk cw)
    {
        var tm = TrafficManager.Instance;
        if (tm == null) return true;
        foreach (var (lane, sLane, _) in cw.LaneCrossings)
        {
            foreach (var v in tm.Vehicles)
            {
                float front = v.S + v.LengthM * 0.5f, rear = v.S - v.LengthM * 0.5f;
                float d;
                if (v.Lane == lane)
                {
                    if (rear > sLane + cw.WidthM * 0.5f) continue;             // ya paso
                    if (front > sLane - cw.WidthM * 0.5f - 0.5f) return false;  // esta sobre el cruce
                    d = sLane - cw.WidthM * 0.5f - front;
                }
                else if (v.NextLane == lane) d = v.Lane.LengthM - front + sLane - cw.WidthM * 0.5f;
                else continue;
                // El auto se detiene 1.5 m antes del borde (punto de detencion 1 m antes + 0.5 m): debe alcanzar a frenar ahi
                if (!PedestrianRules.ZebraSafeFromCar(d - CarStopBeforeEdgeM, v.Speed, carComfortDecelMps2, 0.5f, zebraPedestrianGapS))
                    return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------
    // Destinos
    // ------------------------------------------------------------
    private bool NewDestination(PedestrianAgent a, Vector3 from)
    {
        if (idleProbability > 0f && a.Corners != null && rng.NextDouble() < idleProbability)
        {
            a.WaitUntilS = simTimeS + Mathf.Lerp(idleDurationS.x, idleDurationS.y, (float)rng.NextDouble());
            a.Corners = null;
            return true;
        }
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (!RandomSidewalkPoint(out Vector3 p)) continue;
            float d = (p - from).magnitude;
            if (d < destinationDistanceM.x || d > destinationDistanceM.y) continue;
            if (!NavMesh.CalculatePath(from, p, WalkableMask, path) || path.status != NavMeshPathStatus.PathComplete) continue;
            if (path.corners.Length < 2) continue;
            // Un sorteo por intento (orden estable): los destinos al otro lado de la calzada se aceptan solo a veces
            bool acceptCrossing = rng.NextDouble() < crossingProbability;
            if (!acceptCrossing && PathCrossesRoad(path.corners)) continue;
            a.Corners = path.corners;
            a.CornerIndex = 1;
            a.Destination = p;
            return true;
        }
        a.Corners = null;
        return false;
    }

    /// <summary>True si el camino pasa por la calzada de algun paso peatonal.</summary>
    private bool PathCrossesRoad(Vector3[] corners)
    {
        for (int i = 1; i < corners.Length; i++)
        {
            Vector3 a = corners[i - 1], b = corners[i];
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b)));
            for (int k = 0; k <= n; k++)
            {
                Vector3 q = Vector3.Lerp(a, b, k / (float)n);
                foreach (var cw in crosswalks) if (cw.IsOnRoadPart(q)) return true;
            }
        }
        return false;
    }

    /// <summary>Triangulos del NavMesh del area Vereda y su area acumulada (para sortear puntos uniformes).</summary>
    private void BuildWalkTriangles(int veredaArea)
    {
        walkTris.Clear(); walkTrisCum.Clear();
        if (veredaArea < 0) return;
        var tri = NavMesh.CalculateTriangulation();
        float cum = 0f;
        for (int i = 0; i + 2 < tri.indices.Length; i += 3)
        {
            if (tri.areas[i / 3] != veredaArea) continue;
            Vector3 a = tri.vertices[tri.indices[i]], b = tri.vertices[tri.indices[i + 1]], c = tri.vertices[tri.indices[i + 2]];
            float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            if (area < 1e-4f) continue;
            cum += area;
            walkTris.Add(a); walkTris.Add(b); walkTris.Add(c);
            walkTrisCum.Add(cum);
        }
    }

    private bool RandomSidewalkPoint(out Vector3 point)
    {
        point = default;
        // Uniforme sobre la superficie de vereda del NavMesh (si no hay triangulacion, por zonas como antes)
        if (walkTrisCum.Count > 0)
        {
            float pick = (float)rng.NextDouble() * walkTrisCum[walkTrisCum.Count - 1];
            int lo = 0, hi = walkTrisCum.Count - 1;
            while (lo < hi) { int mid = (lo + hi) / 2; if (walkTrisCum[mid] < pick) lo = mid + 1; else hi = mid; }
            float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
            if (u + v > 1f) { u = 1f - u; v = 1f - v; }
            Vector3 a = walkTris[3 * lo], b = walkTris[3 * lo + 1], c = walkTris[3 * lo + 2];
            point = a + (b - a) * u + (c - a) * v;
            return true;
        }
        double r = rng.NextDouble() * totalAreaWeight;
        Bounds area = sidewalkAreas[sidewalkAreas.Count - 1];
        foreach (var b in sidewalkAreas) { r -= AreaWeight(b); if (r <= 0) { area = b; break; } }
        var p = new Vector3(Mathf.Lerp(area.min.x, area.max.x, (float)rng.NextDouble()), area.center.y,
                            Mathf.Lerp(area.min.z, area.max.z, (float)rng.NextDouble()));
        int vereda = NavMesh.GetAreaFromName("Vereda");
        if (!NavMesh.SamplePosition(p, out NavMeshHit hit, 3f, vereda >= 0 ? 1 << vereda : NavMesh.AllAreas)) return false;
        point = hit.position;
        return true;
    }

    private static float AreaWeight(Bounds b) => Mathf.Max(b.size.x, b.size.z);
}
