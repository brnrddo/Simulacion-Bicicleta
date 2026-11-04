// ============================================================
// VehiclePrefabBuilder.cs
// SimBici > Build Vehicle Prefabs: arma los prefabs de vehiculos del trafico
// a partir de los modelos de terceros (Assets/ThirdParty/Vehicles) y los
// asigna a todos los TrafficSpawner de la escena con sus pesos.
//
// Cada prefab (Assets/AssetsLab/Vehicles/Veh_<nombre>.prefab):
//   - raiz en la capa Vehicle con Rigidbody kinematico, BoxCollider ajustado a
//     los limites del modelo, TrafficVehicle (largo, ancho y distancia entre
//     ejes medidos del modelo) y LODGroup (se oculta bajo el 1 % de pantalla);
//   - hijo "Model" con el modelo girado y escalado para que el frente mire a +Z,
//     centrado y apoyado en el suelo (pivote al centro, a nivel del suelo);
//   - una sola malla por vehiculo (todas las piezas fusionadas) con el color de
//     cada pieza en los vertices y el sombreador SimBici/VehicleVertexColor:
//     un solo draw call por vehiculo y GPU instancing entre vehiculos del mismo
//     modelo. La carroceria de los autos (alfa del vertice = 1) se pinta al
//     aparecer (TrafficVehicle.SetBodyColor) con colores sorteados con semilla.
// Taxi: negro con techo amarillo (como los taxis de Santiago). Bus: rojo y
// blanco (Red Movilidad, sin logos), 12 x 2.55 x 3.2 m. [VERIFICAR] librea.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class VehiclePrefabBuilder
{
    private const string SrcDir = "Assets/ThirdParty/Vehicles";
    private const string OutDir = "Assets/AssetsLab/Vehicles";

    private class Spec
    {
        public string name, model, type, bodyMat;
        public float weight;
        public bool randomColor;
        public Vector3 targetSize;            // (ancho, alto, largo) si se escala; cero = escala original
        public Dictionary<string, Color> recolor = new Dictionary<string, Color>();
        public string splitMat; public float splitAboveY; public Color splitColor;   // taxi: techo
        public float wheelbase;               // 0 = medir de las ruedas
        public bool useBusProfile;
        public bool flipForward;              // el detector de frente se equivoca
        public HashSet<string> removeMats = new HashSet<string>();   // submallas que se quitan (decoracion de la librea original)
        public string removeMatRear; public float removeRearOfZ;     // esa submalla se quita solo en piezas detras de esta z (m)
    }

    private static List<Spec> Specs() => new List<Spec>
    {
        new Spec { name = "Sedan", model = "QuaterniusCars/NormalCar1.fbx", type = "sedan", bodyMat = "Blue", weight = 30, randomColor = true },
        new Spec { name = "Hatchback", model = "QuaterniusCars/NormalCar2.fbx", type = "hatchback", bodyMat = "LightBlue", weight = 26, randomColor = true },
        new Spec { name = "SUV", model = "QuaterniusCars/SUV.fbx", type = "suv", bodyMat = "White", weight = 20, randomColor = true },
        new Spec { name = "Coupe", model = "QuaterniusCars/SportsCar2.fbx", type = "coupe", bodyMat = "White", weight = 5, randomColor = true },
        new Spec { name = "Taxi", model = "QuaterniusCars/Taxi.fbx", type = "taxi", bodyMat = "Yellow", weight = 8,
                   recolor = { ["Yellow"] = new Color(0.06f, 0.06f, 0.07f) },
                   splitMat = "Yellow", splitAboveY = 0.98f, splitColor = new Color(0.95f, 0.78f, 0.10f) },
        new Spec { name = "BusRed", model = "MontrealBus/MontrealBus.obj", type = "bus", weight = 3, wheelbase = 6.0f,
                   targetSize = new Vector3(2.55f, 3.2f, 12.0f), useBusProfile = true,
                   // Carroceria roja; la flecha de la librea original se pinta del mismo rojo (sin logos); grises claros en blanco
                   // Carroceria roja con faldon blanco; se quita la flecha de la librea de Montreal (sin logos)
                   recolor = { ["mat21"] = new Color(0.78f, 0.06f, 0.09f), ["mat5"] = new Color(0.78f, 0.06f, 0.09f),
                               ["mat15"] = new Color(0.95f, 0.95f, 0.95f) },
                   removeMats = { "mat12" }, removeMatRear = "mat5", removeRearOfZ = 1.0f },
    };

    [MenuItem("SimBici/Build Vehicle Prefabs")]
    private static void Menu() => Debug.Log(BuildAll(assignSpawners: true));

    public static string BuildAll(bool assignSpawners)
    {
        var log = new StringBuilder("[VehiclePrefabBuilder]\n");
        EnsureFolder(OutDir); EnsureFolder(OutDir + "/Materials"); EnsureFolder(OutDir + "/Meshes");
        // Limpiar intermedios de versiones anteriores (solo quedan la malla fusionada y el material comun)
        foreach (var g in AssetDatabase.FindAssets("", new[] { OutDir + "/Materials", OutDir + "/Meshes" }))
        {
            string ap = AssetDatabase.GUIDToAssetPath(g);
            if (!ap.EndsWith("_combined.asset") && !ap.EndsWith("Vehicle_VertexColor.mat")) AssetDatabase.DeleteAsset(ap);
        }
        var busProfile = BusProfile();
        var built = new List<(TrafficVehicle prefab, float weight)>();
        foreach (var spec in Specs())
        {
            var pf = Build(spec, busProfile, log);
            if (pf != null) built.Add((pf, spec.weight));
        }
        AssetDatabase.SaveAssets();
        // Reimportar: la copia en memoria de los prefabs podia quedar con la malla fusionada nula
        foreach (var (pf, _) in built) AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(pf), ImportAssetOptions.ForceUpdate);
        if (assignSpawners) AssignSpawners(built, log);
        return log.ToString();
    }

    // ------------------------------------------------------------
    private static TrafficVehicle Build(Spec spec, DriverProfile busProfile, StringBuilder log)
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>($"{SrcDir}/{spec.model}");
        if (src == null) { log.AppendLine($"  {spec.name}: no se encontro {spec.model}"); return null; }

        var root = new GameObject("Veh_" + spec.name);
        int layer = LayerMask.NameToLayer("Vehicle");
        // root / Model (escala y posicion, sin giro) / modelo de terceros (giro)
        var wrapper = new GameObject("Model");
        wrapper.transform.SetParent(root.transform, false);
        var model = (GameObject)PrefabUtility.InstantiatePrefab(src);
        model.transform.SetParent(wrapper.transform, false);

        // Orientacion: eje principal en planta y frente (cristal delantero / focos) hacia +Z
        float yaw = ForwardYaw(model, spec);
        model.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

        // Escala (bus): a las dimensiones objetivo (ancho, alto, largo) en el marco del vehiculo
        if (spec.targetSize != Vector3.zero)
        {
            Bounds b0 = LocalBounds(root, model);
            wrapper.transform.localScale = new Vector3(spec.targetSize.x / b0.size.x, spec.targetSize.y / b0.size.y,
                                                       spec.targetSize.z / b0.size.z);
        }

        // Centrar en planta y apoyar en el suelo
        Bounds b = LocalBounds(root, model);
        wrapper.transform.localPosition -= new Vector3(b.center.x, b.min.y, b.center.z);
        b = LocalBounds(root, model);

        if (spec.removeMats.Count > 0 || spec.removeMatRear != null) RemoveSubmeshes(spec, root, model, log);

        // Materiales propios y carroceria
        var renderers = model.GetComponentsInChildren<Renderer>(true);
        var bodyR = new List<Renderer>(); var bodyIdx = new List<int>();
        foreach (var r in renderers)
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                string mn = mats[i].name;
                Color c = spec.recolor.TryGetValue(mn, out Color rc) ? rc : mats[i].color;
                mats[i] = OwnMaterial(spec.name, mn, c, mats[i]);
                if (spec.randomColor && mn == spec.bodyMat) { bodyR.Add(r); bodyIdx.Add(i); }
            }
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }
        if (!string.IsNullOrEmpty(spec.splitMat)) SplitByHeight(spec, root, model, log);

        // Distancia entre ejes (de las ruedas) antes de fusionar
        var wheels = new List<Transform>(); float wheelR = 0.33f;
        float frontZ = float.NaN, backZ = float.NaN;
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.Contains("Wheel")) continue;
            var wr = t.GetComponent<Renderer>(); if (wr == null) continue;
            Vector3 c = root.transform.InverseTransformPoint(wr.bounds.center);
            Vector3 piv = root.transform.InverseTransformPoint(t.position);
            if ((c - piv).magnitude < 0.06f) { wheels.Add(t); wheelR = wr.bounds.extents.y; }
            if (c.z > 0f) frontZ = float.IsNaN(frontZ) ? c.z : Mathf.Max(frontZ, c.z);
            else backZ = float.IsNaN(backZ) ? c.z : Mathf.Min(backZ, c.z);
        }
        float wheelbase = spec.wheelbase > 0f ? spec.wheelbase
                        : (!float.IsNaN(frontZ) && !float.IsNaN(backZ) ? frontZ - backZ : b.size.z * 0.6f);

        // Fusionar todas las piezas en una malla con color por vertice (las ruedas ya no giran: un draw call por vehiculo)
        var bodyMats = new HashSet<Material>();
        for (int i = 0; i < bodyR.Count; i++) bodyMats.Add(bodyR[i].sharedMaterials[bodyIdx[i]]);
        var combined = CombineToVertexColors(spec, root, wrapper, bodyMats);
        Object.DestroyImmediate(wrapper);
        var body = new GameObject("Body");
        body.transform.SetParent(root.transform, false);
        body.AddComponent<MeshFilter>().sharedMesh = combined;
        var bodyRenderer = body.AddComponent<MeshRenderer>();
        bodyRenderer.sharedMaterial = VertexColorMaterial();
        bodyR = new List<Renderer> { bodyRenderer }; bodyIdx = new List<int> { 0 };
        wheels.Clear();

        // Raiz: fisica, colision y datos
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        var box = root.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, b.size.y * 0.5f, 0f);
        box.size = b.size;
        var rb = root.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.useGravity = false;
        var tv = root.AddComponent<TrafficVehicle>();
        var so = new SerializedObject(tv);
        so.FindProperty("lengthM").floatValue = Round2(b.size.z);
        so.FindProperty("widthM").floatValue = Round2(b.size.x);
        so.FindProperty("modelType").stringValue = spec.type;
        so.FindProperty("wheelbaseM").floatValue = Round2(wheelbase);
        so.FindProperty("profileOverride").objectReferenceValue = spec.useBusProfile ? busProfile : null;
        so.FindProperty("randomBodyColor").boolValue = spec.randomColor && bodyR.Count > 0;
        SetArray(so.FindProperty("bodyRenderers"), bodyR.Cast<Object>().ToList());
        var idxProp = so.FindProperty("bodyMaterialIndex"); idxProp.arraySize = bodyIdx.Count;
        for (int i = 0; i < bodyIdx.Count; i++) idxProp.GetArrayElementAtIndex(i).intValue = bodyIdx[i];
        SetArray(so.FindProperty("wheels"), wheels.Cast<Object>().ToList());
        so.FindProperty("wheelRadiusM").floatValue = wheelR;
        so.ApplyModifiedPropertiesWithoutUndo();

        var lod = root.AddComponent<LODGroup>();
        lod.SetLODs(new[] { new LOD(0.01f, new Renderer[] { bodyRenderer }) });

        string path = $"{OutDir}/Veh_{spec.name}.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        log.AppendLine($"  Veh_{spec.name}: {b.size.z:F2} x {b.size.x:F2} x {b.size.y:F2} m, entre ejes {wheelbase:F2} m, " +
                       $"giro {yaw:F0} grados, {combined.triangles.Length / 3} triangulos, color {(spec.randomColor ? "sorteado" : "fijo")}, peso {spec.weight}");
        return prefab.GetComponent<TrafficVehicle>();
    }

    /// <summary>Giro (grados) que lleva el eje principal del modelo a Z con el frente hacia +Z.</summary>
    private static float ForwardYaw(GameObject model, Spec spec)
    {
        // Puntos de todas las mallas en planta
        var pts = new List<Vector3>(); var frontPts = new List<Vector3>(); var backPts = new List<Vector3>();
        foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
        {
            var mesh = mf.sharedMesh; if (mesh == null) continue;
            var mr = mf.GetComponent<MeshRenderer>(); var vs = mesh.vertices;
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                string mn = mr != null && sm < mr.sharedMaterials.Length && mr.sharedMaterials[sm] != null ? mr.sharedMaterials[sm].name : "";
                bool front = mn.Contains("Headlights") || mn == "mat25";          // focos / parabrisas
                bool back = mn.Contains("TailLights") || mn == "mat8" || mn == "mat14";
                foreach (int idx in mesh.GetTriangles(sm))
                {
                    Vector3 w = model.transform.InverseTransformPoint(mf.transform.TransformPoint(vs[idx]));
                    pts.Add(w); if (front) frontPts.Add(w); if (back) backPts.Add(w);
                }
            }
        }
        double mx = pts.Average(p => p.x), mz = pts.Average(p => p.z);
        double sxx = 0, szz = 0, sxz = 0;
        foreach (var p in pts) { double dx = p.x - mx, dz = p.z - mz; sxx += dx * dx; szz += dz * dz; sxz += dx * dz; }
        double ang = 0.5 * System.Math.Atan2(2 * sxz, sxx - szz);
        Vector3 axis = new Vector3((float)System.Math.Cos(ang), 0f, (float)System.Math.Sin(ang));
        float f = frontPts.Count > 0 ? frontPts.Average(p => Vector3.Dot(p, axis)) : 1f;
        float k = backPts.Count > 0 ? backPts.Average(p => Vector3.Dot(p, axis)) : -1f;
        if (f < k) axis = -axis;                                     // el frente queda en +axis
        if (spec.flipForward) axis = -axis;
        float heading = Mathf.Atan2(axis.x, axis.z) * Mathf.Rad2Deg;   // rumbo del frente (0 = +Z)
        float yaw = -heading;
        return Mathf.Abs(yaw) < 0.5f ? 0f : yaw;
    }

    /// <summary>Limites del modelo en el marco de la raiz.</summary>
    private static Bounds LocalBounds(GameObject root, GameObject model)
    {
        bool has = false; Bounds b = default;
        foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            foreach (var v in mf.sharedMesh.vertices)
            {
                Vector3 p = root.transform.InverseTransformPoint(mf.transform.TransformPoint(v));
                if (!has) { b = new Bounds(p, Vector3.zero); has = true; } else b.Encapsulate(p);
            }
        }
        return b;
    }

    /// <summary>Taxi: los triangulos de 'splitMat' por encima de una altura pasan a un submesh nuevo (techo amarillo).</summary>
    private static void SplitByHeight(Spec spec, GameObject root, GameObject model, StringBuilder log)
    {
        foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
        {
            var mr = mf.GetComponent<MeshRenderer>(); if (mr == null || mf.sharedMesh == null) continue;
            var mats = mr.sharedMaterials.ToList();
            int target = mats.FindIndex(m => m != null && m.name.EndsWith("_" + spec.splitMat));
            if (target < 0) continue;
            var src = mf.sharedMesh; var mesh = Object.Instantiate(src); mesh.name = $"{spec.name}_{src.name}_split";
            var verts = src.vertices; var tris = src.GetTriangles(target);
            var low = new List<int>(); var high = new List<int>();
            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 c = (verts[tris[i]] + verts[tris[i + 1]] + verts[tris[i + 2]]) / 3f;
                float y = root.transform.InverseTransformPoint(mf.transform.TransformPoint(c)).y;
                (y > spec.splitAboveY ? high : low).AddRange(new[] { tris[i], tris[i + 1], tris[i + 2] });
            }
            if (high.Count == 0) continue;
            mesh.subMeshCount = src.subMeshCount + 1;
            for (int sm = 0; sm < src.subMeshCount; sm++) mesh.SetTriangles(sm == target ? low.ToArray() : src.GetTriangles(sm), sm);
            mesh.SetTriangles(high.ToArray(), src.subMeshCount);
            mf.sharedMesh = mesh;   // intermedia, en memoria (la que se guarda es la fusionada)
            mats.Add(OwnMaterial(spec.name, spec.splitMat + "_Roof", spec.splitColor, null));
            mr.sharedMaterials = mats.ToArray();
            log.AppendLine($"    {spec.name}: techo separado ({high.Count / 3} triangulos sobre {spec.splitAboveY} m)");
        }
    }

    /// <summary>
    /// Quita de las mallas las submallas con esos materiales y, de 'removeMatRear', los triangulos que quedan detras
    /// de removeRearOfZ (en el marco del vehiculo). Las mallas modificadas se guardan como assets.
    /// </summary>
    private static void RemoveSubmeshes(Spec spec, GameObject root, GameObject model, StringBuilder log)
    {
        int pieces = 0, trisRemoved = 0;
        foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
        {
            var mr = mf.GetComponent<MeshRenderer>(); if (mr == null || mf.sharedMesh == null) continue;
            var src = mf.sharedMesh; var mats = mr.sharedMaterials; var verts = src.vertices;
            var newTris = new List<int[]>(); var newMats = new List<Material>(); bool changed = false;
            for (int i = 0; i < mats.Length && i < src.subMeshCount; i++)
            {
                string mn = mats[i] != null ? mats[i].name : "";
                var tris = src.GetTriangles(i);
                if (spec.removeMats.Contains(mn)) { changed = true; trisRemoved += tris.Length / 3; continue; }
                if (mn == spec.removeMatRear)
                {
                    var keep = new List<int>();
                    for (int t = 0; t < tris.Length; t += 3)
                    {
                        Vector3 c = (verts[tris[t]] + verts[tris[t + 1]] + verts[tris[t + 2]]) / 3f;
                        if (root.transform.InverseTransformPoint(mf.transform.TransformPoint(c)).z >= spec.removeRearOfZ)
                            keep.AddRange(new[] { tris[t], tris[t + 1], tris[t + 2] });
                    }
                    if (keep.Count != tris.Length) { changed = true; trisRemoved += (tris.Length - keep.Count) / 3; }
                    tris = keep.ToArray();
                }
                newTris.Add(tris); newMats.Add(mats[i]);
            }
            if (!changed) continue;
            var mesh = Object.Instantiate(src); mesh.name = $"{spec.name}_{src.name}_clean";
            mesh.subMeshCount = newTris.Count;
            for (int k = 0; k < newTris.Count; k++) mesh.SetTriangles(newTris[k], k);
            mf.sharedMesh = mesh;   // intermedia, en memoria
            mr.sharedMaterials = newMats.ToArray();
            pieces++;
        }
        log.AppendLine($"    {spec.name}: {trisRemoved} triangulos quitados en {pieces} piezas");
    }

    /// <summary>
    /// Fusiona todas las mallas bajo 'from' en una sola malla en el marco de 'root'. Cada vertice lleva el color del
    /// material de su pieza (en espacio lineal si el proyecto es lineal); alfa = 1 en la carroceria (color sorteado).
    /// </summary>
    private static Mesh CombineToVertexColors(Spec spec, GameObject root, GameObject from, HashSet<Material> bodyMats)
    {
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var cols = new List<Color>(); var tris = new List<int>();
        bool linear = PlayerSettings.colorSpace == ColorSpace.Linear;
        foreach (var mf in from.GetComponentsInChildren<MeshFilter>(true))
        {
            var mr = mf.GetComponent<MeshRenderer>(); var mesh = mf.sharedMesh;
            if (mr == null || mesh == null || !mr.enabled) continue;
            var mv = mesh.vertices; var mn = mesh.normals; bool hasN = mn != null && mn.Length == mv.Length;
            var toRoot = root.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            var nMat = toRoot.inverse.transpose;
            for (int sm = 0; sm < mesh.subMeshCount && sm < mr.sharedMaterials.Length; sm++)
            {
                var mat = mr.sharedMaterials[sm]; if (mat == null) continue;
                Color c = mat.color; if (linear) c = c.linear;
                c.a = bodyMats.Contains(mat) ? 1f : 0f;
                var remap = new Dictionary<int, int>();
                var st = mesh.GetTriangles(sm);
                for (int t = 0; t < st.Length; t++)
                {
                    if (!remap.TryGetValue(st[t], out int ni))
                    {
                        ni = verts.Count; remap[st[t]] = ni;
                        verts.Add(toRoot.MultiplyPoint3x4(mv[st[t]]));
                        norms.Add(hasN ? nMat.MultiplyVector(mn[st[t]]).normalized : Vector3.up);
                        cols.Add(c);
                    }
                    tris.Add(ni);
                }
            }
        }
        // Se reutiliza el asset si existe (borrarlo y recrearlo en la misma ruta dejaba referencias nulas)
        string mp = $"{OutDir}/Meshes/Veh_{spec.name}_combined.asset";
        var outMesh = AssetDatabase.LoadAssetAtPath<Mesh>(mp);
        bool isNew = outMesh == null;
        if (isNew) outMesh = new Mesh { name = $"Veh_{spec.name}_combined" };
        outMesh.Clear();
        outMesh.indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        outMesh.SetVertices(verts); outMesh.SetNormals(norms); outMesh.SetColors(cols); outMesh.SetTriangles(tris, 0);
        outMesh.RecalculateBounds();
        if (isNew) AssetDatabase.CreateAsset(outMesh, mp); else EditorUtility.SetDirty(outMesh);
        return outMesh;
    }

    private static Material VertexColorMaterial()
    {
        string p = $"{OutDir}/Materials/Vehicle_VertexColor.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(p);
        if (m == null) { m = new Material(Shader.Find("SimBici/VehicleVertexColor")); AssetDatabase.CreateAsset(m, p); }
        m.shader = Shader.Find("SimBici/VehicleVertexColor");
        m.enableInstancing = true;
        m.SetFloat("_Glossiness", 0.45f); m.SetFloat("_Metallic", 0.05f);
        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>Material intermedio en memoria: solo aporta el color de la pieza a la malla fusionada.</summary>
    private static Material OwnMaterial(string vehicle, string matName, Color color, Material src)
    {
        var m = new Material(Shader.Find("Standard")) { name = $"{vehicle}_{matName}" };
        m.color = color;
        bool glass = matName.Contains("Window") || matName == "mat25" || matName == "mat17";
        m.SetFloat("_Glossiness", glass ? 0.85f : matName.Contains("Black") || matName == "mat23" ? 0.2f : 0.45f);
        m.SetFloat("_Metallic", glass ? 0.1f : 0.05f);
        if (src != null && src.color.a < 0.99f && matName == "mat25")
            m.color = new Color(0.12f, 0.16f, 0.18f);   // cristal del bus: opaco oscuro (el transparente cuesta en VR)
        return m;
    }

    private static DriverProfile BusProfile()
    {
        const string p = "Assets/scripts/traffic/Profiles/DriverProfile_Bus.asset";
        var prof = AssetDatabase.LoadAssetAtPath<DriverProfile>(p);
        if (prof == null) { prof = ScriptableObject.CreateInstance<DriverProfile>(); AssetDatabase.CreateAsset(prof, p); }
        // Bus urbano: mas lento, acelera y frena suave, deja mas espacio. [VERIFICAR] con observacion
        prof.speedFactorMean = 0.85f; prof.speedFactorSd = 0.05f; prof.speedFactorRange = new Vector2(0.7f, 1.0f);
        prof.timeHeadwayMean = 1.6f; prof.timeHeadwaySd = 0.2f; prof.timeHeadwayRange = new Vector2(1.2f, 2.2f);
        prof.minGapMean = 2.5f; prof.minGapSd = 0.3f; prof.minGapRange = new Vector2(2.0f, 3.5f);
        prof.maxAccelMean = 1.0f; prof.maxAccelSd = 0.1f; prof.maxAccelRange = new Vector2(0.8f, 1.3f);
        prof.comfortDecelMean = 1.5f; prof.comfortDecelSd = 0.2f; prof.comfortDecelRange = new Vector2(1.2f, 2.0f);
        prof.criticalGapMean = 5.5f; prof.criticalGapSd = 0.6f; prof.criticalGapRange = new Vector2(4.0f, 8.0f);
        EditorUtility.SetDirty(prof);
        return prof;
    }

    private static void AssignSpawners(List<(TrafficVehicle prefab, float weight)> built, StringBuilder log)
    {
        var spawners = Object.FindObjectsByType<TrafficSpawner>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                             .OrderBy(s => s.name, System.StringComparer.Ordinal).ToList();
        foreach (var sp in spawners)
        {
            var so = new SerializedObject(sp);
            var list = so.FindProperty("vehicles");
            list.arraySize = built.Count;
            for (int i = 0; i < built.Count; i++)
            {
                var e = list.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("prefab").objectReferenceValue = built[i].prefab;
                e.FindPropertyRelative("weight").floatValue = built[i].weight;
            }
            so.ApplyModifiedProperties();
        }
        if (spawners.Count > 0) EditorSceneManager.MarkSceneDirty(spawners[0].gameObject.scene);
        log.AppendLine($"  {spawners.Count} spawners con la mezcla nueva ({string.Join(", ", built.Select(b => $"{b.prefab.name} {b.weight}"))})");
    }

    private static void SetArray(SerializedProperty prop, List<Object> items)
    {
        prop.arraySize = items.Count;
        for (int i = 0; i < items.Count; i++) prop.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
    }

    private static float Round2(float x) => Mathf.Round(x * 100f) / 100f;

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }
}
