// ============================================================
// OvertakeMonitor.cs
// Mide cada pasada de un auto junto al ciclista (con o sin maniobra) y la
// publica como "overtake_pass" (source=ambient):
//   - distancia planificada D (la sorteada por el auto; NaN si no sorteo)
//   - distancia MINIMA REAL: de los dos extremos del manubrio a la superficie
//     del collider del auto (Collider.ClosestPoint), en todo el encuentro
//   - velocidad del auto y velocidad relativa en el punto mas cercano
//   - tiempo a colision con el trafico de frente al decidir, maniobra, tipo de auto
// Una pasada empieza cuando un auto en el mismo sentido esta detras de la bici
// (a menos de 5 m lateral) y termina cuando su frente queda adelante.
// Va en la bici (playerBike). Orden 70: despues del trafico y de RiderRoute.
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

[DefaultExecutionOrder(70)]
public class OvertakeMonitor : MonoBehaviour
{
    [SerializeField] private BicycleController bike;
    [Tooltip("Centro del manubrio (se busca un hijo llamado 'Handlebar*' si esta vacio).")]
    [SerializeField] private Renderer handlebar;
    [SerializeField] private RiderRoute route;
    [SerializeField] private float trackRadiusM = 25f;
    [SerializeField] private float lateralLimitM = 5f;

    private class Track
    {
        public TrafficVehicle v;
        public float minDist = float.PositiveInfinity;
        public float carSpeed, relSpeed, planned, ttc;
        public string maneuver, side;
    }

    private readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
    private readonly List<int> toDrop = new List<int>();
    private readonly HashSet<int> seen = new HashSet<int>();
    private Vector3 lastBikePos;
    private bool hasLastPos;

    private void Awake()
    {
        if (bike == null) bike = GetComponentInParent<BicycleController>() ?? FindFirstObjectByType<BicycleController>();
        if (route == null) route = FindFirstObjectByType<RiderRoute>();
        if (handlebar == null && bike != null)
            foreach (var r in bike.GetComponentsInChildren<Renderer>())
                if (r.name.StartsWith("Handlebar")) { handlebar = r; break; }
    }

    private void FixedUpdate()
    {
        var tm = TrafficManager.Instance;
        if (tm == null || bike == null) return;

        Vector3 fwd = bike.transform.forward; fwd.y = 0f; fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 center = handlebar != null ? handlebar.bounds.center : bike.RiderPosition + Vector3.up;
        Vector3 tipL = center - right * tm.HandlebarHalfWidthM;
        Vector3 tipR = center + right * tm.HandlebarHalfWidthM;
        float bikeSpeed = bike.CurrentSpeedMps;

        // Teletransporte (reinicio tras choque): los encuentros en curso ya no son pasadas reales
        if (hasLastPos && (bike.RiderPosition - lastBikePos).sqrMagnitude > 9f) tracks.Clear();
        lastBikePos = bike.RiderPosition;
        hasLastPos = true;

        seen.Clear();
        foreach (var v in tm.Vehicles)
        {
            seen.Add(v.Id);
            Vector3 rel = v.transform.position - center; rel.y = 0f;
            float along = Vector3.Dot(rel, fwd);
            float lat = Vector3.Dot(rel, right);
            Vector3 vf = v.transform.forward; vf.y = 0f;
            bool sameDir = Vector3.Dot(vf.normalized, fwd) > 0.7f;

            if (!tracks.TryGetValue(v.Id, out Track t))
            {
                if (sameDir && along < -2f && along > -trackRadiusM && Mathf.Abs(lat) < lateralLimitM)
                    tracks[v.Id] = new Track { v = v };
                continue;
            }

            if (!sameDir || Mathf.Abs(lat) > lateralLimitM + 1f || along < -trackRadiusM - 5f)
            {
                toDrop.Add(v.Id);  // giro o se alejo: no es una pasada
                continue;
            }

            var col = v.GetComponent<Collider>();
            if (col != null)
            {
                float d = Mathf.Min(Vector3.Distance(tipL, col.ClosestPoint(tipL)), Vector3.Distance(tipR, col.ClosestPoint(tipR)));
                if (d < t.minDist)
                {
                    t.minDist = d;
                    t.carSpeed = v.Speed;
                    t.relSpeed = v.Speed - bikeSpeed;
                    t.planned = v.PlannedPassingDistanceM;
                    t.maneuver = v.OvertakeManeuver;
                    t.ttc = v.OvertakeOncomingTtcS;
                    t.side = lat < 0f ? "left" : "right";
                }
            }

            if (along > v.LengthM * 0.5f + 2f)
            {
                if (t.minDist <= lateralLimitM) Publish(t, bikeSpeed);  // si nunca estuvo cerca, no fue una pasada
                toDrop.Add(v.Id);
            }
        }

        foreach (var id in tracks.Keys) if (!seen.Contains(id)) toDrop.Add(id);  // salio de la red
        foreach (var id in toDrop) tracks.Remove(id);
        toDrop.Clear();
    }

    private void Publish(Track t, float bikeSpeed)
    {
        string type = t.v.Prefab != null ? t.v.Prefab.name : t.v.name;
        EventBus.Publish("overtake_pass", t.v.Label, string.Format(CultureInfo.InvariantCulture,
            "source=ambient;vehicle_type={0};planned_m={1:F2};min_real_m={2:F3};car_speed_mps={3:F2};rel_speed_mps={4:F2};" +
            "bike_speed_mps={5:F2};maneuver={6};oncoming_ttc_s={7:F2};side={8};path={9}",
            type, t.planned, t.minDist, t.carSpeed, t.relSpeed, bikeSpeed, t.maneuver, t.ttc, t.side,
            route != null && route.HasFix ? route.CurrentPath.name : ""));
    }
}
