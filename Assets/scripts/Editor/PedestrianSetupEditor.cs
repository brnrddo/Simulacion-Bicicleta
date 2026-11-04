// ============================================================
// PedestrianSetupEditor.cs
// SimBici > Build Pedestrian Setup: arma todo lo necesario para los peatones
// (T6) en la escena abierta. Se puede volver a correr: rehace lo que genera.
//   1. Areas del NavMesh: "Vereda" (3) y "Cruce" (4). La calzada no es caminable.
//   2. Pasos peatonales: uno por brazo en las intersecciones con semaforo
//      (siguen la fase peatonal; posicion y ancho del builder, entre la esquina
//      y la linea de detencion). Sin pasos cebra por ahora (soportados).
//      Los extremos se miden con rayos (soleras). Pintura y semaforos peatonales.
//   3. NavMesh: veredas de la ciudad (NavMeshModifier "Vereda") + superficie de
//      los cruces ("Cruce"); solo objetos marcados.
//   4. Avatares Rocketbox: materiales (pelo con recorte), controladores de
//      animacion (Idle/Walk por parametro Speed) y un prefab por avatar.
//   5. PedestrianManager con zonas y largo de vereda, y dos eventos de ejemplo
//      (PedestrianEvent) desactivados.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class PedestrianSetupEditor
{
    private const string RootName = "Pedestrians";
    private const string MatDir = "Assets/AssetsLab/Pedestrians/Materials";
    private const string PrefabDir = "Assets/AssetsLab/Pedestrians";
    private const string RbDir = "Assets/ThirdParty/Rocketbox";
    private const int AreaVereda = 3, AreaCruce = 4;

    [MenuItem("SimBici/Build Pedestrian Setup")]
    private static void Menu() => Debug.Log(Build());

    public static string Build()
    {
        var log = new StringBuilder("[PedestrianSetup]\n");
        EnsureFolder(PrefabDir); EnsureFolder(MatDir);

        SetAreaNames();
        var mats = CreateMaterials();

        // Raiz (se rehace); se conserva la densidad elegida para la condicion
        var old = GameObject.Find(RootName);
        float density = float.NaN;
        var oldMgr = old != null ? old.GetComponentInChildren<PedestrianManager>(true) : null;
        if (oldMgr != null) density = new SerializedObject(oldMgr).FindProperty("pedestriansPer100m").floatValue;
        if (old != null) Object.DestroyImmediate(old);
        var root = new GameObject(RootName);
        var cwRoot = new GameObject("Crosswalks"); cwRoot.transform.SetParent(root.transform, false);

        // --- Veredas: marcarlas para el NavMesh y medir su largo ---
        var sidewalks = GameObject.Find("City/Sidewalks");
        var areas = new List<Bounds>();
        float length = 0f;
        var seen = new HashSet<string>();
        foreach (Transform t in sidewalks.transform)
        {
            var r = t.GetComponent<Renderer>();
            if (r == null) continue;
            var mod = t.GetComponent<NavMeshModifier>() ?? t.gameObject.AddComponent<NavMeshModifier>();
            mod.overrideArea = true; mod.area = AreaVereda;
            string key = $"{Mathf.Round(r.bounds.center.x)}_{Mathf.Round(r.bounds.center.z)}_{Mathf.Round(r.bounds.size.x)}_{Mathf.Round(r.bounds.size.z)}";
            if (!seen.Add(key)) continue;  // hay veredas duplicadas en la misma posicion
            areas.Add(r.bounds);
            length += Mathf.Max(r.bounds.size.x, r.bounds.size.z);
        }
        log.AppendLine($"  veredas: {areas.Count} unicas, {length:F0} m");

        // --- NavMesh, primera pasada: solo veredas (para medir donde empiezan las veredas en cada cruce) ---
        var navGo = new GameObject("NavMesh_Peatones"); navGo.transform.SetParent(root.transform, false);
        var surface = navGo.AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.MarkedWithModifier;
        surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.RenderMeshes;
        surface.defaultArea = 1;  // no caminable salvo lo marcado
        surface.BuildNavMesh();

        // --- Pasos peatonales ---
        int nCw = 0;
        foreach (var b in Object.FindObjectsByType<IntersectionLaneBuilder>(FindObjectsSortMode.None).OrderBy(x => x.prefix))
        {
            if (b.controller == null) continue;
            foreach (Approach arm in System.Enum.GetValues(typeof(Approach)))
            {
                var a = b.arms.FirstOrDefault(x => x.approach == arm);
                if (a == null) continue;
                // Entre la esquina y la linea de detencion (SimBici > Fit Intersections To Curbs los deja fuera de la curva)
                float dc = b.CrosswalkCenterDistance(a);
                float width = a.crosswalkWidthM;
                Vector3 c = b.transform.position + b.ArmDirection(arm) * dc;
                Vector3 across = Vector3.Cross(Vector3.up, b.ArmDirection(arm));
                if (MakeCrosswalk(cwRoot.transform, $"Paso_{b.prefix}_{IntersectionController.ApproachCode(arm)}", c, across,
                                  width, Crosswalk.Kind.Signalized, b.controller, arm, mats, log)) nCw++;
            }
        }
        // (El paso cebra de la recta norte se quito el 2026-10-13: quedaba casi junto al semaforo de I_NO.
        //  Las reglas de paso cebra siguen en Crosswalk / PedestrianManager para usarlas en otro lugar.)

        // --- NavMesh definitivo: veredas + cruces ---
        surface.RemoveData();
        surface.BuildNavMesh();
        string navPath = "Assets/EscenasLab/NavMesh_Peatones_test.asset";
        AssetDatabase.DeleteAsset(navPath);
        AssetDatabase.CreateAsset(surface.navMeshData, navPath);
        surface.navMeshData = AssetDatabase.LoadAssetAtPath<UnityEngine.AI.NavMeshData>(navPath);
        surface.AddData();
        var tri = UnityEngine.AI.NavMesh.CalculateTriangulation();
        log.AppendLine($"  NavMesh: {tri.indices.Length / 3} triangulos, {tri.vertices.Length} vertices -> {navPath}");

        // --- Avatares ---
        var prefabs = BuildAvatarPrefabs(log);

        // --- Gestor y eventos de ejemplo ---
        var mgrGo = new GameObject("PedestrianManager"); mgrGo.transform.SetParent(root.transform, false);
        var mgr = mgrGo.AddComponent<PedestrianManager>();
        var so = new SerializedObject(mgr);
        so.FindProperty("sidewalkLengthM").floatValue = length;
        if (!float.IsNaN(density)) so.FindProperty("pedestriansPer100m").floatValue = density;
        var ar = so.FindProperty("sidewalkAreas"); ar.arraySize = areas.Count;
        for (int i = 0; i < areas.Count; i++) ar.GetArrayElementAtIndex(i).boundsValue = areas[i];
        var pf = so.FindProperty("prefabs"); pf.arraySize = prefabs.Count;
        for (int i = 0; i < prefabs.Count; i++) pf.GetArrayElementAtIndex(i).objectReferenceValue = prefabs[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        var evRoot = new GameObject("Eventos_Peatones (activar por condicion)"); evRoot.transform.SetParent(root.transform, false);
        MakeEvent(evRoot.transform, "Evento_PasadaCercana_Norte", PedestrianEvent.EventType.NearPass, "Bike_Lane_Main_03", 20f);
        MakeEvent(evRoot.transform, "Evento_CruceFueraDePaso_Sur", PedestrianEvent.EventType.Jaywalk, "Bike_Lane_Main_05", 20f);
        evRoot.SetActive(false);

        EditorSceneManager.MarkSceneDirty(root.scene);
        log.Insert(0, $"{nCw} pasos peatonales, {prefabs.Count} avatares, objetivo {mgr.TargetCount} peatones. ");
        return log.ToString();
    }

    // ------------------------------------------------------------
    // Pasos peatonales
    // ------------------------------------------------------------
    private static bool MakeCrosswalk(Transform parent, string name, Vector3 center, Vector3 across, float width,
                                      Crosswalk.Kind kind, IntersectionController controller, Approach arm,
                                      Dictionary<string, Material> mats, StringBuilder log)
    {
        across.y = 0f; across.Normalize();
        // Soleras: desde el centro hacia ambos lados mientras haya calzada
        // Extremos: donde empieza la vereda a cada lado (NavMesh de veredas). Las esquinas de la ciudad son
        // placas de asfalto con collider bajo veredas sin collider, asi que un rayo no distingue calzada de vereda.
        float sA = DistanceToSidewalk(center, -across), sB = DistanceToSidewalk(center, across);
        if (float.IsNaN(sA) || float.IsNaN(sB) || sA + sB < 3f) { log.AppendLine($"  {name}: no se encontraron las veredas, omitido"); return false; }
        Vector3 a = center - across * sA, b = center + across * sB;
        a.y = b.y = 0.02f;

        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.transform.position = (a + b) * 0.5f;
        var cw = go.AddComponent<Crosswalk>();
        cw.Configure(kind, controller, arm, a, b, width);

        Vector3 along = across;                           // direccion del cruce (A -> B)
        Vector3 traffic = Vector3.Cross(Vector3.up, along);  // direccion de los autos
        float len = sA + sB;

        // Superficie caminable (invisible), entra 0.8 m bajo cada vereda para conectar el NavMesh
        var surf = Quad(go.transform, "Superficie", (a + b) * 0.5f, along, traffic, len + 1.6f, width, mats["walkable"]);
        var mod = surf.AddComponent<NavMeshModifier>(); mod.overrideArea = true; mod.area = AreaCruce;

        // Pintura: franjas de 0.5 m cada 1.0 m, paralelas al transito
        var stripes = new GameObject("Pintura"); stripes.transform.SetParent(go.transform, false);
        for (float u = 0.5f; u < len - 0.25f; u += 1.0f)
            Quad(stripes.transform, "Franja", a + along * u + Vector3.up * 0.005f, along, traffic, 0.5f, width, mats["white"]);

        // Semaforos peatonales en ambos extremos, mirando al otro lado
        if (kind == Crosswalk.Kind.Signalized)
        {
            PedSignal(go.transform, cw, a - along * 0.9f + traffic * (width * 0.5f + 0.3f), along, mats);
            PedSignal(go.transform, cw, b + along * 0.9f - traffic * (width * 0.5f + 0.3f), -along, mats);
        }
        log.AppendLine($"  {name}: {kind}, largo {len:F1} m, ancho {width:F1} m");
        return true;
    }

    private static GameObject Quad(Transform parent, string name, Vector3 center, Vector3 uDir, Vector3 vDir,
                                   float uLen, float vLen, Material mat)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, true);
        go.transform.position = center;
        var mf = go.AddComponent<MeshFilter>();
        var mesh = new Mesh { name = name };
        Vector3 hu = go.transform.InverseTransformVector(uDir * uLen * 0.5f), hv = go.transform.InverseTransformVector(vDir * vLen * 0.5f);
        // centro en el medio del rectangulo, salvo las franjas que se ubican desde su borde izquierdo
        if (name == "Franja") { go.transform.position = center + uDir * uLen * 0.5f; }
        mesh.vertices = new[] { -hu - hv, hu - hv, -hu + hv, hu + hv };
        mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        mesh.RecalculateNormals();
        if (Mathf.Abs(mesh.normals[0].y) < 0.5f) mesh.triangles = new[] { 0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3 };  // vertical: dos caras
        else if (mesh.normals[0].y < 0f) mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };                           // horizontal: hacia arriba
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        mf.sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    private static void PedSignal(Transform parent, Crosswalk cw, Vector3 basePos, Vector3 facing, Dictionary<string, Material> mats)
    {
        var go = new GameObject("SemaforoPeatonal"); go.transform.SetParent(parent, true);
        basePos.y = 0.2f;
        go.transform.SetPositionAndRotation(basePos, Quaternion.LookRotation(facing, Vector3.up));
        go.layer = LayerMask.NameToLayer("StaticObstacle");

        var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder); pole.name = "Poste";
        pole.transform.SetParent(go.transform, false);
        pole.transform.localPosition = new Vector3(0f, 1.25f, 0f); pole.transform.localScale = new Vector3(0.1f, 1.25f, 0.1f);
        pole.GetComponent<Renderer>().sharedMaterial = mats["dark"]; pole.layer = go.layer;
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube); box.name = "Caja";
        Object.DestroyImmediate(box.GetComponent<Collider>());
        box.transform.SetParent(go.transform, false);
        box.transform.localPosition = new Vector3(0f, 2.45f, 0.05f); box.transform.localScale = new Vector3(0.32f, 0.62f, 0.2f);
        box.GetComponent<Renderer>().sharedMaterial = mats["dark"];

        Renderer Lens(string n, float y) => Quad(go.transform, n, go.transform.TransformPoint(new Vector3(0f, y, 0.16f)),
                                                 go.transform.right, go.transform.up, 0.22f, 0.22f, mats["off"]).GetComponent<Renderer>();
        Renderer Halo(string n, float y, Material m)
        {
            var r = Quad(go.transform, n, go.transform.TransformPoint(new Vector3(0f, y, 0.18f)),
                         go.transform.right, go.transform.up, 0.6f, 0.6f, m).GetComponent<Renderer>();
            r.enabled = false;
            return r;
        }
        var red = Lens("LenteRoja", 2.6f); var green = Lens("LenteVerde", 2.3f);
        var rh = Halo("HaloRojo", 2.6f, mats["haloRed"]); var gh = Halo("HaloVerde", 2.3f, mats["haloGreen"]);
        go.AddComponent<PedestrianSignalLight>().Configure(cw, red, green, rh, gh, mats["redOn"], mats["greenOn"], mats["off"]);
    }

    /// <summary>Distancia desde p, en la direccion dir, hasta el primer punto sobre el NavMesh de veredas (NaN si no hay en 15 m).</summary>
    private static float DistanceToSidewalk(Vector3 p, Vector3 dir)
    {
        for (float d = 0f; d < 15f; d += 0.1f)
        {
            Vector3 q = p + dir * d;
            if (UnityEngine.AI.NavMesh.SamplePosition(q + Vector3.up * 0.2f, out var hit, 0.4f, 1 << AreaVereda))
            {
                Vector3 dd = hit.position - q; dd.y = 0f;
                if (dd.magnitude < 0.2f) return d;
            }
        }
        return float.NaN;
    }

    private static bool OnRoad(Vector3 p) =>
        Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 10f) &&
        hit.collider.gameObject.layer == LayerMask.NameToLayer("Ground") && !(hit.collider is TerrainCollider);

    // ------------------------------------------------------------
    // Eventos de ejemplo
    // ------------------------------------------------------------
    private static void MakeEvent(Transform parent, string name, PedestrianEvent.EventType type, string pathName, float s)
    {
        var path = Object.FindObjectsByType<CyclistPath>(FindObjectsSortMode.None).FirstOrDefault(p => p.name == pathName);
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        var ev = go.AddComponent<PedestrianEvent>();
        var so = new SerializedObject(ev);
        so.FindProperty("type").enumValueIndex = (int)type;
        so.FindProperty("path").objectReferenceValue = path;
        so.FindProperty("sOnPath").floatValue = s;
        so.ApplyModifiedPropertiesWithoutUndo();
        if (path != null)
        {
            var sc = path.GetComponent<UnityEngine.Splines.SplineContainer>();
            float t = UnityEngine.Splines.SplineUtility.ConvertIndexUnit(sc.Spline, Mathf.Min(s, sc.Spline.GetLength()),
                          UnityEngine.Splines.PathIndexUnit.Distance, UnityEngine.Splines.PathIndexUnit.Normalized);
            go.transform.position = sc.transform.TransformPoint((Vector3)UnityEngine.Splines.SplineUtility.EvaluatePosition(sc.Spline, t));
        }
    }

    // ------------------------------------------------------------
    // Materiales, areas
    // ------------------------------------------------------------
    private static void SetAreaNames()
    {
        var settings = Unsupported.GetSerializedAssetInterfaceSingleton("NavMeshProjectSettings");
        var so = new SerializedObject(settings);
        var areas = so.FindProperty("areas");
        areas.GetArrayElementAtIndex(AreaVereda).FindPropertyRelative("name").stringValue = "Vereda";
        areas.GetArrayElementAtIndex(AreaVereda).FindPropertyRelative("cost").floatValue = 1f;
        areas.GetArrayElementAtIndex(AreaCruce).FindPropertyRelative("name").stringValue = "Cruce";
        areas.GetArrayElementAtIndex(AreaCruce).FindPropertyRelative("cost").floatValue = 1.5f;  // preferir veredas
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static Dictionary<string, Material> CreateMaterials()
    {
        var d = new Dictionary<string, Material>();
        Material Std(string n, Color c, Color? emission = null)
        {
            string p = $"{MatDir}/{n}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null) { m = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(m, p); }
            m.color = c;
            if (emission.HasValue) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", emission.Value); }
            EditorUtility.SetDirty(m);
            return m;
        }
        d["white"] = Std("Paso_Pintura", new Color(0.92f, 0.92f, 0.92f));
        d["dark"] = Std("SemaforoPeatonal_Cuerpo", new Color(0.12f, 0.12f, 0.12f));
        d["off"] = Std("SemaforoPeatonal_Apagado", new Color(0.08f, 0.08f, 0.08f));
        d["redOn"] = Std("SemaforoPeatonal_Rojo", new Color(1f, 0.15f, 0.1f), new Color(1.5f, 0.2f, 0.1f));
        d["greenOn"] = Std("SemaforoPeatonal_Verde", new Color(0.2f, 1f, 0.4f), new Color(0.25f, 1.5f, 0.5f));

        // Superficie caminable invisible (el NavMesh solo junta mallas con renderer)
        string wp = $"{MatDir}/Paso_SuperficieInvisible.mat";
        var w = AssetDatabase.LoadAssetAtPath<Material>(wp);
        if (w == null) { w = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(w, wp); }
        w.SetFloat("_Mode", 2f); w.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        w.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); w.SetInt("_ZWrite", 0);
        w.EnableKeyword("_ALPHABLEND_ON"); w.renderQueue = 3000; w.color = new Color(0f, 0f, 0f, 0f);
        EditorUtility.SetDirty(w);
        d["walkable"] = w;

        d["haloRed"] = AssetDatabase.LoadAssetAtPath<Material>("Assets/AssetsLab/TrafficLights/TL_Halo_Red.mat");
        d["haloGreen"] = AssetDatabase.LoadAssetAtPath<Material>("Assets/AssetsLab/TrafficLights/TL_Halo_Green.mat");
        AssetDatabase.SaveAssets();
        return d;
    }

    // ------------------------------------------------------------
    // Avatares Rocketbox -> prefabs de peatones
    // ------------------------------------------------------------
    private static List<PedestrianAgent> BuildAvatarPrefabs(StringBuilder log)
    {
        var result = new List<PedestrianAgent>();
        EnsureFolder($"{RbDir}/Materials"); EnsureFolder($"{RbDir}/Controllers");
        var controllers = new Dictionary<string, AnimatorController>();
        foreach (var g in new[] { "f", "m" })
            foreach (var v in new[] { "01", "02" })
                controllers[g + v] = Controller(g, v);

        var guids = AssetDatabase.FindAssets("t:GameObject", new[] { $"{RbDir}/Avatars" });
        int i = 0;
        foreach (var path in guids.Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith(".fbx")).OrderBy(p => p))
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            string avatar = model.name;
            string g = avatar.StartsWith("Female") ? "f" : "m";
            var ctrl = controllers[g + (i++ % 2 == 0 ? "01" : "02")];

            var root = new GameObject("Ped_" + avatar);
            root.layer = LayerMask.NameToLayer("Pedestrian");
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inst.transform.SetParent(root.transform, false);

            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>();
            var newMats = smr.sharedMaterials.Select(m => AvatarMaterial(avatar, m)).ToArray();
            smr.sharedMaterials = newMats;
            smr.updateWhenOffscreen = false;

            var anim = inst.GetComponent<Animator>() ?? inst.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            var cap = root.AddComponent<CapsuleCollider>();
            cap.center = new Vector3(0f, 0.9f, 0f); cap.height = 1.75f; cap.radius = 0.25f;
            root.AddComponent<Rigidbody>().isKinematic = true;
            var agent = root.AddComponent<PedestrianAgent>();
            SetNominalWalkSpeed(agent, ctrl);

            // Sin niveles de detalle en el modelo: se oculta cuando ocupa menos del 1.5 % de la altura de pantalla
            var lod = root.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(0.015f, new Renderer[] { smr }) });

            string pPath = $"{PrefabDir}/Ped_{avatar}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, pPath);
            Object.DestroyImmediate(root);
            result.Add(prefab.GetComponent<PedestrianAgent>());
        }
        log.AppendLine($"  {result.Count} prefabs de peatones en {PrefabDir}");
        return result;
    }

    private static Material AvatarMaterial(string avatar, Material src)
    {
        string part = src.name.Contains("opacity") ? "opacity" : src.name.Contains("head") ? "head" : "body";
        string p = $"{RbDir}/Materials/{avatar}_{part}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(p);
        if (m == null) { m = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(m, p); }
        m.mainTexture = src.mainTexture;
        m.SetFloat("_Glossiness", 0.15f);
        string texDir = $"{RbDir}/Avatars/{avatar}/Textures/";
        var normal = AssetDatabase.FindAssets($"{part}_normal t:Texture2D", new[] { texDir }).Select(AssetDatabase.GUIDToAssetPath)
                                  .Select(AssetDatabase.LoadAssetAtPath<Texture2D>).FirstOrDefault(t => !t.name.Contains("wrinkle"));
        if (normal != null) { m.SetTexture("_BumpMap", normal); m.EnableKeyword("_NORMALMAP"); }
        if (part == "opacity")
        {
            // Pelo, pestanas: recorte por alfa
            m.SetFloat("_Mode", 1f); m.SetFloat("_Cutoff", 0.5f);
            m.EnableKeyword("_ALPHATEST_ON"); m.renderQueue = 2450;
            m.SetOverrideTag("RenderType", "TransparentCutout");
        }
        EditorUtility.SetDirty(m);
        return m;
    }

    private static AnimatorController Controller(string g, string variant)
    {
        string p = $"{RbDir}/Controllers/Peaton_{g}_{variant}.controller";
        // Se reutiliza el asset (vaciado) en vez de borrarlo: borrar y recrear en la misma ruta dejaba
        // las referencias de los prefabs en memoria apuntando a nada hasta reimportar
        var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(p);
        if (c == null) c = AnimatorController.CreateAnimatorControllerAtPath(p);
        while (c.parameters.Length > 0) c.RemoveParameter(0);
        var sm = c.layers[0].stateMachine;
        foreach (var st in sm.states.ToArray()) sm.RemoveState(st.state);
        c.AddParameter("Speed", AnimatorControllerParameterType.Float);
        var walkClip = InPlaceWalkClip($"{g}_walk_neutral_{variant}", out _);
        var idleClip = Clip($"{g}_idle_breathe_01");
        var idle = sm.AddState("Idle"); idle.motion = idleClip;
        var walk = sm.AddState("Walk"); walk.motion = walkClip;
        sm.defaultState = idle;
        var t1 = idle.AddTransition(walk); t1.hasExitTime = false; t1.duration = 0.2f;
        t1.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        var t2 = walk.AddTransition(idle); t2.hasExitTime = false; t2.duration = 0.25f;
        t2.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
        EditorUtility.SetDirty(c);
        AssetDatabase.SaveAssetIfDirty(c);
        return c;
    }

    // Huesos que avanzan en los clips de caminar de Rocketbox (el resto de la pose va relativo a Bip01)
    private static readonly string[] DriftingNodes = { "Bip01", "MotionExtractionHelper" };

    /// <summary>
    /// Copia en el lugar de un clip de caminar de Rocketbox. En el original Bip01
    /// avanza 1.2-1.6 m por ciclo y vuelve de golpe al repetirse, y el peaton
    /// parecia retroceder "con lag". Aqui se le resta a la posicion horizontal
    /// (x, z) de los nodos que avanzan su deriva lineal (ultimo - primero)*t/T;
    /// queda el vaiven natural de la marcha. speedMps = avance por ciclo / T:
    /// a esa velocidad la animacion va a speed = 1 sin que patinen los pies.
    /// </summary>
    public static AnimationClip InPlaceWalkClip(string file, out float speedMps)
    {
        var src = Clip(file);
        string dir = $"{RbDir}/Animations/InPlace";
        EnsureFolder(dir);
        string p = $"{dir}/{file}_inplace.anim";
        var dst = AssetDatabase.LoadAssetAtPath<AnimationClip>(p);
        if (dst == null) { dst = new AnimationClip(); AssetDatabase.CreateAsset(dst, p); }
        dst.ClearCurves();
        dst.frameRate = src.frameRate;

        float T = src.length;
        speedMps = 0f;
        var bindings = AnimationUtility.GetCurveBindings(src);
        var curves = new AnimationCurve[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            var b = bindings[i];
            var c = AnimationUtility.GetEditorCurve(src, b);
            bool drifts = DriftingNodes.Contains(b.path) &&
                          (b.propertyName == "m_LocalPosition.x" || b.propertyName == "m_LocalPosition.z");
            if (drifts && c.length > 1 && T > 0f)
            {
                float t0 = c.keys[0].time, v0 = c.keys[0].value;
                float slope = (c.keys[c.length - 1].value - v0) / (c.keys[c.length - 1].time - t0);
                var keys = c.keys;
                for (int k = 0; k < keys.Length; k++)
                {
                    keys[k].value -= slope * (keys[k].time - t0);
                    keys[k].inTangent -= slope;
                    keys[k].outTangent -= slope;
                }
                c.keys = keys;
                if (b.path == "Bip01") speedMps = Mathf.Sqrt(speedMps * speedMps + slope * slope);
            }
            curves[i] = c;
        }
        AnimationUtility.SetEditorCurves(dst, bindings, curves);
        dst.EnsureQuaternionContinuity();
        var settings = AnimationUtility.GetAnimationClipSettings(src);
        settings.loopTime = true;
        AnimationUtility.SetAnimationClipSettings(dst, settings);
        EditorUtility.SetDirty(dst);
        return dst;
    }

    /// <summary>Fija en el peaton la velocidad propia de su clip de caminar.</summary>
    public static void SetNominalWalkSpeed(PedestrianAgent agent, RuntimeAnimatorController ctrl)
    {
        var walk = ctrl.animationClips.FirstOrDefault(c => c.name.EndsWith("_inplace"));
        if (walk == null) return;
        float speed = WalkClipSpeed(Clip(walk.name.Substring(0, walk.name.Length - "_inplace".Length)));
        var so = new SerializedObject(agent);
        so.FindProperty("animNominalSpeedMps").floatValue = speed;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>Avance horizontal de Bip01 por ciclo / duracion del clip original.</summary>
    private static float WalkClipSpeed(AnimationClip src)
    {
        float d2 = 0f;
        foreach (var b in AnimationUtility.GetCurveBindings(src))
        {
            if (b.path != "Bip01" || (b.propertyName != "m_LocalPosition.x" && b.propertyName != "m_LocalPosition.z")) continue;
            var c = AnimationUtility.GetEditorCurve(src, b);
            float d = c.keys[c.length - 1].value - c.keys[0].value;
            d2 += d * d;
        }
        return src.length > 0f ? Mathf.Sqrt(d2) / src.length : 0f;
    }

    private static AnimationClip Clip(string file) =>
        AssetDatabase.LoadAllAssetsAtPath($"{RbDir}/Animations/{file}.max.fbx").OfType<AnimationClip>()
                     .FirstOrDefault(c => !c.name.StartsWith("__preview"));

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }
}
