// ============================================================
// StopLine.cs
// Linea de detencion de un acceso semaforizado. Por convencion la linea
// es el FINAL del tramo de llegada en el que esta el componente.
//
// Autos: va en el carril de llegada (TrafficLane). TrafficManager la usa en
// FindLeader: en rojo es un obstaculo detenido de largo 0 en el final del
// carril; en amarillo aplica SignalRules.ShouldStopOnYellow una vez por auto.
//
// Ciclista (isCyclistApproach): va en el tramo de llegada de la red del
// ciclista (CyclistPath). Con RiderRoute sabe en que tramo va la bici y a
// cuantos metros esta de la linea. Publica:
//   cyclist_wait_start / cyclist_wait_end  (si se detiene antes de la linea)
//   cyclist_stopline_cross                 (al cruzarla: pasa del tramo de llegada a la interseccion)
//   cyclist_intersection_exit              (al salir: movimiento realizado y tiempo en la interseccion;
//                                           se publica aparte porque al cruzar la linea todos los
//                                           giros parten del mismo punto y aun no se sabe cual tomo)
// La genera SimBici > Generate Cyclist Network, copiando la senal del carril de autos.
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

[DefaultExecutionOrder(60)]  // despues de RiderRoute (50)
public class StopLine : MonoBehaviour
{
    [Header("Senal")]
    [Tooltip("Signal = semaforo (requiere controller). Stop = PARE (detencion obligatoria y brecha). Yield = CEDA EL PASO.")]
    [SerializeField] private ApproachControl control = ApproachControl.Signal;
    [SerializeField] private IntersectionController controller;
    [SerializeField] private Approach approach;

    [Header("Acceso del ciclista (en un CyclistPath)")]
    [SerializeField] private bool isCyclistApproach;
    [Tooltip("Distancia antes de la linea en que una detencion cuenta como 'espera' en este acceso (m).")]
    [SerializeField] private float approachZoneM = 30f;
    [Tooltip("Bajo esta velocidad se considera que el ciclista esta detenido (m/s).")]
    [SerializeField] private float stoppedSpeedMps = 0.3f;

    public ApproachControl Control => control;
    public bool IsSignalized => control == ApproachControl.Signal && controller != null;
    public IntersectionController Controller => controller;
    public Approach Approach => approach;
    public bool IsCyclistApproach => isCyclistApproach;

    private RiderRoute route;
    private BicycleController bike;
    private CyclistPath linePath;
    // Tramos desde los que se llega a la linea y metros extra entre su final y la linea
    private readonly Dictionary<CyclistPath, float> approachPaths = new Dictionary<CyclistPath, float>();

    private bool waiting;
    private bool stoppedBefore;
    private double waitStartS;
    private bool insideIntersection;
    private double crossTimeS;
    private string crossPhase;

    public void Configure(IntersectionController c, Approach a, ApproachControl ctrl, bool cyclist = false)
    {
        controller = c;
        approach = a;
        control = ctrl;
        isCyclistApproach = cyclist;
    }

    private void Awake()
    {
        if (isCyclistApproach) return;
        var lane = GetComponent<TrafficLane>();
        if (lane != null) lane.StopLine = this;
    }

    private void Start()
    {
        if (!isCyclistApproach) return;

        linePath = GetComponent<CyclistPath>();
        route = FindFirstObjectByType<RiderRoute>();
        bike = FindFirstObjectByType<BicycleController>();
        if (linePath == null || route == null || route.Network == null || bike == null || controller == null)
        {
            Debug.LogWarning($"[StopLine] {name}: falta CyclistPath, RiderRoute, BicycleController o IntersectionController; se desactiva.");
            enabled = false;
            return;
        }

        // Tramos de llegada: el propio y sus predecesores hasta cubrir approachZoneM
        approachPaths[linePath] = 0f;
        var frontier = new List<CyclistPath> { linePath };
        while (frontier.Count > 0)
        {
            var nextFrontier = new List<CyclistPath>();
            foreach (var p in frontier)
            {
                float extra = approachPaths[p] + p.LengthM;  // metros entre el final de un predecesor y la linea
                if (extra >= approachZoneM) continue;
                foreach (var pred in route.Network.PredecessorsOf(p))
                {
                    if (approachPaths.ContainsKey(pred)) continue;
                    approachPaths[pred] = extra;
                    nextFrontier.Add(pred);
                }
            }
            frontier = nextFrontier;
        }

        route.PathChanged += OnPathChanged;
    }

    private void OnDestroy()
    {
        if (route != null) route.PathChanged -= OnPathChanged;
    }

    private void OnPathChanged(CyclistPath from, CyclistPath to, bool advanced)
    {
        if (!advanced) return;
        float speed = bike.CurrentSpeedMps;

        if (from == linePath)
        {
            if (waiting) EndWait(speed, 0f);
            crossPhase = controller.GetState(approach).Phase.ToString();
            Publish("cyclist_stopline_cross", speed, 0f, double.NaN);
            stoppedBefore = false;
            insideIntersection = true;
            crossTimeS = Time.timeAsDouble;
        }
        else if (insideIntersection && IsSuccessorOfLine(from))
        {
            // Salio del giro que realmente recorrio (from) hacia la calle de salida (to)
            insideIntersection = false;
            EventBus.Publish("cyclist_intersection_exit", "cyclist", string.Format(CultureInfo.InvariantCulture,
                "intersection={0};approach={1};movement={2};exit_path={3};phase_at_cross={4};time_in_intersection_s={5:F2};speed_mps={6:F2}",
                controller.name, IntersectionController.ApproachCode(approach), from.name, to.name, crossPhase,
                Time.timeAsDouble - crossTimeS, speed));
        }
    }

    private bool IsSuccessorOfLine(CyclistPath p)
    {
        foreach (var n in linePath.Next) if (n == p) return true;
        return false;
    }

    private void FixedUpdate()
    {
        if (!isCyclistApproach || !route.HasFix) return;

        if (!approachPaths.TryGetValue(route.CurrentPath, out float extra))
        {
            // Fuera del acceso (reaparicion, otro camino): olvidar el estado sin registrar nada
            waiting = false;
            stoppedBefore = false;
            if (insideIntersection && !IsSuccessorOfLine(route.CurrentPath)) insideIntersection = false;
            return;
        }

        float distToLine = route.CurrentPath.LengthM - route.PathS + extra;
        if (distToLine > approachZoneM) return;

        float speed = bike.CurrentSpeedMps;
        if (!waiting && speed < stoppedSpeedMps)
        {
            waiting = true;
            stoppedBefore = true;
            waitStartS = Time.timeAsDouble;
            Publish("cyclist_wait_start", speed, distToLine, double.NaN);
        }
        else if (waiting && speed > stoppedSpeedMps + 0.2f)
        {
            EndWait(speed, distToLine);
        }
    }

    private void EndWait(float speed, float distanceToLine)
    {
        waiting = false;
        Publish("cyclist_wait_end", speed, distanceToLine, Time.timeAsDouble - waitStartS);
    }

    private void Publish(string type, float speed, float distanceToLine, double waitS)
    {
        SignalState s = controller.GetState(approach);
        string detail = string.Format(CultureInfo.InvariantCulture,
            "intersection={0};approach={1};phase={2};time_in_phase_s={3:F2};time_to_change_s={4:F2};" +
            "speed_mps={5:F2};dist_to_line_m={6:F2};stopped_before={7}",
            controller.name, IntersectionController.ApproachCode(approach), s.Phase, s.TimeInPhaseS,
            s.TimeToChangeS, speed, distanceToLine, stoppedBefore ? 1 : 0);
        if (!double.IsNaN(waitS)) detail += string.Format(CultureInfo.InvariantCulture, ";wait_s={0:F2}", waitS);
        EventBus.Publish(type, "cyclist", detail);
    }

    private void OnDrawGizmos()
    {
        var c = GetComponent<UnityEngine.Splines.SplineContainer>();
        if (c == null || c.Spline == null || c.Spline.Count == 0) return;
        Vector3 p = c.transform.TransformPoint((Vector3)UnityEngine.Splines.SplineUtility.EvaluatePosition(c.Spline, 1f));
        Gizmos.color = isCyclistApproach ? Color.cyan : Color.white;
        Gizmos.DrawCube(p + Vector3.up * 0.05f, isCyclistApproach ? new Vector3(0.8f, 0.1f, 0.8f) : new Vector3(1.5f, 0.1f, 1.5f));
    }
}
