// ============================================================
// NetworkValidator.cs
// SimBici > Validate Network: revisa que la red de carriles sirva para la
// matriz origen-destino.
//   - Nombres de carril unicos (el orden por nombre es parte del determinismo).
//   - Ninguna conexion a un carril que no existe.
//   - Todo carril tiene por donde llegar y por donde salir, salvo los de los
//     nodos (OdNode): las entradas no tienen antecesor y las salidas no tienen
//     sucesor.
//   - Para cada par entrada -> salida, las rutas posibles (sin repetir
//     interseccion), con su largo y su tiempo a flujo libre.
// No cambia nada de la escena.
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

public static class NetworkValidator
{
    private const int MaxRoutesListed = 4;

    [MenuItem("SimBici/Validate Network")]
    private static void Menu()
    {
        string report = Validate(out int errors);
        if (errors > 0) Debug.LogError(report); else Debug.Log(report);
    }

    public static string Validate(out int errors)
    {
        var inv = CultureInfo.InvariantCulture;
        var log = new StringBuilder("[NetworkValidator]\n");
        errors = 0;

        var lanes = Object.FindObjectsByType<TrafficLane>(FindObjectsSortMode.None)
                          .OrderBy(l => l.name, System.StringComparer.Ordinal).ToList();
        var nodes = Object.FindObjectsByType<OdNode>(FindObjectsSortMode.None)
                          .OrderBy(n => n.NodeId, System.StringComparer.Ordinal).ToList();
        log.AppendLine($"  carriles: {lanes.Count}; nodos: {string.Join(", ", nodes.Select(n => $"{n.NodeId}={(n.Lane != null ? n.Lane.name : "?")} ({n.Kind})"))}");

        foreach (var dup in lanes.GroupBy(l => l.name).Where(g => g.Count() > 1))
        { errors++; log.AppendLine($"  ERROR: nombre repetido {dup.Key} ({dup.Count()} carriles)"); }

        var entryLanes = new HashSet<TrafficLane>(nodes.Where(n => n.Kind == OdNode.NodeKind.Entry && n.Lane != null).Select(n => n.Lane));
        var exitLanes = new HashSet<TrafficLane>(nodes.Where(n => n.Kind == OdNode.NodeKind.Exit && n.Lane != null).Select(n => n.Lane));
        foreach (var n in nodes.Where(n => n.Lane == null)) { errors++; log.AppendLine($"  ERROR: nodo {n.NodeId} sin carril"); }

        var hasPredecessor = new HashSet<TrafficLane>();
        foreach (var l in lanes)
            for (int i = 0; i < l.NextLanes.Count; i++)
            {
                var next = l.NextLanes[i].lane;
                if (next == null) { errors++; log.AppendLine($"  ERROR: {l.name} conecta con un carril que no existe (posicion {i})"); }
                else hasPredecessor.Add(next);
            }

        foreach (var l in lanes)
        {
            bool isEntry = entryLanes.Contains(l), isExit = exitLanes.Contains(l);
            bool hasNext = l.NextLanes.Any(n => n.lane != null);
            if (!hasNext && !isExit) { errors++; log.AppendLine($"  ERROR: {l.name} no tiene salida y no es nodo de salida"); }
            if (hasNext && isExit) { errors++; log.AppendLine($"  ERROR: {l.name} es nodo de salida pero sigue a otro carril"); }
            if (!hasPredecessor.Contains(l) && !isEntry) { errors++; log.AppendLine($"  ERROR: {l.name} no tiene por donde llegar y no es nodo de entrada"); }
        }

        foreach (var from in nodes.Where(n => n.Kind == OdNode.NodeKind.Entry && n.Lane != null))
            foreach (var to in nodes.Where(n => n.Kind == OdNode.NodeKind.Exit && n.Lane != null))
            {
                var routes = Routes(from.Lane, to.Lane);
                if (routes.Count == 0) { errors++; log.AppendLine($"  ERROR: no hay ruta {from.NodeId} -> {to.NodeId}"); continue; }
                log.AppendLine($"  {from.NodeId} -> {to.NodeId}: {routes.Count} rutas");
                foreach (var r in routes.OrderBy(r => r.timeS).Take(MaxRoutesListed))
                    log.AppendLine(string.Format(inv, "      {0,5:F0} m  {1,5:F1} s  {2}", r.lengthM, r.timeS, r.description));
            }

        log.AppendLine(errors == 0 ? "  OK: sin errores" : $"  {errors} errores");
        return log.ToString();
    }

    private static List<(float lengthM, float timeS, string description)> Routes(TrafficLane start, TrafficLane goal)
    {
        var result = new List<(float, float, string)>();
        var path = new List<TrafficLane> { start };
        var seen = new HashSet<string>();
        Search(start, goal, path, seen, result);
        return result;
    }

    private static void Search(TrafficLane current, TrafficLane goal, List<TrafficLane> path, HashSet<string> seenIntersections,
                               List<(float, float, string)> result)
    {
        if (current == goal)
        {
            float length = 0f, time = 0f;
            var parts = new List<string>();
            foreach (var l in path)
            {
                float len = l.GetComponent<SplineContainer>().Spline.GetLength();
                length += len;
                time += len / Mathf.Max(l.SpeedLimitMps, 0.1f);
                if (l.IsConnector) parts.Add($"{IntersectionOf(l)}:{l.Movement}");
                else if (!l.name.StartsWith("I_")) parts.Add(l.name.Replace("Lane_", ""));
            }
            result.Add((length, time, string.Join(" > ", parts)));
            return;
        }
        foreach (var n in current.NextLanes)
        {
            if (n.lane == null || path.Contains(n.lane)) continue;
            string inter = n.lane.IsConnector ? IntersectionOf(n.lane) : null;
            if (inter != null && seenIntersections.Contains(inter)) continue;
            path.Add(n.lane);
            if (inter != null) seenIntersections.Add(inter);
            Search(n.lane, goal, path, seenIntersections, result);
            path.RemoveAt(path.Count - 1);
            if (inter != null) seenIntersections.Remove(inter);
        }
    }

    /// <summary>Prefijo de interseccion de un conector generado (I_NO_E_Conn_... -> I_NO).</summary>
    private static string IntersectionOf(TrafficLane l)
    {
        var p = l.name.Split('_');
        return p.Length > 1 ? p[0] + "_" + p[1] : l.name;
    }
}
