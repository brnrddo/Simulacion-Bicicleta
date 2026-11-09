// ============================================================
// ParkedCarsEditor.cs
// SimBici > Place Parked Cars (2026-11-09): autos estacionados en la berma interior
// del anillo (la que era la segunda ciclovia), solo frente a edificios (no frente
// al pasto), en grupos de 2 a 5 autos con espacios entre ellos (~40 % del largo
// elegible). Se dejan libres los extremos de cada cuadra, los pasos peatonales,
// los paraderos y los grifos. Usa los modelos y colores del trafico, con semilla
// (TrafficManager.CreateRng(Seed, 5100)). Como no se mueven, todos se dibujan en una
// sola malla estatica con el color de carroceria horneado en los vertices; cada auto
// queda como un objeto con su colision (StaticObstacle) y su ParkedVehicle. Al final
// regenera la red del ciclista: en esos tramos el ciclista va por la calzada, junto a
// los autos estacionados.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

public static class ParkedCarsEditor
{
    public const string RootName = "Autos_Estacionados";
    public const int Seed = 20261109;
    public const int RngOffset = 5100;

    private const float CurbGapM = 0.15f;           // entre el pie de la solera y el costado del auto
    private const float CarGapM = 1.2f;             // entre autos de un mismo grupo
    private const float LaneEndClearM = 12f;        // libre en los extremos de cada cuadra
    private const float CrosswalkClearM = 6f;
    private const float BusStopClearM = 10f;
    private const float HydrantClearM = 3.5f;
    private static readonly Vector2Int GroupSize = new Vector2Int(2, 5);
    private static readonly Vector2 GroupGapM = new Vector2(15f, 40f);

    private static readonly (string prefab, float weight)[] Mix =
    {
        ("Veh_Sedan", 30f), ("Veh_Hatchback", 26f), ("Veh_SUV", 20f), ("Veh_Coupe", 5f), ("Veh_Taxi", 8f),
    };
    private const string PrefabDir = "Assets/AssetsLab/Vehicles";

    [MenuItem("SimBici/Place Parked Cars")]
    private static void Menu() => Debug.Log(Build());

    public static string Build()
    {
        if (EditorApplication.isPlaying) return "[ParkedCars] Salir de Play antes de generar.";
        var log = new StringBuilder("[ParkedCars]\n");
        var old = GameObject.Find(RootName);
        if (old != null) Undo.DestroyObjectImmediate(old);
        if (!RingBikeLaneEditor.TryGetRing(out var ring)) return log + "  ERROR: no se encontro el anillo";

        var prefabs = Mix.Select(m => (pf: AssetDatabase.LoadAssetAtPath<TrafficVehicle>($"{PrefabDir}/{m.prefab}.prefab"), m.weight))
                         .Where(m => m.pf != null).ToList();
        if (prefabs.Count == 0) return log + "  ERROR: no hay prefabs de autos en " + PrefabDir;
        float totalW = prefabs.Sum(p => p.weight);
        var rng = TrafficManager.CreateRng(Seed, RngOffset);

        var crosswalks = Object.FindObjectsByType<Crosswalk>(FindObjectsSortMode.None);
        var buildings = GameObject.Find("City/Buildings");
        var decor = GameObject.Find($"{CityDecorator.RootName}/Colisiones");
        var stops = new List<Vector3>(); var hydrants = new List<Vector3>();
        if (decor != null)
            foreach (Transform t in decor.transform)
            {
                if (t.name.StartsWith("Paradero") || t.name.StartsWith("Senal_parada")) stops.Add(t.position);
                else if (t.name.StartsWith("Grifo")) hydrants.Add(t.position);
            }

        // Colliders temporales de las veredas (no tienen) para encontrar la solera
        var temp = new List<GameObject>();
        var sw = GameObject.Find("City/Sidewalks");
        if (sw != null)
            foreach (var mf in sw.GetComponentsInChildren<MeshFilter>())
            {
                var go = new GameObject("TmpSidewalk") { hideFlags = HideFlags.HideAndDontSave, layer = 2 };
                go.transform.SetPositionAndRotation(mf.transform.position, mf.transform.rotation);
                go.transform.localScale = mf.transform.lossyScale;
                go.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                temp.Add(go);
            }
        Physics.SyncTransforms();

        var root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, RootName);
        int placed = 0, groups = 0, obstacle = LayerMask.NameToLayer("StaticObstacle");
        var mesh = new CombinedMesh();
        Material bodyMat = null;
        float eligibleM = 0f, usedM = 0f;
        try
        {
            var lanes = Object.FindObjectsByType<TrafficLane>(FindObjectsSortMode.None)
                              .Where(l => !l.name.Contains("Conn_"))
                              .OrderBy(l => l.name, System.StringComparer.Ordinal).ToList();
            foreach (var lane in lanes)
            {
                float len = lane.GetComponent<SplineContainer>().Spline.GetLength();
                if (len < 2f * LaneEndClearM + 6f) continue;
                // Solo los carriles cuya solera derecha es la berma interior del anillo
                if (!Curb(lane, len * 0.5f, out Vector3 mid, out _, out _)) continue;
                float o = ring.Offset(mid);
                if (o > -(ring.HalfWidth - 0.6f) || o < -(ring.HalfWidth + 0.8f)) continue;

                bool Eligible(float s)
                {
                    if (s < LaneEndClearM || s > len - LaneEndClearM) return false;
                    if (!Curb(lane, s, out Vector3 c, out Vector3 n, out _)) return false;
                    if (RingBikeLaneEditor.OnCrosswalk(crosswalks, c, CrosswalkClearM)) return false;
                    if (stops.Any(p => Flat(p - c).magnitude < BusStopClearM)) return false;
                    if (hydrants.Any(p => Flat(p - c).magnitude < HydrantClearM)) return false;
                    return FacesBuildings(c, n, buildings);
                }

                float sCur = LaneEndClearM + (float)rng.NextDouble() * 10f;
                while (sCur < len - LaneEndClearM)
                {
                    if (!Eligible(sCur)) { sCur += 1f; continue; }
                    int want = rng.Next(GroupSize.x, GroupSize.y + 1);
                    int inGroup = 0;
                    while (inGroup < want)
                    {
                        var (pf, _) = Pick(prefabs, totalW, rng);
                        float L = pf.LengthM, W = pf.WidthM;
                        float sc = sCur + L * 0.5f;
                        if (!(Eligible(sCur) && Eligible(sc) && Eligible(sCur + L))) break;
                        Curb(lane, sc, out Vector3 curb, out Vector3 nrm, out Vector3 fwd);
                        Color color = pf.RandomBodyColor ? TrafficManager.SampleBodyColor(rng) : Color.white;
                        float jitterLat = (float)rng.NextDouble() * 0.10f, jitterYaw = ((float)rng.NextDouble() - 0.5f) * 3f;
                        Vector3 pos = curb - nrm * (CurbGapM + jitterLat + W * 0.5f);
                        pos.y = 0f;
                        var m = Place(root.transform, pf, pos, Quaternion.AngleAxis(jitterYaw, Vector3.up) * Quaternion.LookRotation(fwd, Vector3.up),
                                      color, obstacle, placed, mesh);
                        if (bodyMat == null) bodyMat = m;
                        placed++; inGroup++;
                        usedM += L + CarGapM;
                        sCur += L + CarGapM;
                    }
                    if (inGroup > 0) groups++;
                    sCur += Mathf.Lerp(GroupGapM.x, GroupGapM.y, (float)rng.NextDouble());
                }
                for (float s = LaneEndClearM; s < len - LaneEndClearM; s += 1f) if (Eligible(s)) eligibleM += 1f;
            }
        }
        finally { foreach (var g in temp) if (g != null) Object.DestroyImmediate(g); }

        if (mesh.v.Count > 0 && bodyMat != null)
        {
            var go = new GameObject("Malla");
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh.Save();
            go.AddComponent<MeshRenderer>().sharedMaterial = bodyMat;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
        }
        EditorSceneManager.MarkSceneDirty(root.scene);
        log.AppendLine($"  {placed} autos estacionados en {groups} grupos; ocupan {usedM:F0} de {eligibleM:F0} m elegibles ({(eligibleM > 0 ? usedM / eligibleM : 0f):P0})");

        // La red del ciclista esquiva los autos estacionados
        var net = Object.FindFirstObjectByType<CyclistNetwork>();
        if (net != null)
        {
            string g = CyclistNetworkEditor.Generate(net);
            log.AppendLine(g.Split('\n')[0]);
            foreach (var line in g.Split('\n').Where(l => l.Contains("esquiva"))) log.AppendLine(line);
        }
        return log.ToString();
    }

    private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    private static (TrafficVehicle, float) Pick(List<(TrafficVehicle pf, float weight)> prefabs, float total, System.Random rng)
    {
        double r = rng.NextDouble() * total;
        foreach (var p in prefabs) { r -= p.weight; if (r <= 0) return (p.pf, p.weight); }
        return (prefabs[prefabs.Count - 1].pf, prefabs[prefabs.Count - 1].weight);
    }

    /// <summary>Malla de todos los autos estacionados, con el color de carroceria horneado (alfa 0).</summary>
    private class CombinedMesh
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Color> c = new List<Color>();
        public readonly List<int> t = new List<int>();

        public void Add(Mesh src, Matrix4x4 m, Color body, bool paint)
        {
            int baseIdx = v.Count;
            var verts = src.vertices; var norms = src.normals; var cols = src.colors;
            for (int i = 0; i < verts.Length; i++)
            {
                v.Add(m.MultiplyPoint3x4(verts[i]));
                n.Add(norms.Length > 0 ? m.MultiplyVector(norms[i]).normalized : Vector3.up);
                Color k = cols.Length > 0 ? cols[i] : Color.white;
                // El sombreador mezcla color del vertice y _BodyColor segun el alfa: se hornea la mezcla y alfa = 0
                Color baked = paint ? Color.Lerp(k, body.linear, k.a) : k;
                baked.a = 0f;
                c.Add(baked);
            }
            foreach (int i in src.triangles) t.Add(baseIdx + i);
        }

        public Mesh Save()
        {
            const string path = "Assets/AssetsLab/Vehicles/Meshes/Autos_Estacionados.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool isNew = mesh == null;
            if (isNew) mesh = new Mesh { name = "Autos_Estacionados" };
            mesh.Clear();
            mesh.indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetColors(c); mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            if (isNew) AssetDatabase.CreateAsset(mesh, path); else EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssets();
            return mesh;
        }
    }

    /// <summary>
    /// Auto estacionado: objeto con la colision (StaticObstacle) y ParkedVehicle; su carroceria va a la malla
    /// combinada. Devuelve el material de la carroceria.
    /// </summary>
    private static Material Place(Transform parent, TrafficVehicle pf, Vector3 pos, Quaternion rot, Color color, int layer,
                                  int index, CombinedMesh mesh)
    {
        var go = new GameObject($"Estacionado_{index:D3}_{pf.ModelType}");
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(pos, rot);
        if (layer >= 0) go.layer = layer;
        var src = pf.GetComponent<BoxCollider>();
        var box = go.AddComponent<BoxCollider>();
        if (src != null) { box.center = src.center; box.size = src.size; }
        else { box.size = new Vector3(pf.WidthM, 1.5f, pf.LengthM); box.center = new Vector3(0f, 0.75f, 0f); }
        go.AddComponent<ParkedVehicle>().Setup(pf.ModelType, pf.LengthM, pf.WidthM, null, false, color);
        Undo.RegisterCreatedObjectUndo(go, go.name);

        var body = pf.transform.Find("Body");
        var mf = body != null ? body.GetComponent<MeshFilter>() : null;
        if (mf == null || mf.sharedMesh == null) return null;
        // Del espacio del prefab (raiz en el origen) al mundo
        Matrix4x4 m = Matrix4x4.TRS(pos, rot, Vector3.one) * pf.transform.worldToLocalMatrix * body.localToWorldMatrix;
        mesh.Add(mf.sharedMesh, m, color, pf.RandomBodyColor);
        return body.GetComponent<MeshRenderer>().sharedMaterial;
    }

    /// <summary>
    /// Pie de la solera a la derecha del carril en s (y = 0), normal hacia la vereda y sentido del carril.
    /// La solera es una cara inclinada de ~0.4 m: su pie es el fin del collider de la calzada, y la vereda plana
    /// empieza ~0.4 m mas alla. Exige que haya vereda (no una boca de calle).
    /// </summary>
    private static bool Curb(TrafficLane lane, float s, out Vector3 curb, out Vector3 n, out Vector3 fwd)
    {
        var c = lane.GetComponent<SplineContainer>();
        var sp = c.Spline;
        float t = sp.ConvertIndexUnit(Mathf.Clamp(s, 0f, sp.GetLength()), PathIndexUnit.Distance, PathIndexUnit.Normalized);
        fwd = c.transform.TransformDirection((Vector3)sp.EvaluateTangent(t)); fwd.y = 0f; fwd.Normalize();
        Vector3 p = c.transform.TransformPoint((Vector3)sp.EvaluatePosition(t));
        n = new Vector3(fwd.z, 0f, -fwd.x);
        curb = default;
        float x = 0f;
        while (x < 8f && OnRoad(p + n * x)) x += 0.02f;
        if (x >= 8f) return false;
        Vector3 edge = p + n * x;
        // Debe haber vereda poco mas alla (si no, es una boca de calle o un rebaje)
        Vector3 q = edge + n * 0.8f;
        if (!(Physics.Raycast(new Vector3(q.x, 5f, q.z), Vector3.down, out RaycastHit hit, 10f, 1 << 2) && hit.point.y > 0.15f)) return false;
        curb = new Vector3(edge.x, 0f, edge.z);
        return true;
    }

    private static bool OnRoad(Vector3 p) =>
        Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 10f) &&
        hit.collider.gameObject.layer == LayerMask.NameToLayer("Ground") && !(hit.collider is TerrainCollider);

    /// <summary>True si detras de la vereda (hasta 30 m) hay un edificio y no pasto abierto.</summary>
    private static bool FacesBuildings(Vector3 curb, Vector3 n, GameObject buildings)
    {
        if (buildings == null) return false;
        foreach (var h in Physics.RaycastAll(curb + Vector3.up * 2.5f, n, 30f))
            if (h.collider.transform.IsChildOf(buildings.transform)) return true;
        return false;
    }
}
