// ============================================================
// IntersectionLaneBuilderEditor.cs
// Generacion de carriles de interseccion (ver IntersectionLaneBuilder).
// Boton "Generar carriles" en el Inspector y menu
// SimBici > Generate Intersection Lanes (seleccion, o todas si no hay seleccion).
// Todo queda en un solo paso de Undo.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

[CustomEditor(typeof(IntersectionLaneBuilder))]
public class IntersectionLaneBuilderEditor : Editor
{
    private const float StraightDot = 0.7f;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        EditorGUILayout.Space();
        if (GUILayout.Button("Generar carriles", GUILayout.Height(28)))
        {
            string report = Generate((IntersectionLaneBuilder)target);
            Debug.Log(report);
        }
    }

    [MenuItem("SimBici/Generate Intersection Lanes")]
    private static void GenerateMenu()
    {
        var selected = Selection.gameObjects.Select(g => g.GetComponentInParent<IntersectionLaneBuilder>())
                                            .Where(b => b != null).Distinct().ToList();
        if (selected.Count == 0)
            selected = Object.FindObjectsByType<IntersectionLaneBuilder>(FindObjectsSortMode.None)
                             .OrderBy(b => b.name, System.StringComparer.Ordinal).ToList();
        foreach (var b in selected) Debug.Log(Generate(b));
    }

    private class GenConn
    {
        public TrafficLane lane, entry;
        public IntersectionLaneBuilder.Arm arm;
        public MovementKind kind;
        public List<Vector2> poly;
    }

    private class GenLane
    {
        public TrafficLane lane;
        public IntersectionLaneBuilder.Arm arm;
        public Vector3 start, end;     // mundo
        public Vector3 heading;        // direccion de circulacion en el extremo que toca la interseccion
        public int index;
    }

    /// <summary>Genera (o regenera) los carriles del builder. Devuelve un reporte legible.</summary>
    public static string Generate(IntersectionLaneBuilder b)
    {
        var log = new StringBuilder();
        string error = Validate(b);
        if (error != null) return $"[IntersectionLaneBuilder] {b.name}: ERROR {error}. No se genero nada.";

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Generar carriles de interseccion");
        int undoGroup = Undo.GetCurrentGroup();

        // 1) Borrar lo generado antes (solo el hijo Generated)
        Transform old = b.transform.Find(IntersectionLaneBuilder.GeneratedRootName);
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

        var root = new GameObject(IntersectionLaneBuilder.GeneratedRootName);
        Undo.RegisterCreatedObjectUndo(root, "Generated");
        root.transform.SetParent(b.transform, false);

        Vector3 center = b.transform.position;
        var ins = new List<GenLane>();
        var outs = new List<GenLane>();

        // 2) Carriles de llegada (In) y salida (Out) por brazo
        foreach (var arm in b.arms)
        {
            string code = IntersectionController.ApproachCode(arm.approach);
            Vector3 dir = b.ArmDirection(arm.approach);
            Vector3 hIn = -dir;
            Vector3 rightIn = Vector3.Cross(Vector3.up, hIn);
            float roadW = (arm.inLanes + arm.outLanes) * b.laneWidthM;

            for (int i = 0; i < arm.inLanes; i++)
            {
                float u = roadW * 0.5f - (i + 0.5f) * b.laneWidthM;  // carril 0 = el de la solera
                float y = center.y;
                Vector3 start;
                bool fed = i == 0 && arm.feedFrom != null;
                if (fed)
                {
                    start = EndPoint(arm.feedFrom, 1f);
                    u = Vector3.Dot(start - center, rightIn);
                    y = start.y;
                }
                else start = center + dir * (arm.stopLineDistanceM + arm.approachLengthM) + rightIn * u;

                Vector3 stop = center + dir * arm.stopLineDistanceM + rightIn * u;
                stop.y = y;

                float speed = fed ? GetSpeedLimit(arm.feedFrom) : b.speedLimitKmh;
                string name = $"{b.prefix}_{code}_In{(arm.inLanes > 1 ? i.ToString() : "")}";
                var lane = CreateLane(root.transform, name, start, hIn, stop, hIn, speed, b.laneWidthM);

                // Linea de detencion solo si el acceso tiene control (semaforo, PARE o CEDA)
                ApproachControl ctrl = b.EffectiveControl(arm);
                if (ctrl != ApproachControl.Priority)
                {
                    var sl = Undo.AddComponent<StopLine>(lane.gameObject);
                    var slo = new SerializedObject(sl);
                    slo.FindProperty("control").enumValueIndex = (int)ctrl;
                    slo.FindProperty("controller").objectReferenceValue = ctrl == ApproachControl.Signal ? b.controller : null;
                    slo.FindProperty("approach").enumValueIndex = (int)arm.approach;
                    slo.ApplyModifiedPropertiesWithoutUndo();
                }

                ins.Add(new GenLane { lane = lane, arm = arm, start = start, end = stop, heading = hIn, index = i });

                if (fed)
                {
                    SetNextLanes(arm.feedFrom, new List<(TrafficLane, float)> { (lane, 1f) });
                    log.AppendLine($"  {arm.feedFrom.name} -> {name}");
                }
            }

            if (b.paintStopLines && arm.inLanes > 0 && b.EffectiveControl(arm) != ApproachControl.Priority)
                PaintStopLine(b, arm, root.transform, ins.Where(g => g.arm == arm).ToList(), rightIn, log);

            for (int j = 0; j < arm.outLanes; j++)
            {
                float u = -roadW * 0.5f + (j + 0.5f) * b.laneWidthM;  // carril 0 = el de la solera (a la derecha de quien sale)
                float y = center.y;
                Vector3 end;
                bool cont = j == 0 && arm.continueTo != null;
                if (cont)
                {
                    end = EndPoint(arm.continueTo, 0f);
                    u = Vector3.Dot(end - center, rightIn);
                    y = end.y;
                }
                else end = center + dir * (arm.stopLineDistanceM + arm.departureLengthM) + rightIn * u;

                Vector3 start = center + dir * arm.stopLineDistanceM + rightIn * u;
                start.y = y;

                float speed = cont ? GetSpeedLimit(arm.continueTo) : b.speedLimitKmh;
                string name = $"{b.prefix}_{code}_Out{(arm.outLanes > 1 ? j.ToString() : "")}";
                var lane = CreateLane(root.transform, name, start, dir, end, dir, speed, b.laneWidthM);
                if (cont) SetNextLanes(lane, new List<(TrafficLane, float)> { (arm.continueTo, 1f) });

                outs.Add(new GenLane { lane = lane, arm = arm, start = start, end = end, heading = dir, index = j });
            }
        }

        // 3) Conectores: de cada In a cada Out de OTRO brazo (sin vueltas en U)
        int nStraight = 0, nRight = 0, nLeft = 0;
        var conns = new List<GenConn>();
        foreach (var gi in ins)
        {
            var next = new List<(TrafficLane, float)>();
            foreach (var go in outs)
            {
                if (go.arm == gi.arm) continue;
                float dot = Vector3.Dot(gi.heading, go.heading);
                if (dot < -StraightDot) continue;

                MovementKind kind; float weight;
                var w = gi.arm.customWeights ? (gi.arm.straightWeight, gi.arm.rightWeight, gi.arm.leftWeight)
                                             : (b.straightWeight, b.rightWeight, b.leftWeight);
                if (dot > StraightDot) { kind = MovementKind.Straight; weight = w.Item1; nStraight++; }
                else if (Vector3.Cross(gi.heading, go.heading).y > 0f) { kind = MovementKind.Right; weight = w.Item2; nRight++; }
                else { kind = MovementKind.Left; weight = w.Item3; nLeft++; }

                string from = IntersectionController.ApproachCode(gi.arm.approach);
                string to = IntersectionController.ApproachCode(go.arm.approach);
                string suffix = (gi.arm.inLanes > 1 ? gi.index.ToString() : "") + (go.arm.outLanes > 1 ? "_" + go.index : "");
                string name = $"{b.prefix}_{from}_Conn_{kind}_{to}{suffix}";

                var conn = CreateLane(root.transform, name, gi.end, gi.heading, go.start, go.heading, b.speedLimitKmh,
                                      b.laneWidthM, kind == MovementKind.Straight ? 1f / 3f : 0.4f);
                SetNextLanes(conn, new List<(TrafficLane, float)> { (go.lane, 1f) });
                next.Add((conn, weight));

                // Limite en curva: v = sqrt(a_lat * R_min), acotado entre el minimo y el limite de la calle
                float rMin = MinRadius(conn);
                float vCurve = TrafficRules.CurveSpeedLimitMps(rMin, b.lateralAccelMps2) * 3.6f;
                float limit = Mathf.Clamp(vCurve, b.minConnectorSpeedKmh, b.speedLimitKmh);
                var so = new SerializedObject(conn);
                so.FindProperty("speedLimitKmh").floatValue = limit;
                so.FindProperty("movement").enumValueIndex = (int)kind;
                so.ApplyModifiedPropertiesWithoutUndo();

                conns.Add(new GenConn { lane = conn, arm = gi.arm, entry = gi.lane, kind = kind, poly = Polyline(conn) });
                if (kind != MovementKind.Straight)
                    log.AppendLine($"  {name}: R_min={rMin:F1} m -> {limit:F0} km/h");
            }
            SetNextLanes(gi.lane, next);
        }

        // 4) Puntos de conflicto entre conectores (cruces y convergencias) y quien cede
        int nConflicts = WriteConflicts(b, conns, log);

        // 5) Semaforos (las StopLine del ciclista las crea SimBici > Generate Cyclist Network)
        if (b.placeSignals && b.controller != null) PlaceSignals(b, log);

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(b.gameObject.scene);

        log.Insert(0, $"[IntersectionLaneBuilder] {b.name}: {ins.Count} In, {outs.Count} Out, " +
                      $"conectores {nStraight} rectos / {nRight} derecha / {nLeft} izquierda " +
                      $"(pesos {b.straightWeight}/{b.rightWeight}/{b.leftWeight}), {nConflicts} puntos de conflicto\n");
        return log.ToString();
    }

    // ------------------------------------------------------------
    // Validacion: los carriles existentes deben terminar/empezar mas afuera que la linea
    // ------------------------------------------------------------
    private static string Validate(IntersectionLaneBuilder b)
    {
        if (string.IsNullOrWhiteSpace(b.prefix)) return "falta el prefijo";
        if (b.arms.Count < 2) return "se necesitan al menos 2 brazos";
        if (b.arms.Select(a => a.approach).Distinct().Count() != b.arms.Count) return "hay brazos repetidos";

        Vector3 center = b.transform.position;
        foreach (var arm in b.arms)
        {
            Vector3 dir = b.ArmDirection(arm.approach);
            if (arm.feedFrom != null)
            {
                float along = Vector3.Dot(EndPoint(arm.feedFrom, 1f) - center, dir);
                if (along <= arm.stopLineDistanceM + 0.05f)
                    return $"{arm.feedFrom.name} termina a {along:F2} m del centro, dentro de la linea de detencion ({arm.stopLineDistanceM} m)";
            }
            if (arm.continueTo != null)
            {
                float along = Vector3.Dot(EndPoint(arm.continueTo, 0f) - center, dir);
                if (along <= arm.stopLineDistanceM + 0.05f)
                    return $"{arm.continueTo.name} empieza a {along:F2} m del centro, dentro del borde de la interseccion ({arm.stopLineDistanceM} m)";
            }
        }
        return null;
    }

    // ------------------------------------------------------------
    // Carriles
    // ------------------------------------------------------------
    private static TrafficLane CreateLane(Transform parent, string name, Vector3 start, Vector3 startDir,
                                          Vector3 end, Vector3 endDir, float speedKmh, float widthM,
                                          float tangentFactor = 1f / 3f)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(start, Quaternion.identity);

        var lane = go.AddComponent<TrafficLane>();  // agrega SplineContainer (RequireComponent)
        var container = go.GetComponent<SplineContainer>();

        Vector3 p1 = go.transform.InverseTransformPoint(end);
        float h = Mathf.Max(p1.magnitude * tangentFactor, 0.01f);
        Vector3 t0 = go.transform.InverseTransformDirection(startDir.normalized) * h;
        Vector3 t1 = go.transform.InverseTransformDirection(endDir.normalized) * h;

        var spline = new Spline();
        spline.Add(new BezierKnot(Vector3.zero, -t0, t0, Quaternion.identity), TangentMode.Broken);
        spline.Add(new BezierKnot(p1, -t1, t1, Quaternion.identity), TangentMode.Broken);
        container.Spline = spline;

        var so = new SerializedObject(lane);
        so.FindProperty("speedLimitKmh").floatValue = speedKmh;
        so.FindProperty("laneWidthM").floatValue = widthM;
        so.ApplyModifiedPropertiesWithoutUndo();
        return lane;
    }

    private static void SetNextLanes(TrafficLane lane, List<(TrafficLane lane, float weight)> next)
    {
        var so = new SerializedObject(lane);
        var list = so.FindProperty("nextLanes");
        list.arraySize = next.Count;
        for (int i = 0; i < next.Count; i++)
        {
            var e = list.GetArrayElementAtIndex(i);
            e.FindPropertyRelative("lane").objectReferenceValue = next[i].lane;
            e.FindPropertyRelative("weight").floatValue = next[i].weight;
        }
        so.ApplyModifiedProperties();  // con Undo: puede tocar carriles existentes (feedFrom)
    }

    private static float GetSpeedLimit(TrafficLane lane) =>
        new SerializedObject(lane).FindProperty("speedLimitKmh").floatValue;

    private static Vector3 EndPoint(TrafficLane lane, float t)
    {
        var c = lane.GetComponent<SplineContainer>();
        return c.transform.TransformPoint((Vector3)SplineUtility.EvaluatePosition(c.Spline, t));
    }

    // ------------------------------------------------------------
    // Linea de detencion pintada: franja blanca a lo ancho de los carriles de
    // llegada (en un brazo de sentido unico, de solera a solera), justo aguas
    // arriba de la linea logica (StopLine).
    // ------------------------------------------------------------
    private const string PaintMaterialPath = "Assets/AssetsLab/Pedestrians/Materials/Paso_Pintura.mat";

    private static void PaintStopLine(IntersectionLaneBuilder b, IntersectionLaneBuilder.Arm arm, Transform parent,
                                      List<GenLane> armIns, Vector3 rightIn, StringBuilder log)
    {
        Vector3 center = b.transform.position;
        Vector3 dir = b.ArmDirection(arm.approach);
        float uMin = armIns.Min(g => Vector3.Dot(g.end - center, rightIn)) - b.laneWidthM * 0.5f;
        float uMax = arm.curbDistanceM;
        if (arm.outLanes == 0 && arm.leftCurbDistanceM > 0f) uMin = -arm.leftCurbDistanceM;
        if (uMax - uMin < 0.5f) return;

        string code = IntersectionController.ApproachCode(arm.approach);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(go, "Linea de detencion");
        go.name = $"{b.prefix}_{code}_LineaDetencion";
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, true);
        float y = armIns.Average(g => g.end.y) + 0.02f;
        Vector3 p = center + dir * (arm.stopLineDistanceM + b.stopLinePaintWidthM * 0.5f) + rightIn * ((uMin + uMax) * 0.5f);
        p.y = y;
        go.transform.SetPositionAndRotation(p, Quaternion.LookRotation(dir, Vector3.up));
        go.transform.localScale = new Vector3(uMax - uMin, 0.01f, b.stopLinePaintWidthM);
        var r = go.GetComponent<MeshRenderer>();
        var mat = AssetDatabase.LoadAssetAtPath<Material>(PaintMaterialPath);
        if (mat != null) r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        log.AppendLine($"  {go.name}: {uMax - uMin:F1} m a {arm.stopLineDistanceM:F1} m del centro");
    }

    // ------------------------------------------------------------
    // Semaforos: un poste en la vereda derecha justo antes de la linea y otro
    // al otro lado de la interseccion, pasado el paso peatonal del brazo de
    // enfrente y sobre su vereda (no en la curva de la esquina), mirando a quien llega.
    // ------------------------------------------------------------
    private static readonly string[] GroupProps = { "lightsNorth", "lightsSouth", "lightsEast", "lightsWest" };

    private static void PlaceSignals(IntersectionLaneBuilder b, StringBuilder log)
    {
        var so = new SerializedObject(b.controller);
        var withArrivals = b.arms.Where(a => a.inLanes > 0).Select(a => a.approach).ToList();

        // Accesos activos del controlador = brazos con llegada
        var active = so.FindProperty("activeApproaches");
        active.arraySize = withArrivals.Count;
        for (int i = 0; i < withArrivals.Count; i++) active.GetArrayElementAtIndex(i).enumValueIndex = (int)withArrivals[i];
        so.ApplyModifiedProperties();

        Vector3 center = b.transform.position;
        foreach (Approach a in System.Enum.GetValues(typeof(Approach)))
        {
            var prop = so.FindProperty(GroupProps[(int)a]);
            var lights = ReadLights(prop);
            var arm = b.arms.FirstOrDefault(x => x.approach == a && x.inLanes > 0);

            if (arm == null)
            {
                // Brazo sin llegada (o inexistente): sus luces no se usan
                foreach (var l in lights) SetActive(PostHolder(l), false);
                if (lights.Count > 0) log.AppendLine($"  Luces del acceso {IntersectionController.ApproachCode(a)} desactivadas (brazo sin llegada)");
                continue;
            }

            if (lights.Count == 0)
            {
                lights = CloneGroup(b.controller, so, prop, a);
                log.AppendLine($"  Grupo TL_{IntersectionController.ApproachCode(a)} creado ({lights.Count} luces) con el modelo de otro grupo");
            }

            Vector3 dir = b.ArmDirection(a);
            Vector3 rightIn = Vector3.Cross(Vector3.up, -dir);
            float crossHalf = b.arms.Where(x => Mathf.Abs(Vector3.Dot(b.ArmDirection(x.approach), dir)) < 0.3f)
                                    .Select(x => x.curbDistanceM).DefaultIfEmpty(arm.stopLineDistanceM).Max();
            float side = arm.curbDistanceM + 0.6f;
            Vector3 farSpot = center - dir * (crossHalf + 1.5f) + rightIn * side;
            var opp = b.arms.FirstOrDefault(x => Vector3.Dot(b.ArmDirection(x.approach), dir) < -0.7f);
            if (opp != null)
            {
                // Brazo de enfrente: pasado su paso peatonal (o su linea), en su vereda (a la izquierda de quien llega por el)
                float dFar = b.EffectiveControl(opp) == ApproachControl.Signal
                    ? opp.stopLineDistanceM - opp.crosswalkSetbackM + 0.5f
                    : opp.stopLineDistanceM + 0.5f;
                float farSide = (opp.leftCurbDistanceM > 0f ? opp.leftCurbDistanceM : opp.curbDistanceM) + 0.6f;
                farSpot = center - dir * dFar + rightIn * farSide;
            }
            Vector3[] spots =
            {
                center + dir * (arm.stopLineDistanceM + 1f) + rightIn * side,  // antes de la linea
                farSpot,                                                       // al otro lado del cruce
            };

            for (int k = 0; k < lights.Count; k++)
            {
                Transform holder = PostHolder(lights[k]);
                Undo.RecordObject(holder, "Ubicar semaforo");
                Vector3 p = spots[Mathf.Min(k, spots.Length - 1)];
                p.y = center.y;
                holder.position = p;
                holder.rotation = Quaternion.LookRotation(dir, Vector3.up);  // lente (+Z local) hacia quien llega
                SetActive(holder, true);
            }
            log.AppendLine($"  Acceso {IntersectionController.ApproachCode(a)}: {lights.Count} postes ubicados");
        }
    }

    private static List<ResearchTrafficLight> ReadLights(SerializedProperty prop)
    {
        var list = new List<ResearchTrafficLight>();
        for (int i = 0; i < prop.arraySize; i++)
            if (prop.GetArrayElementAtIndex(i).objectReferenceValue is ResearchTrafficLight l && l != null) list.Add(l);
        return list.OrderBy(l => l.name, System.StringComparer.Ordinal).ToList();
    }

    /// <summary>Estructura del prefab: TL_x (grupo) / TL_k (poste escalado) / tl_post_01 / tl_x_k (luz).</summary>
    private static Transform PostHolder(ResearchTrafficLight l) => l.transform.parent.parent;

    private static void SetActive(Transform t, bool value)
    {
        if (t.gameObject.activeSelf == value) return;
        Undo.RecordObject(t.gameObject, "Activar semaforo");
        t.gameObject.SetActive(value);
    }

    private static List<ResearchTrafficLight> CloneGroup(IntersectionController controller, SerializedObject so,
                                                         SerializedProperty prop, Approach a)
    {
        // Plantilla: el grupo de cualquier otro acceso que tenga luces
        Transform template = null;
        foreach (var p in GroupProps)
        {
            var l = ReadLights(so.FindProperty(p)).FirstOrDefault();
            if (l != null) { template = PostHolder(l).parent; break; }
        }
        if (template == null) return new List<ResearchTrafficLight>();

        string code = IntersectionController.ApproachCode(a);
        var clone = Object.Instantiate(template.gameObject, controller.transform);
        Undo.RegisterCreatedObjectUndo(clone, "Grupo de semaforos");
        clone.name = "TL_" + code;
        clone.SetActive(true);

        var lights = clone.GetComponentsInChildren<ResearchTrafficLight>(true)
                          .OrderBy(l => l.name, System.StringComparer.Ordinal).ToList();
        for (int i = 0; i < lights.Count; i++)
        {
            lights[i].name = $"tl_{code.ToLowerInvariant()}_{i + 1}";
            SetActive(PostHolder(lights[i]), true);
        }

        prop.arraySize = lights.Count;
        for (int i = 0; i < lights.Count; i++) prop.GetArrayElementAtIndex(i).objectReferenceValue = lights[i];
        so.ApplyModifiedProperties();
        return lights;
    }

    // ------------------------------------------------------------
    // Conflictos
    // ------------------------------------------------------------
    // Zona de conflicto: eje a eje a menos de esto = los vehiculos se tocarian. Desde 2026-11-04 incluye el bus:
    // 1.275 m (medio ancho del bus) + 0.925 m (auto) + 0.6 m de margen. Antes 2.3 m (dos autos).
    private const float ConflictNearM = 2.8f;

    private static int WriteConflicts(IntersectionLaneBuilder b, List<GenConn> conns, StringBuilder log)
    {
        var lists = conns.ToDictionary(c => c, c => new List<LaneConflict>());
        int count = 0;
        for (int i = 0; i < conns.Count; i++)
        {
            for (int j = i + 1; j < conns.Count; j++)
            {
                GenConn a = conns[i], c = conns[j];
                if (a.entry == c.entry) continue;  // salen del mismo carril: divergen, no se cruzan
                if (!TrafficRules.FindConflict(a.poly, c.poly, ConflictNearM, out float sA, out float sAEnd,
                                               out float sC, out float sCEnd)) continue;

                ApproachControl ca = b.EffectiveControl(a.arm), cc = b.EffectiveControl(c.arm);
                bool separated = ca == ApproachControl.Signal && cc == ApproachControl.Signal &&
                                 SignalCycle.AxisOf(a.arm.approach) != SignalCycle.AxisOf(c.arm.approach);

                ConflictMode modeA, modeC;
                if (separated) { modeA = modeC = ConflictMode.OccupancyOnly; }
                else if (TrafficRules.Yields(a.kind, ca, c.kind, cc)) { modeA = ConflictMode.Yield; modeC = ConflictMode.OccupancyOnly; }
                else if (TrafficRules.Yields(c.kind, cc, a.kind, ca)) { modeC = ConflictMode.Yield; modeA = ConflictMode.OccupancyOnly; }
                else { modeA = modeC = ConflictMode.OccupancyOnly; }

                lists[a].Add(new LaneConflict { other = c.lane, sSelf = sA, sSelfEnd = sAEnd, sOther = sC, sOtherEnd = sCEnd, mode = modeA });
                lists[c].Add(new LaneConflict { other = a.lane, sSelf = sC, sSelfEnd = sCEnd, sOther = sA, sOtherEnd = sAEnd, mode = modeC });
                count++;

                string who = separated ? "separados por el semaforo"
                           : modeA == ConflictMode.Yield ? $"{a.lane.name} cede"
                           : modeC == ConflictMode.Yield ? $"{c.lane.name} cede" : "igual prioridad";
                log.AppendLine($"  conflicto {a.lane.name} x {c.lane.name}: {who}");
            }
        }

        foreach (var kv in lists)
        {
            var so = new SerializedObject(kv.Key.lane);
            var list = so.FindProperty("conflicts");
            list.arraySize = kv.Value.Count;
            for (int i = 0; i < kv.Value.Count; i++)
            {
                var e = list.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("other").objectReferenceValue = kv.Value[i].other;
                e.FindPropertyRelative("sSelf").floatValue = kv.Value[i].sSelf;
                e.FindPropertyRelative("sSelfEnd").floatValue = kv.Value[i].sSelfEnd;
                e.FindPropertyRelative("sOther").floatValue = kv.Value[i].sOther;
                e.FindPropertyRelative("sOtherEnd").floatValue = kv.Value[i].sOtherEnd;
                e.FindPropertyRelative("mode").enumValueIndex = (int)kv.Value[i].mode;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        return count;
    }

    /// <summary>Polilinea en planta (XZ) del carril, cada ~0.25 m.</summary>
    private static List<Vector2> Polyline(TrafficLane lane)
    {
        var c = lane.GetComponent<SplineContainer>();
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

    /// <summary>Radio minimo del spline (por curvatura muestreada). Infinito si es recto.</summary>
    private static float MinRadius(TrafficLane lane)
    {
        var sp = lane.GetComponent<SplineContainer>().Spline;
        float kMax = 0f;
        for (int i = 0; i <= 50; i++)
            kMax = Mathf.Max(kMax, Mathf.Abs(SplineUtility.EvaluateCurvature(sp, i / 50f)));
        return kMax > 1e-4f ? 1f / kMax : float.PositiveInfinity;
    }
}
