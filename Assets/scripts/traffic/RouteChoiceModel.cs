// ============================================================
// RouteChoiceModel.cs
// Matematica pura de la eleccion de ruta (E3 del plan de rutas):
//   - BPR:   t = t_base * (1 + alpha * (v / c)^gamma)
//   - logit: P(r_i) = exp(beta * T_i) / sum_k exp(beta * T_k), con beta < 0
//   - capacidad de un giro que cede (HCM, "potential capacity"):
//       c = q_c * exp(-q_c t_c / 3600) / (1 - exp(-q_c t_f / 3600))
//   - demora uniforme de un semaforo con flujo bajo (primer termino de Webster):
//       d = C (1 - g/C)^2 / 2
//   - catalogo de rutas: todas las rutas entre dos carriles que no repiten
//     interseccion, ordenadas por tiempo a flujo libre.
// Sin MonoBehaviour, para testearla. La usa RouteChoice.
// ============================================================

using System;
using System.Collections.Generic;
using UnityEngine;

public static class RouteChoiceModel
{
    /// <summary>Tiempo de un tramo con la curva BPR (s). c &lt;= 0 = sin congestion.</summary>
    public static float Bpr(float baseTimeS, float flowVehPerHour, float capacityVehPerHour, float alpha, float exponent)
    {
        if (capacityVehPerHour <= 0f || flowVehPerHour <= 0f) return baseTimeS;
        return baseTimeS * (1f + alpha * Mathf.Pow(flowVehPerHour / capacityVehPerHour, exponent));
    }

    /// <summary>
    /// Probabilidades logit. Se resta el minimo antes de la exponencial para que no se desborde con tiempos grandes.
    /// beta &lt; 0: mas tiempo, menos probabilidad.
    /// </summary>
    public static float[] Logit(IReadOnlyList<float> timesS, float beta)
    {
        var p = new float[timesS.Count];
        if (p.Length == 0) return p;
        float min = float.PositiveInfinity;
        foreach (float t in timesS) min = Mathf.Min(min, t);
        double sum = 0.0;
        for (int i = 0; i < p.Length; i++) { double e = Math.Exp(beta * (timesS[i] - min)); p[i] = (float)e; sum += e; }
        for (int i = 0; i < p.Length; i++) p[i] = (float)(p[i] / sum);
        return p;
    }

    /// <summary>Capacidad potencial de un movimiento que cede a un flujo q_c (veh/h), HCM. Con q_c = 0: 3600 / t_f.</summary>
    public static float YieldCapacity(float conflictingVehPerHour, float criticalGapS, float followUpS)
    {
        followUpS = Mathf.Max(followUpS, 0.1f);
        if (conflictingVehPerHour <= 1e-3f) return 3600f / followUpS;
        double q = conflictingVehPerHour / 3600.0;
        return (float)(conflictingVehPerHour * Math.Exp(-q * criticalGapS) / (1.0 - Math.Exp(-q * followUpS)));
    }

    /// <summary>Demora media de un semaforo con flujo bajo (s): C (1 - g/C)^2 / 2.</summary>
    public static float SignalDelay(float cycleS, float greenS)
    {
        if (cycleS <= 0f) return 0f;
        float red = 1f - Mathf.Clamp01(greenS / cycleS);
        return cycleS * red * red * 0.5f;
    }

    /// <summary>
    /// Todas las rutas de 'start' a 'goal' que no repiten carril ni interseccion. 'intersectionOf' devuelve la
    /// interseccion de un conector (null si el carril no es un conector). 'allowed' filtra carriles (p. ej. los
    /// conectores donde cabe un bus); null = todos. Orden determinista (el de nextLanes).
    /// </summary>
    public static List<List<TrafficLane>> EnumerateRoutes(TrafficLane start, TrafficLane goal,
                                                         Func<TrafficLane, string> intersectionOf,
                                                         Predicate<TrafficLane> allowed = null, int maxRoutes = 64)
    {
        var result = new List<List<TrafficLane>>();
        if (start == null || goal == null) return result;
        var path = new List<TrafficLane> { start };
        var seen = new HashSet<string>();
        Search(start, goal, path, seen, intersectionOf, allowed, result, maxRoutes);
        return result;
    }

    private static void Search(TrafficLane current, TrafficLane goal, List<TrafficLane> path, HashSet<string> seen,
                               Func<TrafficLane, string> intersectionOf, Predicate<TrafficLane> allowed,
                               List<List<TrafficLane>> result, int maxRoutes)
    {
        if (result.Count >= maxRoutes) return;
        if (current == goal) { result.Add(new List<TrafficLane>(path)); return; }
        foreach (var c in current.NextLanes)
        {
            var next = c.lane;
            if (next == null || path.Contains(next)) continue;
            if (allowed != null && next != goal && !allowed(next)) continue;
            string inter = intersectionOf(next);
            if (inter != null && seen.Contains(inter)) continue;
            path.Add(next);
            if (inter != null) seen.Add(inter);
            Search(next, goal, path, seen, intersectionOf, allowed, result, maxRoutes);
            path.RemoveAt(path.Count - 1);
            if (inter != null) seen.Remove(inter);
        }
    }

    /// <summary>
    /// Se quedan las rutas cuyo tiempo libre no supera 'maxFactor' veces el de la mejor, hasta 'maxRoutes', de la mas
    /// rapida a la mas lenta. Devuelve los indices en 'freeTimes'.
    /// </summary>
    public static List<int> SelectRoutes(IReadOnlyList<float> freeTimes, int maxRoutes, float maxFactor)
    {
        var idx = new List<int>();
        for (int i = 0; i < freeTimes.Count; i++) idx.Add(i);
        idx.Sort((a, b) => freeTimes[a] != freeTimes[b] ? freeTimes[a].CompareTo(freeTimes[b]) : a.CompareTo(b));
        if (idx.Count == 0) return idx;
        float best = freeTimes[idx[0]];
        var kept = new List<int>();
        foreach (int i in idx)
        {
            if (kept.Count >= Mathf.Max(1, maxRoutes)) break;
            if (freeTimes[i] > best * maxFactor) break;
            kept.Add(i);
        }
        return kept;
    }
}
