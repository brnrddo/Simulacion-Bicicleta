// ============================================================
// BikeConflictZone.cs
// Va en un conector de autos que GIRA (derecha o izquierda) y cruza caminos
// del ciclista que siguen derecho (p. ej. el "right hook": el auto dobla a la
// derecha cruzando la ciclovia por la que la bici sigue recto).
// La genera SimBici > Generate Cyclist Network. En runtime calcula en cuanto
// tiempo llegaria la bici al punto de conflicto, mirando el tramo actual del
// ciclista (RiderRoute) y los tramos que llegan a la interseccion.
// ============================================================

using System;
using System.Collections.Generic;
using UnityEngine;

public class BikeConflictZone : MonoBehaviour
{
    [Serializable]
    public class Entry
    {
        public CyclistPath bikePath;
        [Tooltip("Distancia sobre el conector de autos hasta el cruce (m).")]
        public float sCar;
        [Tooltip("Distancia sobre el tramo de bici hasta el cruce (m).")]
        public float sBike;
    }

    [SerializeField] private List<Entry> entries = new List<Entry>();
    [Tooltip("Se miran los tramos de bici que llegan al cruce hasta esta distancia (m).")]
    [SerializeField] private float approachLookbackM = 40f;

    public IReadOnlyList<Entry> Entries => entries;

    // Por entrada: tramos de bici que llegan al cruce -> metros desde su FINAL hasta el cruce
    private readonly List<Dictionary<CyclistPath, float>> reach = new List<Dictionary<CyclistPath, float>>();

    public void SetEntries(List<Entry> list) => entries = list;

    /// <summary>
    /// Arma, por entrada, los tramos desde los que la bici puede llegar al cruce y la distancia
    /// desde el FINAL de cada uno hasta el cruce. Llamar una vez con la red ya cargada.
    /// </summary>
    public void Build(CyclistNetwork network)
    {
        reach.Clear();
        foreach (var e in entries)
        {
            var map = new Dictionary<CyclistPath, float>();
            if (e.bikePath == null || network == null) { reach.Add(map); continue; }

            // El propio tramo se trata aparte (distancia = sBike - S)
            var frontier = new List<(CyclistPath p, float toConflictFromEnd)>();
            foreach (var pred in network.PredecessorsOf(e.bikePath)) frontier.Add((pred, e.sBike));
            while (frontier.Count > 0)
            {
                var next = new List<(CyclistPath, float)>();
                foreach (var (p, d) in frontier)
                {
                    if (map.ContainsKey(p) || d > approachLookbackM) continue;
                    map[p] = d;
                    foreach (var pp in network.PredecessorsOf(p)) next.Add((pp, d + p.LengthM));
                }
                frontier = next;
            }
            reach.Add(map);
        }
    }

    /// <summary>
    /// Menor tiempo de llegada de la bici a algun cruce de este conector (PositiveInfinity si no viene).
    /// Solo cuenta cruces que el auto aun no pasa (sCar mayor que carFrontS).
    /// </summary>
    public float BikeTimeToConflict(RiderRoute route, float bikeSpeedMps, float carFrontS)
    {
        if (route == null || !route.HasFix) return float.PositiveInfinity;
        float best = float.PositiveInfinity;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.sCar < carFrontS) continue;

            float d;
            if (route.CurrentPath == e.bikePath) d = e.sBike - route.PathS;
            else if (i < reach.Count && reach[i].TryGetValue(route.CurrentPath, out float fromEnd))
                d = route.CurrentPath.LengthM - route.PathS + fromEnd;
            else continue;

            if (d < -1f) continue;  // la bici ya paso el cruce
            float t = TrafficRules.TimeToReach(Mathf.Max(d, 0f), bikeSpeedMps, 2f);
            if (t < best) best = t;
        }
        return best;
    }
}
