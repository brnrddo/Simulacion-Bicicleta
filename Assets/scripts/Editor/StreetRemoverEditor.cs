// ============================================================
// StreetRemoverEditor.cs
// SimBici > Remove Streets (E1 del plan de rutas, 2026-11-25). Deja el mapa
// del experimento C2 -> C3:
//   - Quita la calle de salida x = 60 (Los Leones) al sur del circuito: calzada,
//     esquinas de asfalto, veredas, arboles y faroles. La solera exterior de la
//     recta sur queda continua con un tramo de vereda nuevo; donde estaba la
//     calle queda el pasto del terreno.
//   - Quita el tramo de x = 4 (Ricardo Lyon) DENTRO del circuito: recorta la
//     calzada hasta el eje del anillo (su malla es la misma de la calle x = -60,
//     asi que se crea una copia recortada) y las 4 esquinas interiores. Se apagan las dos
//     veredas laterales y el tramo se rellena con tres edificios (Bernardo: sin pasto ni
//     calzada entre C2 y C3). Las soleras interiores de las dos rectas quedan continuas
//     con tramos de vereda nuevos.
//   - Red: borra Lane_Extra, Cruce_SE (con su regulador de salida) y
//     Lane_North_02; une los carriles del anillo que pasaban por Cruce_SE;
//     Cruce_S_Centro y Cruce_N_Centro quedan en T; apaga el PARE del acceso sur
//     de Cruce_N_Centro.
//   - Crea los nodos origen-destino C1..C4 (OdNode).
// La geometria no se borra: queda apagada con el sufijo " (E1 off)". La red
// si se borra (respaldo de la escena antes de correrla). Un solo paso de Undo.
// Despues, en este orden: Align Lanes To Curb, Fit Intersections To Curbs,
// Generate Cyclist Network, Analyze Lane Room, Build Ring Bike Lane,
// Decorate City (rehace los peatones), Place Parked Cars y Validate Network.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

public static class StreetRemoverEditor
{
    public const string OffTag = " (E1 off)";
    public const string NodesRootName = "Nodos_OD";
    private const string MeshDir = "Assets/AssetsLab/Generated";
    private const string CutMeshPath = MeshDir + "/E1_Calle_RicardoLyon.asset";

    // La calzada de x = 4 se corta en el eje de las rectas del anillo (z = +-49.76): como en la antigua T de
    // x = 60, la calle transversal cubre solo la mitad exterior del anillo, y la mitad interior queda con el
    // asfalto del anillo solo (Build Ring Bike Lane deja continua la linea de la berma).
    private const float CutZ = 49.76f;
    // Las veredas nuevas quedan 1 cm mas altas que las vecinas, como las esquinas originales,
    // para que no parpadeen donde se superponen
    private const float FillerLiftM = 0.01f;
    private const float ExtendStepM = 4f;

    [MenuItem("SimBici/Remove Streets")]
    private static void Menu() => Debug.Log(Run());

    public static string Run()
    {
        if (EditorApplication.isPlaying) return "[StreetRemover] Salir de Play antes de correrlo.";
        var log = new StringBuilder("[StreetRemover]\n");
        if (Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(t => t.name.EndsWith(OffTag)))
            return log + "  Ya se aplico (hay objetos con el sufijo" + OffTag + "). Para repetirlo, volver al respaldo de la escena.";

        var city = GameObject.Find("City");
        if (city == null) return log + "  ERROR: no hay City";

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Quitar calles (E1)");
        int group = Undo.GetCurrentGroup();

        // ---------------- Geometria ----------------
        var roads = city.transform.Find("Roads");
        var corners = city.transform.Find("Intersections");
        var sidewalks = city.transform.Find("Sidewalks");

        // Calle x = 60: tiene malla propia
        foreach (Transform t in roads)
            if (t.name.StartsWith("Calle Lateral B")) Off(t.gameObject, log);

        // Esquinas de asfalto: las de x = 60 y las cuatro interiores de x = 4
        foreach (var t in Children(corners))
        {
            var b = Bounds(t); if (!b.HasValue) continue;
            Vector3 c = b.Value.center;
            bool sideStreet60 = c.x > 50f && c.x < 70f && c.z < -49f;
            bool inner4 = c.x > -6f && c.x < 16f && Mathf.Abs(c.z) > 34f && Mathf.Abs(c.z) < 49f;
            if (sideStreet60 || inner4) Off(t.gameObject, log);
        }

        // Veredas: esquinas y laterales de x = 60; esquinas interiores de x = 4 (las laterales se conservan)
        foreach (var t in Children(sidewalks))
        {
            var b = Bounds(t); if (!b.HasValue) continue;
            Vector3 c = b.Value.center, s = b.Value.size;
            bool sideStreet60 = c.x > 48f && c.x < 72f && c.z < -55f;
            bool innerCorner4 = c.x > -9f && c.x < 16f && Mathf.Abs(c.z) > 35f && Mathf.Abs(c.z) < 46f && Mathf.Max(s.x, s.z) < 10f;
            if (sideStreet60 || innerCorner4) Off(t.gameObject, log);
        }

        // Arboles y faroles que estaban en las veredas de x = 60
        foreach (var path in new[] { "Trees", "Street Lights" })
            foreach (var t in Children(city.transform.Find(path)))
                if (t.position.x > 48f && t.position.x < 72f && t.position.z < -62f) Off(t.gameObject, log);

        HideCrossingMarkings(log);

        // PARE del acceso sur de Cruce_N_Centro (ese acceso deja de existir)
        var pare = GameObject.Find("Cruces/PARE_I_NC_S");
        if (pare != null) Off(pare, log);

        // Soleras continuas: tramos de vereda nuevos
        Fill(sidewalks, "Vereda_E1_RectaSur_x60",
             s => s.max.x > 49f && s.max.x < 53f && s.center.z > -62f && s.center.z < -55f && s.size.x > 20f,
             s => s.min.x > 67f && s.min.x < 71f && s.center.z > -63f && s.center.z < -55f && s.size.x > 20f, log);
        Fill(sidewalks, "Vereda_E1_RectaSur_x4",
             s => s.max.x > -6f && s.max.x < -4f && s.center.z > -45f && s.center.z < -37f && s.size.x > 20f,
             s => s.min.x > 11f && s.min.x < 15f && s.center.z > -45f && s.center.z < -37f && s.size.x > 20f, log);
        Fill(sidewalks, "Vereda_E1_RectaNorte_x4",
             s => s.max.x > -6f && s.max.x < -4f && s.center.z > 37f && s.center.z < 45f && s.size.x > 20f,
             s => s.min.x > 11f && s.min.x < 15f && s.center.z > 37f && s.center.z < 45f && s.size.x > 20f, log);

        FillInnerBlockWithBuildings(log);

        // Calzada de x = 4: copia recortada (la original la comparte con x = -60)
        Transform street4 = null;
        foreach (Transform t in roads)
            if (t.name.StartsWith("Calle Transversal A") && Mathf.Abs(t.position.x - 3.7f) < 1.5f) street4 = t;
        if (street4 == null) log.AppendLine("  AVISO: no se encontro la calzada de x = 4");
        else log.AppendLine(CutStreet(street4, street4.GetComponent<MeshFilter>().sharedMesh));

        // ---------------- Red ----------------
        var lanes = Object.FindObjectsByType<TrafficLane>(FindObjectsSortMode.None).ToDictionary(l => l.name, l => l);
        TrafficLane L(string n) => lanes.TryGetValue(n, out var l) ? l : null;

        var cruceSE = GameObject.Find("Cruces/Cruce_SE");
        if (cruceSE != null) { Undo.DestroyObjectImmediate(cruceSE); log.AppendLine("  borrado: Cruce_SE (con RingExitController)"); }
        foreach (var n in new[] { "Lane_Extra", "Lane_North_02" })
            if (L(n) != null) { Undo.DestroyObjectImmediate(L(n).gameObject); log.AppendLine("  borrado: " + n); }

        Join(L("Lane_Main_01"), L("Lane_Main_02"), log);
        Join(L("Lane_Main2_02"), L("Lane_Main2_04"), log);

        var builders = Object.FindObjectsByType<IntersectionLaneBuilder>(FindObjectsSortMode.None).ToDictionary(b => b.prefix, b => b);
        RemoveArm(builders.GetValueOrDefault("I_SC"), Approach.North, log);
        RemoveArm(builders.GetValueOrDefault("I_NC"), Approach.South, log);
        foreach (var p in new[] { "I_SC", "I_NC" })
            if (builders.TryGetValue(p, out var b)) log.AppendLine(IntersectionLaneBuilderEditor.Generate(b));

        // ---------------- Nodos origen-destino ----------------
        var old = GameObject.Find(NodesRootName);
        if (old != null) Undo.DestroyObjectImmediate(old);
        var root = new GameObject(NodesRootName);
        Undo.RegisterCreatedObjectUndo(root, NodesRootName);
        Node(root.transform, "C1", OdNode.NodeKind.Exit, L("Lane_South_03"), log);
        Node(root.transform, "C2", OdNode.NodeKind.Entry, L("Lane_North_01"), log);
        Node(root.transform, "C3", OdNode.NodeKind.Exit, L("Lane_North_03"), log);
        Node(root.transform, "C4", OdNode.NodeKind.Entry, L("Lane_South_01"), log);

        Undo.CollapseUndoOperations(group);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
        log.AppendLine("  Siguiente: Align Lanes To Curb, Fit Intersections To Curbs, Generate Cyclist Network, " +
                       "Analyze Lane Room, Build Ring Bike Lane, Decorate City, Place Parked Cars, Validate Network.");
        return log.ToString();
    }

    // Edificios que rellenan el antiguo tramo de x = 4 entre las dos rectas del anillo (sur, centro, norte).
    // Se copian bloques de la ciudad con casi la misma planta (22 x 25 m) y con color y altura distintos de
    // los vecinos: Block_14 (cafe, 7 m), Block_19 (beige, 17.5 m) y Block_15 (blanco, 17.5 m).
    private static readonly (string name, string source, float zMin, float zMax)[] InnerBlocks =
    {
        ("Block_E1_x4_S", "Block_14", -37.6f, -12.85f),
        ("Block_E1_x4_C", "Block_19", -12.75f, 12.8f),
        ("Block_E1_x4_N", "Block_15", 12.9f, 37.7f),
    };
    private const float InnerBlockWestX = -7.88f;   // fachada de Block_10, Block_34 (1) y Block_12
    private const float InnerBlockEastX = 14.98f;   // fachada de Block_11, Block_26 (1) y Block_13

    /// <summary>
    /// Rellena con edificios el antiguo tramo de x = 4 dentro del circuito (Bernardo, 2026-11-25: sin pasto ni
    /// calzada entre C2 y C3). Apaga las dos veredas laterales del tramo y agrega tres edificios copiados de la
    /// ciudad, escalados a la manzana. Las veredas de las rectas del anillo quedan delante de ellos.
    /// </summary>
    public static string FillInnerBlockWithBuildings(StringBuilder log)
    {
        var city = GameObject.Find("City");
        var buildings = city != null ? city.transform.Find("Buildings") : null;
        if (buildings == null) { log.AppendLine("  AVISO: no hay City/Buildings"); return log.ToString(); }
        if (buildings.Find(InnerBlocks[0].name) != null) { log.AppendLine("  Los edificios de x = 4 ya estaban"); return log.ToString(); }

        foreach (var t in Children(city.transform.Find("Sidewalks")))
        {
            var b = Bounds(t); if (!b.HasValue) continue;
            if (b.Value.center.x > -9f && b.Value.center.x < 16f && Mathf.Abs(b.Value.center.z) < 5f && b.Value.size.z > 50f)
                Off(t.gameObject, log);
        }

        foreach (var (name, sourceName, zMin, zMax) in InnerBlocks)
        {
            var source = buildings.Find(sourceName);
            if (source == null) { log.AppendLine($"  AVISO: no existe {sourceName}"); continue; }
            var mesh = source.GetComponent<MeshFilter>().sharedMesh.bounds;
            var go = Object.Instantiate(source.gameObject, buildings);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.name = name;
            go.transform.rotation = Quaternion.identity;
            Vector3 scale = source.localScale;
            scale.x = (InnerBlockEastX - InnerBlockWestX) / mesh.size.x;
            scale.z = (zMax - zMin) / mesh.size.z;
            go.transform.localScale = scale;
            go.transform.position = new Vector3(InnerBlockWestX - scale.x * mesh.min.x, source.position.y, zMin - scale.z * mesh.min.z);
            log.AppendLine($"  edificio nuevo {name} (copia de {sourceName}): x [{InnerBlockWestX:F2}, {InnerBlockEastX:F2}], " +
                           $"z [{zMin:F2}, {zMax:F2}], escala {scale.x:F2} x {scale.z:F2}");
        }
        HideInsideInnerBlock(log);
        return log.ToString();
    }

    /// <summary>
    /// Apaga los faroles y arboles que quedaron dentro de los edificios nuevos de x = 4. Un mismo objeto
    /// "Street Light(Clone)" de la ciudad tiene varios postes repartidos, asi que se revisa cada hijo.
    /// </summary>
    public static int HideInsideInnerBlock(StringBuilder log)
    {
        var city = GameObject.Find("City");
        if (city == null) return 0;
        bool Inside(Transform t)
        {
            var b = Bounds(t);
            if (!b.HasValue) return false;
            Vector3 c = b.Value.center;
            return c.x > InnerBlockWestX && c.x < InnerBlockEastX && c.z > InnerBlocks[0].zMin && c.z < InnerBlocks[InnerBlocks.Length - 1].zMax;
        }
        int n = 0;
        foreach (var group in new[] { "Street Lights", "Trees" })
            foreach (var top in Children(city.transform.Find(group)).ToList())
            {
                if (Inside(top)) { Off(top.gameObject, log); n++; continue; }
                foreach (var part in Children(top).ToList())
                    if (Inside(part)) { Off(part.gameObject, log); n++; }
            }
        return n;
    }

    /// <summary>
    /// Apaga las marcas transversales de la ciudad original que cruzaban el anillo en la antigua calle x = 60
    /// (dos lineas segmentadas en x = 67 y 72). Sin la calle no tienen sentido.
    /// </summary>
    public static int HideCrossingMarkings(StringBuilder log)
    {
        int n = 0;
        foreach (var lr in Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None))
        {
            var b = lr.bounds;
            bool transversal = b.size.z > b.size.x;
            if (!transversal || b.center.x < 62f || b.center.x > 78f || b.center.z < -57f || b.center.z > -42f) continue;
            Undo.RecordObject(lr.gameObject, "Apagar");
            lr.gameObject.SetActive(false);
            lr.gameObject.name += OffTag;
            n++;
        }
        log.AppendLine($"  apagadas: {n} marcas transversales de la antigua calle x = 60");
        return n;
    }

    // ------------------------------------------------------------
    private static IEnumerable<Transform> Children(Transform parent)
    {
        if (parent == null) yield break;
        foreach (Transform t in parent) if (t.gameObject.activeSelf) yield return t;
    }

    private static Bounds? Bounds(Transform t)
    {
        var rs = t.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return null;
        var b = rs[0].bounds;
        foreach (var r in rs) b.Encapsulate(r.bounds);
        return b;
    }

    private static void Off(GameObject go, StringBuilder log)
    {
        Undo.RecordObject(go, "Apagar");
        string path = go.transform.parent != null ? go.transform.parent.name + "/" + go.name : go.name;
        go.SetActive(false);
        go.name += OffTag;
        log.AppendLine("  apagado: " + path);
    }

    /// <summary>Tramo de vereda recta entre el final de 'left' y el inicio de 'right' (copia escalada de 'left').</summary>
    private static void Fill(Transform sidewalks, string name, System.Func<Bounds, bool> isLeft, System.Func<Bounds, bool> isRight, StringBuilder log)
    {
        Transform left = null, right = null;
        foreach (var t in Children(sidewalks))
        {
            var b = Bounds(t); if (!b.HasValue) continue;
            if (left == null && isLeft(b.Value)) left = t;
            else if (right == null && isRight(b.Value)) right = t;
        }
        if (left == null || right == null) { log.AppendLine($"  AVISO {name}: no se encontraron las veredas vecinas"); return; }
        if (Mathf.Abs(Mathf.DeltaAngle(left.eulerAngles.y, 0f)) > 0.5f) { log.AppendLine($"  AVISO {name}: la vereda de origen esta rotada"); return; }

        float a = Bounds(left).Value.max.x, b2 = Bounds(right).Value.min.x;
        var mesh = left.GetComponent<MeshFilter>().sharedMesh;
        float lmin = mesh.bounds.min.x, lmax = mesh.bounds.max.x;

        var go = Object.Instantiate(left.gameObject, sidewalks);
        Undo.RegisterCreatedObjectUndo(go, name);
        go.name = name;
        Vector3 scale = left.localScale;
        scale.x = (b2 - a) / (lmax - lmin);
        go.transform.localScale = scale;
        Vector3 pos = left.position;
        pos.x = a - scale.x * lmin;
        pos.y += FillerLiftM;
        go.transform.position = pos;
        log.AppendLine($"  vereda nueva {name}: x [{a:F2}, {b2:F2}] ({b2 - a:F1} m)");
    }

    /// <summary>
    /// Asigna a 'street' una copia de 'src' (la malla original de la calle transversal) sin el tramo |z| menor que
    /// CutZ. Se puede repetir: siempre parte de la malla original.
    /// </summary>
    public static string CutStreet(Transform street, Mesh src)
    {
        var mf = street.GetComponent<MeshFilter>();
        var mesh = Object.Instantiate(src);
        mesh.name = "E1_Calle_RicardoLyon";

        var v = mesh.vertices; var uv = mesh.uv; var tris = mesh.triangles;
        var w = v.Select(p => street.TransformPoint(p)).ToArray();
        bool Inside(int i) => Mathf.Abs(w[i].z) < CutZ - 1e-3f;

        var keep = new List<int>();
        var partner = new Dictionary<int, int>();
        int dropped = 0;
        for (int t = 0; t < tris.Length; t += 3)
        {
            int[] tri = { tris[t], tris[t + 1], tris[t + 2] };
            int inside = tri.Count(Inside);
            if (inside == 3) { dropped++; continue; }
            keep.AddRange(tri);
            if (inside == 0) continue;
            // Triangulo que cruza la solera: cada vertice interior se recorta contra el de su misma columna
            foreach (int i in tri.Where(Inside))
            {
                int j = tri.Where(k => !Inside(k)).OrderBy(k => Mathf.Abs(w[k].x - w[i].x)).First();
                partner[i] = j;
            }
        }
        foreach (var (i, j) in partner.Select(kv => (kv.Key, kv.Value)))
        {
            float target = Mathf.Sign(w[i].z) * CutZ;
            float k = (target - w[j].z) / (w[i].z - w[j].z);
            v[i] = street.InverseTransformPoint(Vector3.Lerp(w[j], w[i], k));
            if (uv.Length == v.Length) uv[i] = Vector2.Lerp(uv[j], uv[i], k);
        }
        mesh.vertices = v;
        if (uv.Length == v.Length) mesh.uv = uv;
        mesh.triangles = keep.ToArray();
        mesh.RecalculateBounds();

        RingBikeLaneEditor.EnsureFolder(MeshDir);
        if (AssetDatabase.LoadAssetAtPath<Mesh>(CutMeshPath) != null) AssetDatabase.DeleteAsset(CutMeshPath);
        AssetDatabase.CreateAsset(mesh, CutMeshPath);

        Undo.RecordObject(mf, "Recortar calzada");
        mf.sharedMesh = mesh;
        var mc = street.GetComponent<MeshCollider>();
        if (mc != null) { Undo.RecordObject(mc, "Recortar calzada"); mc.sharedMesh = mesh; }
        Undo.RecordObject(street.gameObject, "Recortar calzada");
        street.gameObject.name = "Calle Transversal A (E1 recortada)";
        return $"  calzada x = 4 recortada entre z = -{CutZ} y {CutZ}: {dropped} triangulos quitados, " +
               $"{partner.Count} vertices recortados ({CutMeshPath})";
    }

    /// <summary>Alarga 'from' en linea recta hasta el inicio de 'to' y lo conecta directo (sin interseccion).</summary>
    private static void Join(TrafficLane from, TrafficLane to, StringBuilder log)
    {
        if (from == null || to == null) { log.AppendLine("  AVISO: falta un carril para unir"); return; }
        var c = from.GetComponent<SplineContainer>();
        var target = to.GetComponent<SplineContainer>();
        Vector3 end = c.transform.TransformPoint((Vector3)c.Spline.EvaluatePosition(1f));
        Vector3 start = target.transform.TransformPoint((Vector3)target.Spline.EvaluatePosition(0f));

        Undo.RecordObject(c, "Unir carriles");
        float gap = Vector3.Distance(end, start);
        int steps = Mathf.Max(1, Mathf.CeilToInt(gap / ExtendStepM));
        for (int i = 1; i <= steps; i++)
        {
            Vector3 p = Vector3.Lerp(end, start, i / (float)steps);
            c.Spline.Add((Unity.Mathematics.float3)c.transform.InverseTransformPoint(p), TangentMode.AutoSmooth);
        }

        var so = new SerializedObject(from);
        var next = so.FindProperty("nextLanes");
        next.ClearArray();
        next.InsertArrayElementAtIndex(0);
        var e = next.GetArrayElementAtIndex(0);
        e.FindPropertyRelative("lane").objectReferenceValue = to;
        e.FindPropertyRelative("weight").floatValue = 1f;
        so.ApplyModifiedProperties();
        log.AppendLine($"  unido: {from.name} -> {to.name} (alargado {gap:F1} m)");
    }

    private static void RemoveArm(IntersectionLaneBuilder b, Approach approach, StringBuilder log)
    {
        if (b == null) { log.AppendLine("  AVISO: falta la interseccion para quitar el brazo " + approach); return; }
        var so = new SerializedObject(b);
        var arms = so.FindProperty("arms");
        for (int i = 0; i < arms.arraySize; i++)
        {
            if (arms.GetArrayElementAtIndex(i).FindPropertyRelative("approach").enumValueIndex != (int)approach) continue;
            arms.DeleteArrayElementAtIndex(i);
            so.ApplyModifiedProperties();
            log.AppendLine($"  {b.name}: brazo {approach} quitado (queda en T)");
            return;
        }
        log.AppendLine($"  AVISO {b.name}: no tenia brazo {approach}");
    }

    private static void Node(Transform root, string id, OdNode.NodeKind kind, TrafficLane lane, StringBuilder log)
    {
        if (lane == null) { log.AppendLine($"  AVISO: nodo {id} sin carril"); return; }
        var c = lane.GetComponent<SplineContainer>();
        float t = kind == OdNode.NodeKind.Entry ? 0f : 1f;
        var go = new GameObject(id);
        Undo.RegisterCreatedObjectUndo(go, id);
        go.transform.SetParent(root, false);
        go.transform.position = c.transform.TransformPoint((Vector3)c.Spline.EvaluatePosition(t));
        go.AddComponent<OdNode>().Setup(id, kind, lane);
        log.AppendLine($"  nodo {id} ({kind}): {lane.name}");
    }
}
