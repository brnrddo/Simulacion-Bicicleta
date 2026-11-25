// ============================================================
// RoutePlanner.cs
// Ruta de menor costo entre dos carriles de la red (Dijkstra sobre las
// conexiones nextLanes). El costo de cada carril lo entrega quien llama
// (TrafficManager usa el tiempo a flujo libre: largo / limite de velocidad).
// Una ruta es la lista de carriles desde el de partida hasta el de destino,
// ambos incluidos. Logica pura para poder testearla.
// En E2 cada auto sigue la ruta mas rapida hacia su destino; E3 la reemplaza
// por la eleccion logit entre varias rutas.
// ============================================================

using System;
using System.Collections.Generic;

public static class RoutePlanner
{
    /// <param name="cost">Costo de recorrer un carril completo (por ejemplo, segundos a flujo libre).</param>
    /// <param name="allowed">Carriles que se pueden usar (null = todos), p. ej. conectores donde cabe un bus.</param>
    /// <returns>La ruta (inicio y destino incluidos) o null si no hay.</returns>
    public static List<TrafficLane> Shortest(TrafficLane start, TrafficLane goal, Func<TrafficLane, float> cost,
                                             Predicate<TrafficLane> allowed = null)
    {
        if (start == null || goal == null) return null;
        if (start == goal) return new List<TrafficLane> { start };

        var dist = new Dictionary<TrafficLane, float> { [start] = 0f };
        var prev = new Dictionary<TrafficLane, TrafficLane>();
        var done = new HashSet<TrafficLane>();
        // Redes de decenas de carriles: basta una busqueda lineal del minimo (y el empate se resuelve por nombre)
        var open = new List<TrafficLane> { start };

        while (open.Count > 0)
        {
            int best = 0;
            for (int i = 1; i < open.Count; i++)
            {
                float a = dist[open[i]], b = dist[open[best]];
                if (a < b || (a == b && string.CompareOrdinal(open[i].name, open[best].name) < 0)) best = i;
            }
            var lane = open[best];
            open.RemoveAt(best);
            if (!done.Add(lane)) continue;
            if (lane == goal) break;

            float d = dist[lane] + cost(lane);
            foreach (var c in lane.NextLanes)
            {
                var next = c.lane;
                if (next == null || done.Contains(next)) continue;
                if (allowed != null && next != goal && !allowed(next)) continue;
                if (dist.TryGetValue(next, out float old) && old <= d) continue;
                dist[next] = d;
                prev[next] = lane;
                if (!open.Contains(next)) open.Add(next);
            }
        }

        if (!prev.ContainsKey(goal)) return null;
        var route = new List<TrafficLane> { goal };
        for (var l = goal; l != start; l = prev[l]) route.Add(prev[l]);
        route.Reverse();
        return route;
    }
}
