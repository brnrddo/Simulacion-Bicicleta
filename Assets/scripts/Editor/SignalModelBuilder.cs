// ============================================================
// SignalModelBuilder.cs
// SimBici > Build Signal Models (2026-11-06): reemplaza los semaforos del pack
// "Traffic Lights System" (Asset Store, no redistribuible) por "Stoplight from
// Chile - Decayed" de Felipe Carvajal Brown (Sketchfab, CC BY 4.0).
//
// Se conservan los objetos de cada semaforo (TL_x / TL_k / tl_post_01 / tl_x_k con
// ResearchTrafficLight y sus lentes), asi siguen funcionando el controlador, los
// halos y IntersectionLaneBuilder. Solo cambian mallas, materiales y la posicion
// del cabezal (el brazo del modelo chileno mide ~2.4 m en vez de ~3.6 m):
//   - poste (tl_post_01): poste + base, con el brazo hacia donde estaba el cabezal;
//   - cabezal (tl_x_k): mirando a su +Z local, como antes (los halos usan +Z);
//   - cuerpo y lentes: caja, marco blanco y las tres luces del modelo.
// Antes desempaqueta las instancias de Interseccion.prefab (contienen un prefab del
// pack): la escena deja de depender de ese prefab.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SignalModelBuilder
{
    private const string ModelPath = "Assets/ThirdParty/TrafficLights/StoplightChile/stoplight_from_chile_decayed.glb";
    private const string MeshDir = "Assets/AssetsLab/TrafficLights/Generated";
    private const string MatDir = "Assets/AssetsLab/TrafficLights";
    private const string IntersectionPrefab = "Assets/AssetsLab/Interseccion.prefab";
    private const string OldPackDir = "Assets/Traffic Lights System";
    private const float ModelScale = 0.01f;   // el GLB viene en centimetros (poste de ~5 m)

    [MenuItem("SimBici/Build Signal Models")]
    private static void Menu() => Debug.Log(Build());

    private class Part
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<List<int>> sub = new List<List<int>>();
        public readonly List<Material> mats = new List<Material>();
    }

    public static string Build()
    {
        if (EditorApplication.isPlaying) return "[Signals] Salir de Play antes de generar.";
        var log = new StringBuilder("[Signals]\n");

        // 0) Desempaquetar los prefabs que dependen del pack
        int unpacked = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var go = t.gameObject;
            if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go)) continue;
            string src = AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(go));
            if (src != IntersectionPrefab && !src.StartsWith(OldPackDir)) continue;
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            unpacked++;
        }
        log.AppendLine($"  Instancias de prefab desempaquetadas: {unpacked}");

        // 1) Piezas del modelo, en marcos canonicos (metros):
        //    poste: origen en el pie, brazo hacia +X; cabezal: origen en su centro, luces hacia +Z
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null) return log + "  ERROR: no se encontro " + ModelPath;
        var tmp = (GameObject)Object.Instantiate(model);
        tmp.hideFlags = HideFlags.HideAndDontSave;
        tmp.transform.position = Vector3.zero;   // conserva la rotacion propia del GLB
        Part pole = new Part(), body = new Part();
        var lens = new Dictionary<string, Part> { ["LuzRoja"] = new Part(), ["LuzAmarilla"] = new Part(), ["LuzVerde"] = new Part() };
        Vector3 headOffset;
        float modelHand;   // lado del brazo respecto de las luces en el modelo (+1 / -1)
        try
        {
            var mfs = tmp.GetComponentsInChildren<MeshFilter>().ToDictionary(f => f.GetComponent<MeshRenderer>().sharedMaterial.name);
            Bounds B(string mat) => mfs[mat].GetComponent<MeshRenderer>().bounds;
            Bounds baseB = B("Base"), boxB = B("Caja"), frameB = B("CajaBlanco");
            Vector3 foot = new Vector3(baseB.center.x, baseB.min.y, baseB.center.z);
            Vector3 headC = boxB.center;
            Vector3 arm = Flat(headC - foot).normalized;
            Vector3 facing = Flat(B("LuzRoja").center - frameB.center).normalized;
            float poleH = (B("Poste").max.y - baseB.min.y) * ModelScale;
            log.AppendLine($"  Modelo: poste de {poleH:F2} m; brazo de {Flat(headC - foot).magnitude * ModelScale:F2} m; cabezal a {(headC.y - foot.y) * ModelScale:F2} m; angulo luces-brazo {Vector3.Angle(arm, facing):F0} grados");

            Quaternion poleFrame = Quaternion.Inverse(Quaternion.LookRotation(Vector3.Cross(arm, Vector3.up), Vector3.up)) ;
            // LookRotation(z = arm x up) deja el brazo en +X local
            Quaternion headFrame = Quaternion.Inverse(Quaternion.LookRotation(facing, Vector3.up));
            Vector3 ToPole(Vector3 w) => poleFrame * ((w - foot) * ModelScale);
            Vector3 ToHead(Vector3 w) => headFrame * ((w - headC) * ModelScale);
            headOffset = ToPole(headC);
            modelHand = Mathf.Sign(Vector3.Dot(Vector3.up, Vector3.Cross(arm, facing)));
            if (Vector3.Dot(poleFrame * arm, Vector3.right) < 0.99f) log.AppendLine("  AVISO: el brazo no quedo en +X");

            Add(pole, mfs["Poste"], ToPole, poleFrame);
            Add(pole, mfs["Base"], ToPole, poleFrame);
            Add(body, mfs["Caja"], ToHead, headFrame);
            Add(body, mfs["CajaBlanco"], ToHead, headFrame);
            foreach (var k in lens.Keys.ToList()) Add(lens[k], mfs[k], ToHead, headFrame);
        }
        finally { Object.DestroyImmediate(tmp); }

        // Poste, base y caja negros, como los semaforos de Santiago (el marco claro del cabezal se mantiene)
        var black = BlackMat();
        for (int i = 0; i < pole.mats.Count; i++) pole.mats[i] = black;
        int box = body.mats.FindIndex(m => m != null && m.name == "Caja");
        if (box >= 0) body.mats[box] = black;

        RingBikeLaneEditor.EnsureFolder(MeshDir);
        Mesh poleMesh = Save("TL_Chile_Poste", pole), poleMeshMirror = Save("TL_Chile_Poste_Espejo", MirrorZ(pole)), bodyMesh = Save("TL_Chile_Cabezal", body);
        var lensMesh = lens.ToDictionary(k => k.Key, k => Save("TL_Chile_" + k.Key, k.Value));

        // 2) Materiales de las lentes (encendida / apagada)
        var mats = new Dictionary<string, Material>
        {
            ["redOn"] = LensMat("TL_Lente_Roja_On", new Color(1f, 0.12f, 0.06f), true),
            ["redOff"] = LensMat("TL_Lente_Roja_Off", new Color(0.22f, 0.04f, 0.03f), false),
            ["yellowOn"] = LensMat("TL_Lente_Amarilla_On", new Color(1f, 0.68f, 0.08f), true),
            ["yellowOff"] = LensMat("TL_Lente_Amarilla_Off", new Color(0.24f, 0.17f, 0.04f), false),
            ["greenOn"] = LensMat("TL_Lente_Verde_On", new Color(0.12f, 1f, 0.55f), true),
            ["greenOff"] = LensMat("TL_Lente_Verde_Off", new Color(0.03f, 0.18f, 0.11f), false),
        };

        // 3) Cada semaforo
        int n = 0, nMirror = 0;
        foreach (var rtl in Object.FindObjectsByType<ResearchTrafficLight>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                                  .OrderBy(r => r.name, System.StringComparer.Ordinal))
        {
            var head = rtl.transform;
            var post = head.parent;
            var postMf = post != null ? post.GetComponent<MeshFilter>() : null;
            if (postMf == null) { log.AppendLine($"  AVISO: {head.name} sin poste con malla; se omite"); continue; }

            Vector3 facing = Flat(head.forward).normalized;
            Vector3 arm = Flat(head.position - post.position);
            if (arm.sqrMagnitude < 1e-4f) arm = Vector3.Cross(Vector3.up, facing);
            arm.Normalize();

            Undo.RecordObject(post, "Poste");
            Undo.RecordObject(head, "Cabezal");
            Vector3 ls = post.parent != null ? post.parent.lossyScale : Vector3.one;
            // Si las luces miran hacia el otro lado del brazo que en el modelo, se refleja el poste (eje Z local):
            // asi el brazo queda siempre detras del cabezal, como en el modelo
            float hand = Mathf.Sign(Vector3.Dot(Vector3.up, Vector3.Cross(arm, facing)));
            bool mirror = hand != modelHand;
            post.localScale = new Vector3(1f / ls.x, 1f / ls.y, 1f / ls.z);   // sin la escala 0.8 del grupo
            post.rotation = Quaternion.LookRotation(Vector3.Cross(arm, Vector3.up), Vector3.up);   // brazo en +X
            postMf.sharedMesh = mirror ? poleMeshMirror : poleMesh;
            post.GetComponent<MeshRenderer>().sharedMaterials = pole.mats.ToArray();

            head.position = post.TransformPoint(mirror ? Vector3.Scale(headOffset, new Vector3(1f, 1f, -1f)) : headOffset);
            if (mirror) nMirror++;
            head.rotation = Quaternion.LookRotation(facing, Vector3.up);
            head.localScale = Vector3.one;

            var so = new SerializedObject(rtl);
            var red = so.FindProperty("redRenderer").objectReferenceValue as Renderer;
            var yellow = so.FindProperty("yellowRenderer").objectReferenceValue as Renderer;
            var green = so.FindProperty("greenRenderer").objectReferenceValue as Renderer;
            foreach (var r in head.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.name.EndsWith("_halo")) continue;
                Undo.RecordObject(r.transform, "Pieza");
                r.transform.localPosition = Vector3.zero; r.transform.localRotation = Quaternion.identity; r.transform.localScale = Vector3.one;
                var mf = r.GetComponent<MeshFilter>();
                if (r == red) { mf.sharedMesh = lensMesh["LuzRoja"]; r.sharedMaterial = mats["redOff"]; }
                else if (r == yellow) { mf.sharedMesh = lensMesh["LuzAmarilla"]; r.sharedMaterial = mats["yellowOff"]; }
                else if (r == green) { mf.sharedMesh = lensMesh["LuzVerde"]; r.sharedMaterial = mats["greenOff"]; }
                else { mf.sharedMesh = bodyMesh; r.sharedMaterials = body.mats.ToArray(); }
            }
            // Contenedor intermedio del pack (tl_simple_01 sin malla): sin desplazamientos
            foreach (Transform c in head) { c.localPosition = Vector3.zero; c.localRotation = Quaternion.identity; c.localScale = Vector3.one; }

            so.FindProperty("matOn").objectReferenceValue = null;
            so.FindProperty("matOff").objectReferenceValue = null;
            foreach (var kv in mats) so.FindProperty(kv.Key).objectReferenceValue = kv.Value;
            so.ApplyModifiedProperties();
            n++;
        }
        log.AppendLine($"  Semaforos reemplazados: {n} ({nMirror} con el poste reflejado para que el brazo quede detras de las luces)");

        // 4) Lo que todavia use el pack en la escena
        var left = new HashSet<string>();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && AssetDatabase.GetAssetPath(mf.sharedMesh).StartsWith(OldPackDir)) left.Add(r.name + " (malla)");
            foreach (var m in r.sharedMaterials)
                if (m != null && AssetDatabase.GetAssetPath(m).StartsWith(OldPackDir)) left.Add(r.name + " (" + m.name + ")");
        }
        log.AppendLine(left.Count == 0 ? "  Ninguna malla ni material del pack queda en la escena" : "  AVISO: aun usan el pack: " + string.Join(", ", left.Take(20)));
        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        return log.ToString();
    }

    private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    /// <summary>Copia reflejada en el eje Z local (vertices, normales y orden de los triangulos).</summary>
    private static Part MirrorZ(Part p)
    {
        var m = new Part();
        foreach (var v in p.v) m.v.Add(new Vector3(v.x, v.y, -v.z));
        foreach (var n in p.n) m.n.Add(new Vector3(n.x, n.y, -n.z));
        m.uv.AddRange(p.uv);
        foreach (var sub in p.sub)
        {
            var l = new List<int>(sub.Count);
            for (int t = 0; t < sub.Count; t += 3) { l.Add(sub[t]); l.Add(sub[t + 2]); l.Add(sub[t + 1]); }
            m.sub.Add(l);
        }
        m.mats.AddRange(p.mats);
        return m;
    }

    private static void Add(Part part, MeshFilter mf, System.Func<Vector3, Vector3> map, Quaternion rot)
    {
        var mesh = mf.sharedMesh; var M = mf.transform.localToWorldMatrix;
        int baseIdx = part.v.Count;
        var verts = mesh.vertices; var norms = mesh.normals; var uvs = mesh.uv;
        for (int i = 0; i < verts.Length; i++)
        {
            part.v.Add(map(M.MultiplyPoint3x4(verts[i])));
            part.n.Add(norms.Length > 0 ? (rot * M.MultiplyVector(norms[i])).normalized : Vector3.up);
            part.uv.Add(uvs.Length > 0 ? uvs[i] : Vector2.zero);
        }
        var tris = mesh.triangles;
        bool flip = M.determinant < 0f;
        var list = new List<int>(tris.Length);
        for (int t = 0; t < tris.Length; t += 3)
        {
            list.Add(baseIdx + tris[t]);
            list.Add(baseIdx + (flip ? tris[t + 2] : tris[t + 1]));
            list.Add(baseIdx + (flip ? tris[t + 1] : tris[t + 2]));
        }
        part.sub.Add(list);
        part.mats.Add(mf.GetComponent<MeshRenderer>().sharedMaterial);
    }

    private static Mesh Save(string name, Part p)
    {
        string path = $"{MeshDir}/{name}.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool isNew = mesh == null;
        if (isNew) mesh = new Mesh { name = name };
        mesh.Clear();
        mesh.SetVertices(p.v);
        mesh.SetNormals(p.n);
        mesh.SetUVs(0, p.uv);
        mesh.subMeshCount = p.sub.Count;
        for (int i = 0; i < p.sub.Count; i++) mesh.SetTriangles(p.sub[i], i);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        if (isNew) AssetDatabase.CreateAsset(mesh, path);
        else EditorUtility.SetDirty(mesh);
        AssetDatabase.SaveAssets();
        return mesh;
    }

    private static Material BlackMat()
    {
        string path = $"{MatDir}/TL_Negro.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(Shader.Find("Standard")) { name = "TL_Negro" }; AssetDatabase.CreateAsset(m, path); }
        m.color = new Color(0.035f, 0.035f, 0.04f);
        m.SetFloat("_Metallic", 0.35f);
        m.SetFloat("_Glossiness", 0.4f);
        m.enableInstancing = true;
        EditorUtility.SetDirty(m);
        return m;
    }

    private static Material LensMat(string name, Color c, bool on)
    {
        string path = $"{MatDir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(Shader.Find("Standard")) { name = name }; AssetDatabase.CreateAsset(m, path); }
        m.color = c;
        m.SetFloat("_Glossiness", 0.85f);
        m.enableInstancing = true;
        if (on)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * 2.2f);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
        else
        {
            m.DisableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", Color.black);
        }
        EditorUtility.SetDirty(m);
        return m;
    }
}
