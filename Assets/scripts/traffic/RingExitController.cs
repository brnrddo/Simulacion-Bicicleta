// ============================================================
// RingExitController.cs
// Mantiene estable la cantidad de autos en la red regulando la calle de
// salida: en una interseccion del anillo ajusta, en runtime, que proporcion
// de los autos del anillo dobla hacia el brazo de salida.
//
//   proporcion = clamp(base + ganancia * (autos - objetivo) / objetivo, min, max)
//
// objetivo = TrafficManager.TargetVehicles (vehiclesPerLevel x Flow Multiplier).
// Con mas autos que el objetivo salen mas; con menos, se quedan circulando.
// Lo avanza TrafficManager dentro de su paso (tambien en el precalentamiento):
// determinista, misma semilla = mismas decisiones.
// Cada 30 s publica "traffic_level" (autos, objetivo, proporcion de salida).
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public class RingExitController : MonoBehaviour
{
    [Header("Donde")]
    [Tooltip("Interseccion del anillo con la calle de salida.")]
    [SerializeField] private IntersectionLaneBuilder intersection;
    [Tooltip("Accesos por los que llegan los autos del anillo.")]
    [SerializeField] private List<Approach> ringApproaches = new List<Approach> { Approach.West, Approach.East };
    [Tooltip("Brazo por el que salen de la red.")]
    [SerializeField] private Approach exitArm = Approach.South;

    [Header("Regulacion")]
    [SerializeField, Range(0f, 1f)] private float baseExitShare = 0.3f;
    [SerializeField, Range(0f, 1f)] private float minExitShare = 0.05f;
    [SerializeField, Range(0f, 1f)] private float maxExitShare = 0.8f;
    [Tooltip("Cuanto sube la proporcion por cada 100 % de exceso sobre el objetivo.")]
    [SerializeField, Min(0f)] private float gain = 1.0f;
    [SerializeField, Min(0.1f)] private float updateIntervalS = 1f;
    [SerializeField, Min(1f)] private float logIntervalS = 30f;

    public float CurrentExitShare { get; private set; }

    private class Controlled
    {
        public TrafficLane entry;
        public TrafficLane exitConn;
        public readonly List<(TrafficLane lane, float share)> others = new List<(TrafficLane, float)>();  // reparto original
    }

    private readonly List<Controlled> controlled = new List<Controlled>();
    private float sinceUpdate = float.PositiveInfinity;
    private float sinceLog;

    /// <summary>Busca los carriles generados por el builder (por nombre: sobrevive a regenerar).</summary>
    public void Resolve()
    {
        controlled.Clear();
        if (intersection == null) { Debug.LogWarning($"[RingExitController] {name}: falta la interseccion."); return; }

        var all = intersection.GetComponentsInChildren<TrafficLane>(true);
        string exitOut = $"{intersection.prefix}_{IntersectionController.ApproachCode(exitArm)}_Out";
        foreach (var ap in ringApproaches)
        {
            string inName = $"{intersection.prefix}_{IntersectionController.ApproachCode(ap)}_In";
            TrafficLane entry = null;
            foreach (var l in all) if (l.name == inName) entry = l;
            if (entry == null) { Debug.LogWarning($"[RingExitController] {name}: no existe {inName}."); continue; }

            var c = new Controlled { entry = entry };
            float othersTotal = 0f;
            foreach (var n in entry.NextLanes)
            {
                if (n.lane == null) continue;
                bool isExit = n.lane.NextLanes.Count > 0 && n.lane.NextLanes[0].lane != null &&
                              n.lane.NextLanes[0].lane.name == exitOut;
                if (isExit) c.exitConn = n.lane;
                else { c.others.Add((n.lane, n.weight)); othersTotal += n.weight; }
            }
            if (c.exitConn == null) { Debug.LogWarning($"[RingExitController] {name}: {inName} no conecta con {exitOut}."); continue; }

            // Normalizar el reparto entre los movimientos que no salen (se conserva su proporcion)
            for (int i = 0; i < c.others.Count; i++)
                c.others[i] = (c.others[i].lane, othersTotal > 0f ? c.others[i].share / othersTotal : 1f / c.others.Count);
            controlled.Add(c);
        }
    }

    public void Tick(float dt, TrafficManager tm)
    {
        if (controlled.Count == 0) return;

        sinceUpdate += dt;
        if (sinceUpdate >= updateIntervalS)
        {
            sinceUpdate = 0f;
            int target = tm.TargetVehicles;
            float share = target <= 0 ? maxExitShare
                        : Mathf.Clamp(baseExitShare + gain * (tm.ActiveCount - target) / (float)target, minExitShare, maxExitShare);
            CurrentExitShare = share;

            foreach (var c in controlled)
            {
                c.entry.SetWeight(c.exitConn, share * 100f);
                foreach (var (lane, s) in c.others) c.entry.SetWeight(lane, (1f - share) * s * 100f);
            }
        }

        if (tm.IsWarmingUp) return;
        sinceLog += dt;
        if (sinceLog >= logIntervalS)
        {
            sinceLog = 0f;
            EventBus.Publish("traffic_level", name, string.Format(CultureInfo.InvariantCulture,
                "vehicles={0};target={1};exit_share={2:F2};flow_multiplier={3}",
                tm.ActiveCount, tm.TargetVehicles, CurrentExitShare, tm.FlowMultiplier));
        }
    }
}
