// ============================================================
// LaneRoomAnalyzer.cs
// SimBici > Analyze Lane Room: para cada carril de trafico (no conector)
// calcula el espacio lateral disponible para adelantar al ciclista:
//   - leftCurbM: distancia del eje a la solera izquierda (rayos sobre la calzada)
//   - opposingLane / opposingOffsetM: carril paralelo en sentido contrario a la
//     izquierda (null = sentido unico) y la distancia entre ejes.
// Mediana de muestras cada 2 m, sin los ultimos 6 m de cada extremo.
// Correr despues de regenerar intersecciones (lo hacen tambien los generadores).
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

public static class LaneRoomAnalyzer
{
    [MenuItem("SimBici/Analyze Lane Room")]
    private static void Menu() => Debug.Log(AnalyzeAll());

    public static string AnalyzeAll()
    {
        var log = new StringBuilder();
        var lanes = Object.FindObjectsByType<TrafficLane>(FindObjectsSortMode.None)
                          .Where(l => !l.IsConnector)
                          .OrderBy(l => l.name, System.StringComparer.Ordinal).ToList();
        var polys = lanes.ToDictionary(l => l, Sample);

        int oneWay = 0, twoWay = 0;
        foreach (var lane in lanes)
        {
            var pts = polys[lane];
            float len = pts.Count > 0 ? pts[pts.Count - 1].s : 0f;
            var mid = pts.Where(p => len < 16f || (p.s > 6f && p.s < len - 6f)).ToList();
            if (mid.Count == 0) mid = pts;

            // Solera izquierda
            var curbs = new List<float>();
            foreach (var p in mid)
            {
                if (!OnRoad(p.pos)) continue;
                for (float x = 0f; x < 15f; x += 0.05f)
                    if (!OnRoad(p.pos - p.right * x)) { curbs.Add(x); break; }
            }
            float leftCurb = curbs.Count > 0 ? Median(curbs) : 0f;

            // Carril contrario: otro carril a la izquierda (1.5-6 m), paralelo y en sentido opuesto
            TrafficLane best = null;
            int bestHits = 0;
            float bestOffset = 0f;
            foreach (var other in lanes)
            {
                if (other == lane) continue;
                var offs = new List<float>();
                foreach (var p in mid)
                {
                    if (!Nearest(polys[other], p.pos, out var q)) continue;
                    Vector3 d = q.pos - p.pos;
                    d.y = 0f;
                    float lat = -Vector3.Dot(d, p.right);  // + = a la izquierda
                    float along = Vector3.Dot(d, p.fwd);
                    if (lat > 1.5f && lat < 6f && Mathf.Abs(along) < 1.5f && Vector3.Dot(q.fwd, p.fwd) < -0.8f) offs.Add(lat);
                }
                if (offs.Count > bestHits) { bestHits = offs.Count; best = other; bestOffset = Median(offs); }
            }
            if (best != null && bestHits < mid.Count * 0.5f) best = null;

            var so = new SerializedObject(lane);
            so.FindProperty("leftCurbM").floatValue = leftCurb;
            so.FindProperty("opposingLane").objectReferenceValue = best;
            so.FindProperty("opposingOffsetM").floatValue = best != null ? bestOffset : 0f;
            so.ApplyModifiedProperties();

            if (best == null) oneWay++; else twoWay++;
            if (len >= 16f)
                log.AppendLine($"  {lane.name}: solera izquierda a {leftCurb:F2} m; " +
                               (best != null ? $"contrario {best.name} a {bestOffset:F2} m" : "sentido unico"));
        }

        if (lanes.Count > 0) EditorSceneManager.MarkSceneDirty(lanes[0].gameObject.scene);
        log.Insert(0, $"[LaneRoomAnalyzer] {lanes.Count} carriles: {twoWay} con carril contrario, {oneWay} de sentido unico\n");
        return log.ToString();
    }

    private struct P { public float s; public Vector3 pos, fwd, right; }

    private static List<P> Sample(TrafficLane l)
    {
        var c = l.GetComponent<SplineContainer>();
        float len = c.Spline.GetLength();
        var list = new List<P>();
        for (float s = 0f; s <= len + 0.01f; s += 2f)
        {
            float t = c.Spline.ConvertIndexUnit(Mathf.Min(s, len), PathIndexUnit.Distance, PathIndexUnit.Normalized);
            Vector3 f = c.transform.TransformDirection((Vector3)c.Spline.EvaluateTangent(t));
            f.y = 0f;
            f.Normalize();
            list.Add(new P { s = s, pos = c.transform.TransformPoint((Vector3)c.Spline.EvaluatePosition(t)), fwd = f, right = Vector3.Cross(Vector3.up, f) });
        }
        return list;
    }

    private static bool Nearest(List<P> poly, Vector3 x, out P best)
    {
        best = default;
        float bd = float.PositiveInfinity;
        foreach (var p in poly)
        {
            float d = (p.pos - x).sqrMagnitude;
            if (d < bd) { bd = d; best = p; }
        }
        return bd < 64f;  // a menos de 8 m
    }

    private static bool OnRoad(Vector3 p) =>
        Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 10f) &&
        hit.collider.gameObject.layer == LayerMask.NameToLayer("Ground") && !(hit.collider is TerrainCollider);

    private static float Median(List<float> v)
    {
        v.Sort();
        return v[v.Count / 2];
    }
}
