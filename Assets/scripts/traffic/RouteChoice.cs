// ============================================================
// RouteChoice.cs
// Eleccion de ruta de cada auto al aparecer (E3 del plan de rutas).
//   1. Catalogo: para cada par (carril de aparicion, carril de destino) se
//      enumeran las rutas que no repiten interseccion y se guardan hasta
//      'maxRoutes', descartando las que tardan mas de 'maxRouteTimeFactor'
//      veces la mejor a flujo libre. Un bus solo usa conectores donde cabe.
//   2. Tiempo de cada tramo con la congestion del momento:
//        t = (t0 + d) * (1 + alpha (v/c)^gamma)          (BPR)
//      t0 = largo / limite; v = autos que entraron al tramo en los ultimos
//      'flowWindowS' s, en veh/h; d y c segun el control:
//        - carril de llegada a un semaforo: d = C (1 - g/C)^2 / 2, c = s g/C
//        - carril de llegada a un PARE: d = stopDelayS, c = s
//        - giro que cede (conector): c = capacidad HCM con el flujo actual
//          de los movimientos a los que cede, d = 3600 / c
//        - resto: d = 0, c = s
//   3. Logit sobre el tiempo de cada ruta, P_i = exp(beta T_i) / sum, y un
//      sorteo por auto (CreateRng(seed, 7100)). La ruta no cambia en el camino.
// Registra route_choice por auto y route_share cada 60 s.
// Va en el objeto Traffic; lo usa TrafficManager.
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

public class RouteChoice : MonoBehaviour
{
    public const int RngOffset = 7100;

    [Header("Logit")]
    [Tooltip("Sensibilidad al tiempo (1/s, negativa). Con -0.1 y 3 s de diferencia: 57 % / 43 %. [VERIFICAR] calibrar.")]
    [SerializeField] private float logitBeta = -0.1f;

    [Header("Rutas por par")]
    [SerializeField, Min(1)] private int maxRoutes = 2;
    [Tooltip("Se descartan las rutas cuyo tiempo a flujo libre supera este factor por el de la mejor.")]
    [SerializeField, Min(1f)] private float maxRouteTimeFactor = 2.2f;

    [Header("Curva BPR")]
    [SerializeField, Min(0f)] private float bprAlpha = 0.15f;
    [SerializeField, Min(1f)] private float bprExponent = 4f;
    [Tooltip("Flujo de saturacion de un carril (veh/h).")]
    [SerializeField, Min(100f)] private float saturationVehPerHour = 1800f;
    [Tooltip("Ventana para medir el flujo de cada tramo (s simulados).")]
    [SerializeField, Min(10f)] private float flowWindowS = 120f;

    [Header("Demora por control")]
    [Tooltip("Demora fija de un acceso con PARE (detencion y arranque, s).")]
    [SerializeField, Min(0f)] private float stopDelayS = 3f;
    [Tooltip("Brecha critica de un giro que cede desde una via principal (s), para la capacidad HCM.")]
    [SerializeField, Min(0.5f)] private float criticalGapS = 4.5f;
    [Tooltip("Brecha critica extra si el giro sale de un PARE o CEDA (s).")]
    [SerializeField, Min(0f)] private float minorExtraGapS = 2f;
    [Tooltip("Tiempo de seguimiento (follow-up) del HCM (s): 2.2 desde la principal, 3.3 desde un PARE.")]
    [SerializeField, Min(0.5f)] private float followUpMajorS = 2.2f;
    [SerializeField, Min(0.5f)] private float followUpMinorS = 3.3f;

    [Header("Registro")]
    [SerializeField, Min(5f)] private float shareLogIntervalS = 60f;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private class LaneInfo
    {
        public float t0;
        public float fixedDelay;      // semaforo o PARE
        public float capacity;        // veh/h (semaforo: s g/C; resto: s)
        public bool yields;           // conector que cede: d y c dependen del flujo en conflicto
        public bool minor;            // sale de un PARE / CEDA
        public TrafficLane[] conflicting = new TrafficLane[0];
        public readonly Queue<double> entries = new Queue<double>();
    }

    public class Route
    {
        public List<TrafficLane> lanes;
        public string name;          // p. ej. "I_SC:Right > I_NC:Right"
        public float freeTimeS;      // con la red vacia (incluye la demora por control)
    }

    private class PairStats
    {
        public string key;
        public List<Route> routes;
        public int[] chosen;
        public int[] chosenSinceLog;
    }

    private readonly Dictionary<TrafficLane, LaneInfo> info = new Dictionary<TrafficLane, LaneInfo>();
    private readonly Dictionary<(TrafficLane, TrafficLane, bool), List<Route>> catalog = new Dictionary<(TrafficLane, TrafficLane, bool), List<Route>>();
    private readonly Dictionary<string, PairStats> stats = new Dictionary<string, PairStats>();
    private System.Random rng;
    private System.Predicate<TrafficLane> longVehicleAllowed;
    private double now;
    private float sinceLog;

    /// <summary>Lo llama TrafficManager al iniciar, antes de generar autos.</summary>
    public void Init(TrafficManager tm, IEnumerable<TrafficLane> lanes, System.Predicate<TrafficLane> longVehicleAllowed)
    {
        rng = tm.CreateRng(RngOffset);
        this.longVehicleAllowed = longVehicleAllowed;
        info.Clear();
        foreach (var l in lanes) info[l] = Describe(l);

        foreach (var kv in new Dictionary<string, float>
        {
            ["route_logit_beta"] = logitBeta, ["route_max_routes"] = maxRoutes, ["route_max_time_factor"] = maxRouteTimeFactor,
            ["route_bpr_alpha"] = bprAlpha, ["route_bpr_exponent"] = bprExponent, ["route_saturation_vph"] = saturationVehPerHour,
            ["route_flow_window_s"] = flowWindowS, ["route_stop_delay_s"] = stopDelayS, ["route_critical_gap_s"] = criticalGapS,
            ["route_minor_extra_gap_s"] = minorExtraGapS, ["route_follow_up_major_s"] = followUpMajorS, ["route_follow_up_minor_s"] = followUpMinorS,
        })
            ExperimentLogger.RegisterMeta(kv.Key, kv.Value.ToString("0.###", Inv));
    }

    private LaneInfo Describe(TrafficLane l)
    {
        var li = new LaneInfo { t0 = l.LengthM / Mathf.Max(l.SpeedLimitMps, 0.1f), capacity = saturationVehPerHour };
        var line = l.StopLine;
        if (line != null && !line.IsCyclistApproach)
        {
            if (line.IsSignalized)
            {
                var t = line.Controller.Timing;
                float green = SignalCycle.AxisOf(line.Approach) == SignalAxis.NorthSouth ? t.greenNorthSouthS : t.greenEastWestS;
                li.fixedDelay = RouteChoiceModel.SignalDelay(t.CycleS, green);
                li.capacity = saturationVehPerHour * green / t.CycleS;
            }
            else if (line.Control == ApproachControl.Stop) li.fixedDelay = stopDelayS;
        }
        if (l.IsConnector)
        {
            var yieldTo = l.Conflicts.Where(k => k.other != null && k.mode == ConflictMode.Yield).Select(k => k.other).Distinct().ToArray();
            if (yieldTo.Length > 0)
            {
                li.yields = true;
                li.conflicting = yieldTo;
                var entryLine = l.EntryLane != null ? l.EntryLane.StopLine : null;
                li.minor = entryLine != null && (entryLine.Control == ApproachControl.Stop || entryLine.Control == ApproachControl.Yield);
            }
        }
        return li;
    }

    // ------------------------------------------------------------
    // Flujo medido
    // ------------------------------------------------------------
    public void OnLaneEntered(TrafficLane lane, double simTimeS)
    {
        if (info.TryGetValue(lane, out var li)) li.entries.Enqueue(simTimeS);
    }

    private float Flow(LaneInfo li)
    {
        while (li.entries.Count > 0 && li.entries.Peek() < now - flowWindowS) li.entries.Dequeue();
        return li.entries.Count * 3600f / flowWindowS;
    }

    /// <summary>Tiempo del tramo con la congestion actual (s). 'empty' = con la red vacia.</summary>
    private float LaneTime(TrafficLane l, bool empty)
    {
        var li = info[l];
        float v = empty ? 0f : Flow(li);
        float delay = li.fixedDelay, capacity = li.capacity;
        if (li.yields)
        {
            float qc = 0f;
            if (!empty) foreach (var o in li.conflicting) if (info.TryGetValue(o, out var oi)) qc += Flow(oi);
            capacity = RouteChoiceModel.YieldCapacity(qc, criticalGapS + (li.minor ? minorExtraGapS : 0f),
                                                      li.minor ? followUpMinorS : followUpMajorS);
            delay += 3600f / Mathf.Max(capacity, 1f);
        }
        return RouteChoiceModel.Bpr(li.t0 + delay, v, capacity, bprAlpha, bprExponent);
    }

    private float RouteTime(Route r, bool empty)
    {
        float t = 0f;
        foreach (var l in r.lanes) t += LaneTime(l, empty);
        return t;
    }

    // ------------------------------------------------------------
    // Catalogo y eleccion
    // ------------------------------------------------------------
    private static string IntersectionOf(TrafficLane l)
    {
        if (!l.IsConnector) return null;
        var p = l.name.Split('_');
        return p.Length > 1 ? p[0] + "_" + p[1] : l.name;
    }

    public IReadOnlyList<Route> Routes(TrafficLane from, TrafficLane to, bool longVehicle)
    {
        var key = (from, to, longVehicle);
        if (catalog.TryGetValue(key, out var routes)) return routes;

        var all = RouteChoiceModel.EnumerateRoutes(from, to, IntersectionOf, longVehicle ? longVehicleAllowed : null)
            .Select(lanes => new Route
            {
                lanes = lanes,
                name = string.Join(" > ", lanes.Where(l => l.IsConnector).Select(l => IntersectionOf(l) + ":" + l.Movement)),
            }).ToList();
        foreach (var r in all) r.freeTimeS = RouteTime(r, empty: true);
        routes = RouteChoiceModel.SelectRoutes(all.Select(r => r.freeTimeS).ToList(), maxRoutes, maxRouteTimeFactor)
                                 .Select(i => all[i]).ToList();
        catalog[key] = routes;
        return routes;
    }

    public bool HasRoute(TrafficLane from, TrafficLane to, bool longVehicle) => Routes(from, to, longVehicle).Count > 0;

    /// <summary>Elige la ruta de un auto nuevo (un numero del generador). null si no hay ruta.</summary>
    public IReadOnlyList<TrafficLane> Choose(TrafficVehicle v, TrafficLane from, OdNode destination, bool longVehicle,
                                            double simTimeS, bool warmingUp)
    {
        now = simTimeS;
        var routes = Routes(from, destination.Lane, longVehicle);
        if (routes.Count == 0) return null;

        var times = routes.Select(r => RouteTime(r, empty: false)).ToList();
        var p = RouteChoiceModel.Logit(times, logitBeta);
        int i = routes.Count == 1 ? 0 : OdMatrix.PickIndex(rng, p);
        if (i < 0) i = 0;

        string key = v.Origin + "->" + destination.NodeId;
        if (!stats.TryGetValue(key, out var st))
            stats[key] = st = new PairStats { key = key, routes = routes.ToList(), chosen = new int[routes.Count], chosenSinceLog = new int[routes.Count] };
        if (!warmingUp)
        {
            st.chosen[i]++;
            st.chosenSinceLog[i]++;
            if (routes.Count > 1)
                EventBus.Publish("route_choice", v.Label, string.Format(Inv, "pair={0};vehicle_type={1};chosen={2};route={3};times_s={4};probs={5}",
                    key, v.ModelType, i, routes[i].name,
                    string.Join("|", times.Select(t => t.ToString("F1", Inv))), string.Join("|", p.Select(x => x.ToString("F3", Inv)))));
        }
        return routes[i].lanes;
    }

    // ------------------------------------------------------------
    // Registro periodico
    // ------------------------------------------------------------
    public void ResetCounters()
    {
        foreach (var st in stats.Values) { System.Array.Clear(st.chosen, 0, st.chosen.Length); System.Array.Clear(st.chosenSinceLog, 0, st.chosenSinceLog.Length); }
        sinceLog = 0f;
    }

    public void Tick(float dt, double simTimeS)
    {
        now = simTimeS;
        sinceLog += dt;
        if (sinceLog < shareLogIntervalS) return;
        sinceLog = 0f;
        foreach (var st in stats.Values.OrderBy(s => s.key, System.StringComparer.Ordinal))
        {
            if (st.routes.Count < 2) continue;
            var times = st.routes.Select(r => RouteTime(r, empty: false)).ToList();
            var p = RouteChoiceModel.Logit(times, logitBeta);
            int total = st.chosen.Sum();
            EventBus.Publish("route_share", st.key, string.Format(Inv,
                "pair={0};routes={1};time_s={2};prob={3};chosen_total={4};share_total={5};chosen_last={6};free_time_s={7}",
                st.key, string.Join("|", st.routes.Select(r => r.name)),
                string.Join("|", times.Select(t => t.ToString("F1", Inv))), string.Join("|", p.Select(x => x.ToString("F3", Inv))),
                string.Join("|", st.chosen), string.Join("|", st.chosen.Select(c => total > 0 ? (c / (float)total).ToString("F3", Inv) : "0")),
                string.Join("|", st.chosenSinceLog), string.Join("|", st.routes.Select(r => r.freeTimeS.ToString("F1", Inv)))));
            System.Array.Clear(st.chosenSinceLog, 0, st.chosenSinceLog.Length);
        }
    }

    /// <summary>Para diagnostico y para el validador: rutas de un par con su tiempo actual y su probabilidad.</summary>
    public string Describe(TrafficLane from, TrafficLane to, bool longVehicle, double simTimeS)
    {
        now = simTimeS;
        var routes = Routes(from, to, longVehicle);
        var times = routes.Select(r => RouteTime(r, empty: false)).ToList();
        var p = RouteChoiceModel.Logit(times, logitBeta);
        return string.Join("; ", routes.Select((r, i) => string.Format(Inv, "{0}: libre {1:F1} s, ahora {2:F1} s, P {3:F2}", r.name, r.freeTimeS, times[i], p[i])));
    }

    public int[] ChosenCounts(string pairKey) => stats.TryGetValue(pairKey, out var st) ? st.chosen : new int[0];
}
