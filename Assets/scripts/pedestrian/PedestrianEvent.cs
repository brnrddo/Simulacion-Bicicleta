// ============================================================
// PedestrianEvent.cs
// Manipulacion experimental con peatones (T6). Se ubica en un punto de la red
// del ciclista (CyclistPath + S) y se dispara segun el tiempo de llegada del
// ciclista a ese punto. El peaton scriptado NO esquiva al ciclista.
//
//   NearPass: un peaton camina de frente hacia el ciclista, paralelo a su
//     camino, a una distancia lateral fija del extremo del manubrio
//     (0.5 / 1.0 / 2.0 m: zonas proxemicas). Mientras esta a mas de 8 m del
//     ciclista ajusta su linea a la posicion lateral actual del ciclista (a lo
//     sumo 0.6 m/s); en los ultimos 8 m la mantiene fija y no esquiva.
//   Jaywalk: un peaton espera en la cuneta (fuera de un paso peatonal) y cruza
//     la calle en linea recta cuando el tiempo de llegada del ciclista al punto
//     de conflicto cae a t* ~ U(3, 4) s (sorteado con semilla).
//
// Publica pedestrian_event_start y pedestrian_event_pass (distancia minima real
// del manubrio a la capsula del peaton, velocidades). Despues el peaton sigue
// como peaton ambiental.
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

[DefaultExecutionOrder(16)]
public class PedestrianEvent : MonoBehaviour
{
    public enum EventType { NearPass, Jaywalk }

    [SerializeField] private EventType type = EventType.NearPass;
    [Tooltip("Punto del evento sobre la red del ciclista.")]
    [SerializeField] private CyclistPath path;
    [SerializeField, Min(0f)] private float sOnPath = 10f;

    [Header("Pasada cercana")]
    [Tooltip("Distancia lateral del extremo del manubrio a la capsula del peaton (m): 0.5, 1.0 o 2.0.")]
    [SerializeField, Min(0f)] private float passingDistanceM = 1.0f;
    [Tooltip("Lado del ciclista por el que pasa el peaton.")]
    [SerializeField] private bool passOnRight = true;
    [Tooltip("Se dispara cuando el ciclista esta a este tiempo del punto (s).")]
    [SerializeField] private float nearPassTriggerS = 10f;

    [Header("Cruce fuera del paso")]
    [SerializeField] private Vector2 jaywalkTriggerS = new Vector2(3f, 4f);
    [Tooltip("Sale desde la derecha del ciclista (si no, desde la izquierda).")]
    [SerializeField] private bool jaywalkFromRight = true;
    [Tooltip("Distancia lateral desde el camino del ciclista a donde espera el peaton (m).")]
    [SerializeField] private float jaywalkStartLateralM = 3.5f;
    [Tooltip("Distancia que recorre al otro lado del camino del ciclista (m).")]
    [SerializeField] private float jaywalkEndLateralM = 9f;
    [Tooltip("El peaton aparece esperando cuando el ciclista esta a este tiempo (s), para no aparecer a la vista.")]
    [SerializeField] private float jaywalkSpawnS = 15f;

    [Header("General")]
    [SerializeField] private bool oneShot = true;
    [SerializeField] private int seedOffset = 3100;

    private enum Phase { Armed, Waiting, Active, Done }
    private Phase phase = Phase.Armed;
    private RiderRoute route;
    private BicycleController bike;
    private Renderer handlebar;
    private readonly Dictionary<CyclistPath, float> approach = new Dictionary<CyclistPath, float>();
    private System.Random rng;

    private PedestrianAgent ped;
    private Vector3 eventPoint, tangent, right, walkDir, endPoint;
    private float tTarget, minDist = float.PositiveInfinity, bikeSpeedAtMin, relSpeedAtMin, plannedLateral;
    private float pedAlong, pedLateral;                     // posicion del peaton (near pass) respecto del punto del evento
    private const float FreezeLineM = 8f, LineAdjustMps = 0.6f;

    private void Start()
    {
        route = FindFirstObjectByType<RiderRoute>();
        bike = FindFirstObjectByType<BicycleController>();
        if (bike != null)
            foreach (var r in bike.GetComponentsInChildren<Renderer>())
                if (r.name.StartsWith("Handlebar")) { handlebar = r; break; }
        if (route == null || route.Network == null || path == null || bike == null)
        {
            Debug.LogWarning($"[PedestrianEvent] {name}: falta RiderRoute, CyclistPath o la bici; se desactiva.");
            enabled = false;
            return;
        }

        path.EnsureInit();
        eventPoint = path.GetPoint(sOnPath, out tangent);
        right = Vector3.Cross(Vector3.up, tangent);
        transform.position = eventPoint;

        // Tramos desde los que el ciclista llega al punto (hasta 200 m antes)
        approach[path] = sOnPath;  // para el propio tramo: d = sOnPath - S
        var frontier = new List<(CyclistPath p, float extra)> { (path, sOnPath) };
        while (frontier.Count > 0)
        {
            var next = new List<(CyclistPath, float)>();
            foreach (var (p, extra) in frontier)
                foreach (var pred in route.Network.PredecessorsOf(p))
                {
                    if (approach.ContainsKey(pred)) continue;
                    float e = extra + pred.LengthM;   // desde el INICIO de pred hasta el punto
                    if (e > 200f + pred.LengthM) continue;
                    approach[pred] = e;
                    next.Add((pred, e));
                }
            frontier = next;
        }

        int seed = TrafficManager.Instance != null ? TrafficManager.Instance.MasterSeed : 12345;
        rng = TrafficManager.CreateRng(seed, seedOffset);
        tTarget = Mathf.Lerp(jaywalkTriggerS.x, jaywalkTriggerS.y, (float)rng.NextDouble());
    }

    /// <summary>Distancia del ciclista al punto del evento sobre la red (NaN si no viene hacia el).</summary>
    private float CyclistDistance()
    {
        if (!route.HasFix || route.CurrentPath == null) return float.NaN;
        if (!approach.TryGetValue(route.CurrentPath, out float fromStart)) return float.NaN;
        return fromStart - route.PathS;
    }

    private void FixedUpdate()
    {
        var mgr = PedestrianManager.Instance;
        if (mgr == null) return;
        float dt = Time.fixedDeltaTime;
        float d = CyclistDistance();
        float vb = bike.CurrentSpeedMps;
        float tta = float.IsNaN(d) || vb < 0.5f ? float.PositiveInfinity : d / vb;

        switch (phase)
        {
            case Phase.Armed:
                if (float.IsNaN(d) || d < 0f) return;
                if (type == EventType.NearPass && tta <= nearPassTriggerS) StartNearPass(mgr, d, vb, tta);
                else if (type == EventType.Jaywalk && tta <= jaywalkSpawnS) SpawnJaywalker(mgr);
                return;

            case Phase.Waiting:  // jaywalk: espera en la cuneta
                ped.SpeedMps = 0f;
                ped.MoveTo(ped.Position, walkDir, dt);
                if (PedestrianRules.JaywalkShouldStart(float.IsNaN(d) ? float.PositiveInfinity : d, vb, tTarget))
                {
                    phase = Phase.Active;
                    ped.OnRoad = true;
                    Publish("pedestrian_event_start", string.Format(CultureInfo.InvariantCulture,
                        "type=jaywalk;t_target_s={0:F2};tta_s={1:F2};cyclist_speed_mps={2:F2};side={3};path={4}",
                        tTarget, tta, vb, jaywalkFromRight ? "right" : "left", path.name));
                }
                return;

            case Phase.Active:
                Advance(mgr, dt);
                return;

            case Phase.Done:
                if (!oneShot && (float.IsNaN(d) || d > 100f)) phase = Phase.Armed;
                return;
        }
    }

    private void StartNearPass(PedestrianManager mgr, float d, float vb, float tta)
    {
        // Linea paralela al camino del ciclista, a la distancia lateral pedida desde su posicion actual
        var prefab = mgr.RandomPrefab();
        plannedLateral = NearPassTargetLateral(prefab.RadiusM);

        ped = mgr.Spawn(prefab, eventPoint, scripted: true);
        float vp = ped.DesiredSpeedMps;
        pedAlong = vp * tta;                     // se cruzan cerca del punto del evento
        pedLateral = plannedLateral;
        walkDir = -tangent;
        endPoint = eventPoint - tangent * 30f + right * plannedLateral;
        ped.Snap(eventPoint + tangent * pedAlong + right * pedLateral, walkDir);
        ped.State = "scripted";
        phase = Phase.Active;
        Publish("pedestrian_event_start", string.Format(CultureInfo.InvariantCulture,
            "type=near_pass;planned_m={0:F2};tta_s={1:F2};cyclist_speed_mps={2:F2};pedestrian_speed_mps={3:F2};side={4};path={5}",
            passingDistanceM, tta, vb, vp, passOnRight ? "right" : "left", path.name));
    }

    private void SpawnJaywalker(PedestrianManager mgr)
    {
        float side = jaywalkFromRight ? 1f : -1f;
        Vector3 start = eventPoint + right * side * jaywalkStartLateralM;
        walkDir = -right * side;
        endPoint = eventPoint - right * side * jaywalkEndLateralM;
        ped = mgr.Spawn(mgr.RandomPrefab(), start, scripted: true);
        ped.Snap(start, walkDir);
        ped.State = "scripted";
        phase = Phase.Waiting;
    }

    private float NearPassTargetLateral(float pedRadius)
    {
        float side = passOnRight ? 1f : -1f;
        float half = TrafficManager.Instance != null ? TrafficManager.Instance.HandlebarHalfWidthM : 0.274f;
        return route.LateralOffsetM + side * PedestrianRules.NearPassLateral(half, pedRadius, passingDistanceM);
    }

    private void Advance(PedestrianManager mgr, float dt)
    {
        Vector3 pos = ped.Position;
        float vp = ped.DesiredSpeedMps;
        Vector3 next;
        if (type == EventType.NearPass)
        {
            // Ajusta su linea a la posicion lateral actual del ciclista mientras esta lejos
            pedAlong -= vp * dt;
            float gapToBike = Vector3.Dot(Flat(pos - bike.RiderPosition), -walkDir);
            if (gapToBike > FreezeLineM && route.HasFix)
                pedLateral = Mathf.MoveTowards(pedLateral, NearPassTargetLateral(ped.RadiusM), LineAdjustMps * dt);
            next = eventPoint + tangent * pedAlong + right * pedLateral;
            next.y = pos.y;
        }
        else next = Vector3.MoveTowards(pos, endPoint, vp * dt);
        ped.SpeedMps = (next - pos).magnitude / dt;
        ped.MoveTo(next, walkDir, dt);

        // Distancia minima real: extremos del manubrio a la capsula (vertical) del peaton
        Vector3 fwd = bike.transform.forward; fwd.y = 0f; fwd.Normalize();
        Vector3 bRight = Vector3.Cross(Vector3.up, fwd);
        Vector3 c = handlebar != null ? handlebar.bounds.center : bike.RiderPosition;
        float half = TrafficManager.Instance != null ? TrafficManager.Instance.HandlebarHalfWidthM : 0.274f;
        float dist = Mathf.Min(Flat(c - bRight * half - next).magnitude, Flat(c + bRight * half - next).magnitude) - ped.RadiusM;
        if (dist < minDist && !bike.IsFrozen)  // durante un reinicio tras choque no se mide
        {
            minDist = dist;
            bikeSpeedAtMin = bike.CurrentSpeedMps;
            Vector3 vPed = walkDir * ped.SpeedMps;
            relSpeedAtMin = (fwd * bikeSpeedAtMin - vPed).magnitude;
        }

        bool behindCyclist = Vector3.Dot(Flat(next - bike.RiderPosition), fwd) < -5f;
        bool arrived = type == EventType.NearPass ? pedAlong < -30f : Flat(endPoint - next).sqrMagnitude < 0.04f;
        if ((type == EventType.NearPass && behindCyclist) || arrived)
        {
            Publish("pedestrian_event_pass", string.Format(CultureInfo.InvariantCulture,
                "type={0};planned_m={1};min_real_m={2:F3};cyclist_speed_at_min_mps={3:F2};rel_speed_at_min_mps={4:F2};path={5}",
                type == EventType.NearPass ? "near_pass" : "jaywalk",
                type == EventType.NearPass ? passingDistanceM.ToString("F2", CultureInfo.InvariantCulture) : "NaN",
                minDist, bikeSpeedAtMin, relSpeedAtMin, path.name));
            ped.OnRoad = false;
            mgr.Release(ped);
            ped = null;
            minDist = float.PositiveInfinity;
            phase = Phase.Done;
        }
    }

    private void Publish(string type, string detail) => EventBus.Publish(type, name, detail);

    private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    private void OnDrawGizmos()
    {
        Gizmos.color = type == EventType.NearPass ? new Color(1f, 0.6f, 0f) : Color.red;
        Gizmos.DrawWireSphere(transform.position + Vector3.up, 0.6f);
    }
}
