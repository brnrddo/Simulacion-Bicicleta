// ============================================================
// CyclistNetworkEditor.cs
// Genera la red de caminos del ciclista (CyclistNetwork) a partir de la red
// de trafico. Boton en el Inspector de CyclistNetwork y menu
// SimBici > Generate Cyclist Network. Rehace solo el hijo "Generated".
//
//  - Un tramo "Bike_<carril>" por cada carril de trafico que no es conector:
//      * con ciclovia (hay marcas de bicicleta a la derecha del carril): por el centro
//        de la ciclovia, a CyclistNetwork.bikeLaneCenterFromCurbM de la solera (rayos);
//        los carriles cortos sin marcas (llegada/salida de interseccion) heredan
//        el desplazamiento del carril que los alimenta o al que siguen;
//      * sin ciclovia: pegado a la solera derecha (CyclistNetwork.curbClearanceM de la
//        solera, medida con rayos sobre la calzada; mediana por calle).
//  - Un tramo "Bike_<conector>" por cada conector de interseccion (rectos y giros,
//    cualquiera sea su peso para los autos), del final del tramo de llegada al
//    inicio del de salida.
//  - Una StopLine de ciclista en cada tramo de llegada a un acceso semaforizado.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

[CustomEditor(typeof(CyclistNetwork))]
public class CyclistNetworkEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        EditorGUILayout.Space();
        if (GUILayout.Button("Generar red del ciclista", GUILayout.Height(28)))
            Debug.Log(Generate((CyclistNetwork)target));
    }

    [MenuItem("SimBici/Generate Cyclist Network")]
    private static void GenerateMenu()
    {
        var net = Object.FindFirstObjectByType<CyclistNetwork>();
        if (net == null)
        {
            var go = new GameObject("CyclistNetwork");
            Undo.RegisterCreatedObjectUndo(go, "CyclistNetwork");
            net = go.AddComponent<CyclistNetwork>();
        }
        Debug.Log(Generate(net));
    }

    private class Profile
    {
        public bool bikeLane;
        public bool resolved;
        public readonly List<Vector2> points = new List<Vector2>();  // (s, desplazamiento), ordenados por s
        public float constant;

        public float At(float s)
        {
            if (points.Count == 0) return constant;
            if (s <= points[0].x) return points[0].y;
            for (int i = 1; i < points.Count; i++)
                if (s <= points[i].x)
                    return Mathf.Lerp(points[i - 1].y, points[i].y, Mathf.InverseLerp(points[i - 1].x, points[i].x, s));
            return points[points.Count - 1].y;
        }
    }

    public static string Generate(CyclistNetwork net)
    {
        var log = new StringBuilder();
        var lanes = Object.FindObjectsByType<TrafficLane>(FindObjectsSortMode.None)
                          .OrderBy(l => l.name, System.StringComparer.Ordinal).ToList();
        var road = lanes.Where(l => !IsConnector(l)).ToList();

        // Marcas de ciclovia pintadas en las calles
        var markings = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                             .Where(t => t.gameObject.activeInHierarchy && t.name.StartsWith(net.bikeMarkingPrefix))
                             .Select(t => t.position).ToList();

        // 1) Desplazamiento lateral de cada carril
        var profiles = new Dictionary<TrafficLane, Profile>();
        foreach (var l in road)
        {
            var prof = new Profile();
            float len = Length(l);
            foreach (var m in markings)
            {
                Project(l, m, out float s, out float lat);
                if (s < -2f || s > len + 2f) continue;
                if (lat < net.markingLateralRangeM.x || lat > net.markingLateralRangeM.y) continue;
                prof.points.Add(new Vector2(s, lat));
            }
            prof.points.Sort((a, b) => a.x.CompareTo(b.x));
            prof.bikeLane = prof.points.Count > 0;
            prof.resolved = prof.bikeLane;

            // Con ciclovia: las marcas solo indican que existe; el camino va por el centro de la
            // ciclovia, a bikeLaneCenterFromCurbM de la solera (medida con rayos, paralelo a la calzada)
            if (prof.bikeLane && len >= ShortLaneM)
            {
                var curbPts = CurbOffsetProfile(l, len, net.bikeLaneCenterFromCurbM);
                if (curbPts.Count >= 2) { prof.points.Clear(); prof.points.AddRange(curbPts); }
            }

            // Sin ciclovia (y carril largo): pegado a la solera derecha medida con rayos
            if (!prof.bikeLane && len >= ShortLaneM)
            {
                float? curb = MedianCurbDistance(l, len);
                prof.constant = curb.HasValue ? curb.Value - net.curbClearanceM : net.rightOfLaneOffsetM;
                prof.resolved = true;
                if (!curb.HasValue) log.AppendLine($"  AVISO: no se encontro la solera de {l.name}; se usa {net.rightOfLaneOffsetM} m del eje");
            }
            profiles[l] = prof;
        }

        // Carriles cortos (llegada/salida de interseccion): heredan del que los alimenta o del que sigue
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (var l in road)
            {
                var prof = profiles[l];
                if (prof.resolved) continue;
                var feeder = road.FirstOrDefault(f => f.NextLanes.Any(n => n.lane == l) && profiles[f].resolved);
                if (feeder != null) { Inherit(prof, profiles[feeder], profiles[feeder].At(Length(feeder))); continue; }
                var follow = l.NextLanes.Select(n => n.lane)
                                        .FirstOrDefault(n => n != null && !IsConnector(n) && profiles[n].resolved);
                if (follow != null) Inherit(prof, profiles[follow], profiles[follow].At(0f));
            }
        }
        foreach (var prof in profiles.Values.Where(p => !p.resolved)) prof.constant = net.rightOfLaneOffsetM;

        // 2) Rehacer el hijo Generated
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Generar red del ciclista");
        int undoGroup = Undo.GetCurrentGroup();

        Transform old = net.transform.Find(CyclistNetwork.GeneratedRootName);
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
        var root = new GameObject(CyclistNetwork.GeneratedRootName);
        Undo.RegisterCreatedObjectUndo(root, "Generated");
        root.transform.SetParent(net.transform, false);

        // 3) Tramos de los carriles
        var bike = new Dictionary<TrafficLane, CyclistPath>();
        var next = new Dictionary<CyclistPath, List<CyclistPath>>();
        foreach (var l in road)
        {
            var prof = profiles[l];
            float len = Length(l);
            int n = Mathf.Max(2, Mathf.CeilToInt(len / net.sampleSpacingM) + 1);
            var pts = new List<Vector3>(n);
            for (int i = 0; i < n; i++)
            {
                float s = len * i / (n - 1);
                Vector3 p = LanePoint(l, s, out Vector3 fwd);
                pts.Add(p + Vector3.Cross(Vector3.up, fwd) * prof.At(s));
            }
            var path = CreatePath(root.transform, "Bike_" + l.name, pts,
                                  prof.bikeLane ? CyclistPath.Kind.BikeLane : CyclistPath.Kind.LaneRightSide, l.name);
            bike[l] = path;
            next[path] = new List<CyclistPath>();
        }

        // 4) Enlaces directos y giros (todos los conectores, cualquiera sea su peso)
        int nTurns = 0;
        float maxGap = 0f;
        var bikeTurns = new List<(TrafficLane car, CyclistPath bike)>();
        string worstGap = "";
        foreach (var a in road)
        {
            CyclistPath pa = bike[a];
            foreach (var conn in a.NextLanes.Select(c => c.lane).Where(x => x != null))
            {
                if (!IsConnector(conn))
                {
                    if (!bike.TryGetValue(conn, out var pb)) continue;
                    next[pa].Add(pb);
                    float gap = (End(pa, 1f, out _) - End(pb, 0f, out _)).magnitude;
                    if (gap > maxGap) { maxGap = gap; worstGap = $"{pa.name} -> {pb.name}"; }
                    continue;
                }

                foreach (var m in conn.NextLanes.Select(c => c.lane).Where(x => x != null && bike.ContainsKey(x)))
                {
                    CyclistPath pm = bike[m];
                    Vector3 p0 = End(pa, 1f, out Vector3 d0);
                    Vector3 p1 = End(pm, 0f, out Vector3 d1);
                    bool straight = Vector3.Dot(d0, d1) > 0.7f;
                    var turn = CreateBezierPath(root.transform, "Bike_" + conn.name, p0, d0, p1, d1,
                                                straight ? 1f / 3f : 0.4f, conn.name);
                    next[pa].Add(turn);
                    next[turn] = new List<CyclistPath> { pm };
                    bikeTurns.Add((conn, turn));
                    nTurns++;
                }
            }
        }
        foreach (var kv in next) SetNext(kv.Key, kv.Value);

        // 4b) Quitar el escalonado heredado de los carriles y de las marcas (extremos y direccion fijos)
        foreach (var l in road) LaneSmoother.Smooth(bike[l].GetComponent<SplineContainer>(), out _, out _, out _, out _);

        // 5) StopLines de ciclista (copian la senal del carril de autos)
        int nLines = 0;
        foreach (var l in road)
        {
            if (!l.TryGetComponent(out StopLine vehicleLine) || vehicleLine.IsCyclistApproach || !vehicleLine.IsSignalized) continue;
            var sl = bike[l].gameObject.AddComponent<StopLine>();
            var so = new SerializedObject(sl);
            so.FindProperty("controller").objectReferenceValue = vehicleLine.Controller;
            so.FindProperty("approach").enumValueIndex = (int)vehicleLine.Approach;
            so.FindProperty("isCyclistApproach").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            nLines++;
            log.AppendLine($"  StopLine ciclista: {bike[l].name} ({vehicleLine.Controller.name}, acceso {IntersectionController.ApproachCode(vehicleLine.Approach)})");
        }

        // 6) Giros de autos que cruzan caminos de bici que siguen derecho (right hook, giro izquierda)
        int nZones = WriteBikeConflicts(lanes, bikeTurns, log);

        // 7) Inicio de la red
        var startLane = road.FirstOrDefault(l => l.name == net.startLaneName);
        var nso = new SerializedObject(net);
        nso.FindProperty("startPath").objectReferenceValue = startLane != null ? bike[startLane] : null;
        nso.ApplyModifiedProperties();
        if (startLane == null) log.AppendLine($"  AVISO: no existe el carril de inicio '{net.startLaneName}'");

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(net.gameObject.scene);

        int nBikeLane = profiles.Values.Count(p => p.bikeLane);
        log.Insert(0, $"[CyclistNetwork] {road.Count} tramos ({nBikeLane} por ciclovia, {road.Count - nBikeLane} junto a la solera), " +
                      $"{nTurns} giros/cruces, {nLines} lineas de detencion, {nZones} cruces bici-giro de autos. Mayor desajuste en enlaces directos: {maxGap:F2} m {worstGap}\n");
        foreach (var l in road)
        {
            var p = profiles[l];
            float mean = p.points.Count > 0 ? p.points.Average(v => v.y) : p.constant;
            log.AppendLine($"  {bike[l].name}: {(p.bikeLane ? "ciclovia" : "junto a la solera")} a {mean:F2} m del eje del carril ({p.points.Count} marcas)");
        }
        return log.ToString();
    }

    private const float ShortLaneM = 8f;

    private static void Inherit(Profile prof, Profile from, float offset)
    {
        prof.bikeLane = from.bikeLane;
        prof.resolved = true;
        prof.points.Clear();
        prof.constant = offset;
    }

    /// <summary>
    /// Distancia del eje del carril a la solera derecha: mediana de varias mediciones, sin los
    /// ultimos 6 m de cada extremo (ahi empiezan las esquinas de las intersecciones).
    /// </summary>
    private static float? MedianCurbDistance(TrafficLane l, float len)
    {
        var d = new List<float>();
        float from = len > 16f ? 6f : len * 0.5f, to = len > 16f ? len - 6f : len * 0.5f;
        for (float s = from; s <= to + 0.01f; s += 2f)
        {
            Vector3 p = LanePoint(l, s, out Vector3 fwd);
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (!OnRoad(p)) continue;
            for (float x = 0f; x < 12f; x += 0.05f)
                if (!OnRoad(p + right * x)) { d.Add(x); break; }
        }
        if (d.Count == 0) return null;
        d.Sort();
        return d[d.Count / 2];
    }

    private static bool OnRoad(Vector3 p)
    {
        return Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 10f) &&
               hit.collider.gameObject.layer == LayerMask.NameToLayer("Ground") &&
               !(hit.collider is TerrainCollider);
    }

    private static bool IsConnector(TrafficLane l) => l.name.Contains("Conn_");

    // ------------------------------------------------------------
    // Geometria (en editor TrafficLane/CyclistPath no tienen Awake: se usa el spline directo)
    // ------------------------------------------------------------
    private static float Length(TrafficLane l) => l.GetComponent<SplineContainer>().Spline.GetLength();

    private static Vector3 LanePoint(TrafficLane l, float s, out Vector3 forward)
    {
        var c = l.GetComponent<SplineContainer>();
        var sp = c.Spline;
        float t = sp.ConvertIndexUnit(Mathf.Clamp(s, 0f, sp.GetLength()), PathIndexUnit.Distance, PathIndexUnit.Normalized);
        forward = c.transform.TransformDirection((Vector3)sp.EvaluateTangent(t));
        forward.y = 0f;
        forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
        return c.transform.TransformPoint((Vector3)sp.EvaluatePosition(t));
    }

    private static void Project(TrafficLane l, Vector3 world, out float s, out float lateral)
    {
        var c = l.GetComponent<SplineContainer>();
        var sp = c.Spline;
        SplineUtility.GetNearestPoint(sp, (Unity.Mathematics.float3)c.transform.InverseTransformPoint(world),
                                      out Unity.Mathematics.float3 nearest, out float t, 8, 3);
        s = sp.ConvertIndexUnit(t, PathIndexUnit.Normalized, PathIndexUnit.Distance);
        // Fuera de los extremos: extrapolar s a lo largo de la tangente para descartar marcas lejanas
        Vector3 fwd = c.transform.TransformDirection((Vector3)sp.EvaluateTangent(t));
        fwd.y = 0f;
        fwd.Normalize();
        Vector3 delta = world - c.transform.TransformPoint((Vector3)nearest);
        delta.y = 0f;
        if (t <= 0f || t >= 1f) s += Vector3.Dot(delta, fwd);
        lateral = Vector3.Dot(delta, Vector3.Cross(Vector3.up, fwd));
    }

    private static Vector3 End(CyclistPath p, float t, out Vector3 dir)
    {
        var c = p.GetComponent<SplineContainer>();
        dir = c.transform.TransformDirection((Vector3)c.Spline.EvaluateTangent(t));
        dir.y = 0f;
        dir.Normalize();
        return c.transform.TransformPoint((Vector3)c.Spline.EvaluatePosition(t));
    }

    // ------------------------------------------------------------
    // Creacion de tramos
    // ------------------------------------------------------------
    private static CyclistPath CreatePath(Transform parent, string name, List<Vector3> worldPoints,
                                          CyclistPath.Kind kind, string source)
    {
        var go = NewPathObject(parent, name, worldPoints[0], out var container, out var path);
        var spline = new Spline();
        foreach (var p in worldPoints)
            spline.Add(new BezierKnot(go.transform.InverseTransformPoint(p)), TangentMode.AutoSmooth);
        container.Spline = spline;
        SetMeta(path, kind, source);
        return path;
    }

    private static CyclistPath CreateBezierPath(Transform parent, string name, Vector3 start, Vector3 startDir,
                                                Vector3 end, Vector3 endDir, float tangentFactor, string source)
    {
        var go = NewPathObject(parent, name, start, out var container, out var path);
        Vector3 p1 = go.transform.InverseTransformPoint(end);
        float h = Mathf.Max(p1.magnitude * tangentFactor, 0.01f);
        Vector3 t0 = startDir.normalized * h;
        Vector3 t1 = endDir.normalized * h;
        var spline = new Spline();
        spline.Add(new BezierKnot(Vector3.zero, -t0, t0, Quaternion.identity), TangentMode.Broken);
        spline.Add(new BezierKnot(p1, -t1, t1, Quaternion.identity), TangentMode.Broken);
        container.Spline = spline;
        SetMeta(path, CyclistPath.Kind.Connector, source);
        return path;
    }

    private static GameObject NewPathObject(Transform parent, string name, Vector3 position,
                                            out SplineContainer container, out CyclistPath path)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, Quaternion.identity);
        path = go.AddComponent<CyclistPath>();  // agrega SplineContainer (RequireComponent)
        container = go.GetComponent<SplineContainer>();
        return go;
    }

    private static void SetMeta(CyclistPath path, CyclistPath.Kind kind, string source)
    {
        var so = new SerializedObject(path);
        so.FindProperty("kind").enumValueIndex = (int)kind;
        so.FindProperty("sourceLane").stringValue = source;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetNext(CyclistPath path, List<CyclistPath> next)
    {
        var so = new SerializedObject(path);
        var list = so.FindProperty("next");
        list.arraySize = next.Count;
        for (int i = 0; i < next.Count; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = next[i];
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // ------------------------------------------------------------
    // Cruces entre giros de autos y caminos de bici que siguen derecho
    // ------------------------------------------------------------
    private static int WriteBikeConflicts(List<TrafficLane> lanes, List<(TrafficLane car, CyclistPath bike)> bikeTurns,
                                          StringBuilder log)
    {
        // Rehacer: quitar zonas anteriores
        foreach (var l in lanes)
            foreach (var z in l.GetComponents<BikeConflictZone>()) Undo.DestroyObjectImmediate(z);

        var straightBike = bikeTurns.Where(t => t.car.Movement == MovementKind.Straight).ToList();
        int count = 0;
        foreach (var car in lanes.Where(l => l.Movement == MovementKind.Right || l.Movement == MovementKind.Left))
        {
            var carPoly = Polyline(car.GetComponent<SplineContainer>());
            var entries = new List<BikeConflictZone.Entry>();
            foreach (var (_, bikePath) in straightBike)
            {
                var bikePoly = Polyline(bikePath.GetComponent<SplineContainer>());
                if (TrafficRules.FindConflict(carPoly, bikePoly, BikeZoneM, out float sCar, out float sBike))
                    entries.Add(new BikeConflictZone.Entry { bikePath = bikePath, sCar = sCar, sBike = sBike });
            }
            if (entries.Count == 0) continue;

            var zone = Undo.AddComponent<BikeConflictZone>(car.gameObject);
            var so = new SerializedObject(zone);
            var list = so.FindProperty("entries");
            list.arraySize = entries.Count;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = list.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("bikePath").objectReferenceValue = entries[i].bikePath;
                e.FindPropertyRelative("sCar").floatValue = entries[i].sCar;
                e.FindPropertyRelative("sBike").floatValue = entries[i].sBike;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            count += entries.Count;
            log.AppendLine($"  {car.name} cede al ciclista en: {string.Join(", ", entries.Select(e => e.bikePath.name))}");
        }
        return count;
    }

    // Zona de conflicto auto-bici: medio ancho del auto (0.925) + medio manubrio (~0.3) + margen
    private const float BikeZoneM = 1.5f;

    private static List<Vector2> Polyline(SplineContainer c)
    {
        float len = c.Spline.GetLength();
        int n = Mathf.Max(2, Mathf.CeilToInt(len / 0.25f) + 1);
        var pts = new List<Vector2>(n);
        for (int i = 0; i < n; i++)
        {
            float t = c.Spline.ConvertIndexUnit(len * i / (n - 1), PathIndexUnit.Distance, PathIndexUnit.Normalized);
            Vector3 p = c.transform.TransformPoint((Vector3)c.Spline.EvaluatePosition(t));
            pts.Add(new Vector2(p.x, p.z));
        }
        return pts;
    }

    /// <summary>Desplazamiento (s, offset) para quedar a 'fromCurbM' de la solera derecha, cada 2 m (mediana movil).</summary>
    private static List<Vector2> CurbOffsetProfile(TrafficLane l, float len, float fromCurbM)
    {
        var raw = new List<Vector2>();
        for (float s = 0f; s <= len + 0.01f; s += 2f)
        {
            Vector3 p = LanePoint(l, s, out Vector3 fwd);
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (!OnRoad(p)) continue;
            for (float x = 0f; x < 12f; x += 0.05f)
                if (!OnRoad(p + right * x)) { raw.Add(new Vector2(s, x - fromCurbM)); break; }
        }
        var result = new List<Vector2>();
        for (int i = 0; i < raw.Count; i++)
        {
            var win = new List<float>();
            for (int k = i - 2; k <= i + 2; k++) if (k >= 0 && k < raw.Count) win.Add(raw[k].y);
            win.Sort();
            result.Add(new Vector2(raw[i].x, win[win.Count / 2]));
        }
        return result;
    }
}
