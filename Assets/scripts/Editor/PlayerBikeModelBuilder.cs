// ============================================================
// PlayerBikeModelBuilder.cs
// SimBici > Build Player Bike Model (2026-11-06): reemplaza el modelo BMX de
// Understone (Asset Store, no redistribuible) por "Vintage Bicycle" de VladNeko
// (Sketchfab, CC BY 4.0) en AssetsLab/playerBike 1.prefab.
//
// El GLB viene separado por material, no por pieza (las dos ruedas en una malla,
// la horquilla y el manubrio junto al cuadro). Se separa por islas de malla
// conectada (no se corta geometria):
//   - ruedas: islas de la malla de ruedas, segun el eje mas cercano;
//   - direccion: horquilla, guardabarros, freno y reflector delanteros (bajo el
//     tubo de direccion y delante de su eje) y potencia, manubrio, punos y manillas
//     (arriba, en la mitad delantera);
//   - cuadro: el resto.
// Escala: radio de rueda = wheelRadiusM del BicycleAnimator (0.334 m, el mismo de
// la circunferencia de 2.1 m del sensor). Jerarquia nueva:
//   VintageBike (visualRoot) / Cuadro, RuedaTrasera, EjeDireccion (inclinado) /
//   Handlebar (handlebarTransform) / RuedaDelantera (frontWheelTransform)
// La rueda delantera es hija del manubrio para girar con la direccion. Las mallas
// se hornean en el espacio local de cada pieza (sin escalas en los transforms).
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class PlayerBikeModelBuilder
{
    private const string ModelPath = "Assets/ThirdParty/Bike/VintageBicycle/vintage_bicycle.glb";
    private const string PrefabPath = "Assets/AssetsLab/playerBike 1.prefab";
    private const string MeshDir = "Assets/AssetsLab/Bike/Generated";
    private const string OldRootName = "BMXBikeE";
    public const string RootName = "VintageBike";

    // El centro del manubrio queda donde estaba el de la BMX (adelante del origen de playerBike)
    private const float HandlebarForwardM = 0.57f;

    [MenuItem("SimBici/Build Player Bike Model")]
    private static void Menu() => Debug.Log(Build());

    private enum Part { Frame, Steer, FrontWheel, RearWheel }

    private class Piece
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Vector4> tan = new List<Vector4>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<List<int>> sub = new List<List<int>>();
        public readonly List<Material> mats = new List<Material>();
        public readonly Dictionary<(Mesh, int), int> remap = new Dictionary<(Mesh, int), int>();

        public List<int> Sub(Material m)
        {
            int i = mats.IndexOf(m);
            if (i < 0) { mats.Add(m); sub.Add(new List<int>()); i = mats.Count - 1; }
            return sub[i];
        }
    }

    public static string Build()
    {
        if (EditorApplication.isPlaying) return "[PlayerBike] Salir de Play antes de generar.";
        var log = new StringBuilder("[PlayerBike]\n");
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null) return "[PlayerBike] No se encontro " + ModelPath;

        var tmp = (GameObject)Object.Instantiate(model);
        tmp.hideFlags = HideFlags.HideAndDontSave;
        tmp.transform.position = Vector3.zero;   // conserva la rotacion propia del GLB (raiz a 270 grados en X)
        try
        {
            var filters = tmp.GetComponentsInChildren<MeshFilter>().OrderBy(f => f.name, System.StringComparer.Ordinal).ToList();

            // 1) Ejes de las ruedas: las dos islas grandes de la malla de ruedas (aros con rayos)
            var wheelMf = filters.First(f => f.name.Contains("whell"));
            var wheelIslands = Islands(wheelMf).OrderByDescending(i => i.tris.Count).Take(2).OrderBy(i => i.bounds.center.x).ToList();
            Vector3 hubF = wheelIslands[0].bounds.center, hubR = wheelIslands[1].bounds.center;   // el frente esta hacia -x
            float diameter = Mathf.Max(wheelIslands[0].bounds.size.x, wheelIslands[0].bounds.size.y);
            float wheelRadiusM = 0.334f;
            float s = wheelRadiusM / (diameter * 0.5f);
            log.AppendLine($"  Ejes (modelo): delantero {hubF:F0}, trasero {hubR:F0}; diametro {diameter:F0} -> escala {s:F6} (rueda de {2f * wheelRadiusM:F3} m)");

            // 2) Eje de direccion: de la parte alta de la horquilla a la base de la potencia
            var frameMf = filters.First(f => f.name.Contains("frame"));
            var frameIslands = Islands(frameMf);
            var fork = frameIslands.Where(i => Vector2.Distance(new Vector2(i.bounds.min.x, i.bounds.min.y), new Vector2(hubF.x, hubF.y)) < diameter * 0.25f)
                                   .OrderByDescending(i => i.bounds.size.y).First();
            Vector3 crown = TopCenter(fork, frameMf, 0.08f);
            log.AppendLine($"  Horquilla (modelo): {fork.bounds.center:F0} tamano {fork.bounds.size:F0}");
            var stem = frameIslands.Where(i => i.bounds.center.x < (hubF.x + hubR.x) * 0.5f && i.bounds.min.y > crown.y && i.bounds.size.z < diameter * 0.2f)
                                   .OrderBy(i => i.bounds.min.y).FirstOrDefault();
            if (stem == null)
                return log + "  ERROR: no se encontro la potencia. Islas del cuadro: " +
                       string.Join(" | ", frameIslands.Select(i => $"{i.bounds.center:F0} {i.bounds.size:F0}"));
            Vector3 stemBase = BottomCenter(stem, frameMf, 0.15f);
            Vector3 axis = (stemBase - crown).normalized;
            if (axis.y < 0f) axis = -axis;
            log.AppendLine($"  Eje de direccion (modelo): corona {crown:F0}, potencia {stemBase:F0}; inclinacion {Vector3.Angle(axis, Vector3.up):F1} grados de la vertical");

            // 3) Clasificar islas
            float midX = (hubF.x + hubR.x) * 0.5f;
            Vector3 axisNormal = Vector3.Cross(axis, Vector3.forward).normalized;   // en el plano lateral, hacia el frente o atras
            if (Vector3.Dot(axisNormal, Vector3.left) < 0f) axisNormal = -axisNormal; // apunta al frente (-x)
            float headTopY = stemBase.y;
            Part Classify(Island isl, bool wheelMesh)
            {
                Vector3 c = isl.bounds.center;
                if (wheelMesh) return Mathf.Abs(c.x - hubF.x) < Mathf.Abs(c.x - hubR.x) ? Part.FrontWheel : Part.RearWheel;
                if (c.x >= midX) return Part.Frame;
                float ahead = Vector3.Dot(c - crown, axisNormal);             // > 0: delante del eje de direccion
                float top = headTopY - diameter * 0.02f;
                if (c.y < top && ahead > -diameter * 0.045f) return Part.Steer;            // horquilla, freno, reflector
                if (c.y < top && c.x < crown.x &&
                    Vector2.Distance(new Vector2(c.x, c.y), new Vector2(hubF.x, hubF.y)) < diameter * 0.62f) return Part.Steer;   // guardabarros y sus varillas
                if (c.y >= top && c.x < crown.x + diameter * 0.45f) return Part.Steer;     // potencia, manubrio, punos, manillas
                return Part.Frame;
            }

            // 4) Transformacion modelo -> raiz VintageBike: frente (-x) hacia +z, suelo en y = 0, escala s
            Vector3 groundUnder = new Vector3(midX, hubF.y - diameter * 0.5f, (hubF.z + hubR.z) * 0.5f);
            Quaternion yaw = Quaternion.Euler(0f, 90f, 0f);   // -x -> +z
            Vector3 ToRoot(Vector3 p) => yaw * ((p - groundUnder) * s);

            Vector3 hubFr = ToRoot(hubF), hubRr = ToRoot(hubR), crownR = ToRoot(crown);
            Vector3 axisR = (yaw * axis).normalized;
            Quaternion steerRot = Quaternion.FromToRotation(Vector3.up, axisR);   // up local = eje de direccion

            // Marcos locales de cada pieza (relativos a la raiz)
            var frames = new Dictionary<Part, Matrix4x4>
            {
                [Part.Frame] = Matrix4x4.identity,
                [Part.RearWheel] = Matrix4x4.Translate(hubRr),
                [Part.Steer] = Matrix4x4.TRS(crownR, steerRot, Vector3.one),
                [Part.FrontWheel] = Matrix4x4.TRS(hubFr, steerRot, Vector3.one),
            };
            var pieces = new Dictionary<Part, Piece>();
            foreach (Part p in System.Enum.GetValues(typeof(Part))) pieces[p] = new Piece();
            var counts = new Dictionary<Part, int>();
            int removedCables = 0;

            foreach (var mf in filters)
            {
                bool isWheel = mf == wheelMf;
                var mesh = mf.sharedMesh;
                var mat = mf.GetComponent<MeshRenderer>().sharedMaterial;
                var M = mf.transform.localToWorldMatrix;
                var verts = mesh.vertices; var norms = mesh.normals; var tans = mesh.tangents; var uvs = mesh.uv;
                foreach (var isl in Islands(mf))
                {
                    // Cables de freno y cambios: van del manubrio al cuadro y no pueden seguir la direccion; se quitan
                    if (!isWheel && IsCable(isl, diameter)) { removedCables++; continue; }
                    Part part = Classify(isl, isWheel);
                    counts[part] = (counts.TryGetValue(part, out int k) ? k : 0) + 1;
                    var pc = pieces[part];
                    var inv = frames[part].inverse;
                    var list = pc.Sub(mat);
                    foreach (int t in isl.tris)
                    {
                        if (!pc.remap.TryGetValue((mesh, t), out int idx))
                        {
                            idx = pc.v.Count;
                            pc.remap[(mesh, t)] = idx;
                            pc.v.Add(inv.MultiplyPoint3x4(ToRoot(M.MultiplyPoint3x4(verts[t]))));
                            Vector3 nw = norms.Length > 0 ? M.MultiplyVector(norms[t]) : Vector3.up;
                            pc.n.Add(inv.MultiplyVector(yaw * nw).normalized);
                            if (tans.Length > 0)
                            {
                                Vector3 tw = inv.MultiplyVector(yaw * M.MultiplyVector(tans[t])).normalized;
                                pc.tan.Add(new Vector4(tw.x, tw.y, tw.z, tans[t].w));
                            }
                            pc.uv.Add(uvs.Length > 0 ? uvs[t] : Vector2.zero);
                        }
                        list.Add(idx);
                    }
                }
            }
            log.AppendLine("  Islas por pieza: " + string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}")) + $"; cables quitados: {removedCables}");

            // 5) Guardar mallas
            RingBikeLaneEditor.EnsureFolder(MeshDir);
            var meshes = new Dictionary<Part, Mesh>();
            foreach (var kv in pieces) meshes[kv.Key] = SaveMesh("Bike_" + kv.Key, kv.Value);

            // 6) Ancho del manubrio (extremos de los punos, a lo ancho de la bici)
            var steerVerts = pieces[Part.Steer].v.Select(v => frames[Part.Steer].MultiplyPoint3x4(v)).Where(v => v.y > crownR.y).ToList();
            float barWidth = steerVerts.Max(v => v.x) - steerVerts.Min(v => v.x);
            Vector3 gripsCenter = new Vector3(0f, steerVerts.Average(v => v.y), steerVerts.Where(v => Mathf.Abs(v.x) > barWidth * 0.4f).Average(v => v.z));
            log.AppendLine($"  Manubrio: {barWidth:F3} m de ancho; punos a {gripsCenter.y:F2} m de altura y {gripsCenter.z:F2} m delante del centro de la bici");
            log.AppendLine($"  Distancia entre ejes: {Vector3.Distance(hubFr, hubRr):F3} m");

            // 7) Prefab
            log.Append(ApplyToPrefab(meshes, pieces, frames, gripsCenter, barWidth));
        }
        finally { Object.DestroyImmediate(tmp); }
        return log.ToString();
    }

    private static string ApplyToPrefab(Dictionary<Part, Mesh> meshes, Dictionary<Part, Piece> pieces,
                                        Dictionary<Part, Matrix4x4> frames, Vector3 gripsCenter, float barWidth)
    {
        var log = new StringBuilder();
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var old = root.transform.Find(OldRootName);
            if (old != null) { Object.DestroyImmediate(old.gameObject); log.AppendLine("  Quitado el modelo BMX (Understone)"); }
            var prev = root.transform.Find(RootName);
            if (prev != null) Object.DestroyImmediate(prev.gameObject);

            var vis = new GameObject(RootName).transform;
            vis.SetParent(root.transform, false);
            vis.localPosition = new Vector3(0f, 0f, HandlebarForwardM - gripsCenter.z);

            Transform Make(string name, Transform parent, Matrix4x4 m, Part part)
            {
                var t = new GameObject(name).transform;
                t.SetParent(parent, false);
                Matrix4x4 rel = (parent == vis ? Matrix4x4.identity : RelTo(parent, vis)).inverse * m;
                t.localPosition = rel.GetColumn(3);
                t.localRotation = rel.rotation;
                t.gameObject.AddComponent<MeshFilter>().sharedMesh = meshes[part];
                var r = t.gameObject.AddComponent<MeshRenderer>();
                r.sharedMaterials = pieces[part].mats.ToArray();
                return t;
            }

            Make("Cuadro", vis, frames[Part.Frame], Part.Frame);
            var rear = Make("RuedaTrasera", vis, frames[Part.RearWheel], Part.RearWheel);
            var axisT = new GameObject("EjeDireccion").transform;
            axisT.SetParent(vis, false);
            axisT.localPosition = frames[Part.Steer].GetColumn(3);
            axisT.localRotation = frames[Part.Steer].rotation;
            // "Handlebar": el OvertakeMonitor busca el renderer por este prefijo
            var bar = new GameObject("Handlebar").transform;
            bar.SetParent(axisT, false);
            bar.gameObject.AddComponent<MeshFilter>().sharedMesh = meshes[Part.Steer];
            bar.gameObject.AddComponent<MeshRenderer>().sharedMaterials = pieces[Part.Steer].mats.ToArray();
            var front = Make("RuedaDelantera", bar, frames[Part.FrontWheel], Part.FrontWheel);

            // Referencias del animador (las ruedas giran en su eje local X: el eje de la rueda va a lo ancho)
            var anim = root.GetComponent<BicycleAnimator>();
            var so = new SerializedObject(anim);
            so.FindProperty("handlebarTransform").objectReferenceValue = bar;
            so.FindProperty("frontWheelTransform").objectReferenceValue = front;
            so.FindProperty("rearWheelTransform").objectReferenceValue = rear;
            so.FindProperty("visualRoot").objectReferenceValue = vis;
            so.FindProperty("wheelSpinAxis").enumValueIndex = (int)BicycleAnimator.SpinAxis.X;
            so.ApplyModifiedPropertiesWithoutUndo();

            var om = root.GetComponent<OvertakeMonitor>();
            if (om != null)
            {
                var so2 = new SerializedObject(om);
                so2.FindProperty("handlebar").objectReferenceValue = bar.GetComponent<MeshRenderer>();
                so2.ApplyModifiedPropertiesWithoutUndo();
            }
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            log.AppendLine($"  Prefab guardado: {PrefabPath} (manubrio {barWidth:F3} m)");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        return log.ToString();
    }

    private static Matrix4x4 RelTo(Transform t, Transform root)
    {
        Matrix4x4 m = Matrix4x4.identity;
        for (var x = t; x != null && x != root; x = x.parent) m = Matrix4x4.TRS(x.localPosition, x.localRotation, Vector3.one) * m;
        return m;
    }

    private static Mesh SaveMesh(string name, Piece pc)
    {
        string path = $"{MeshDir}/{name}.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool isNew = mesh == null;
        if (isNew) mesh = new Mesh { name = name };
        mesh.Clear();
        mesh.indexFormat = pc.v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(pc.v);
        mesh.SetNormals(pc.n);
        if (pc.tan.Count == pc.v.Count) mesh.SetTangents(pc.tan);
        mesh.SetUVs(0, pc.uv);
        mesh.subMeshCount = pc.sub.Count;
        for (int i = 0; i < pc.sub.Count; i++) mesh.SetTriangles(pc.sub[i], i);
        mesh.RecalculateBounds();
        if (isNew) AssetDatabase.CreateAsset(mesh, path);
        else EditorUtility.SetDirty(mesh);
        AssetDatabase.SaveAssets();
        return mesh;
    }

    // ------------------------------------------------------------
    // Islas de malla conectada (vertices soldados por posicion)
    // ------------------------------------------------------------
    private class Island { public readonly List<int> tris = new List<int>(); public Bounds bounds; public float area; }

    /// <summary>
    /// Tubo delgado y largo (cable): diametro equivalente = area / (pi * largo) menor que 0.9 % del diametro de la
    /// rueda (~6 mm) y largo mayor que 35 % de la rueda. Las varillas de los guardabarros (~7 mm) y los rieles del
    /// asiento (mas cortos) quedan.
    /// </summary>
    private static bool IsCable(Island isl, float wheelDiameter)
    {
        float len = isl.bounds.size.magnitude;
        float diam = isl.area / (Mathf.PI * Mathf.Max(len, 1e-3f));
        return len > wheelDiameter * 0.35f && diam < wheelDiameter * 0.009f;
    }

    private static readonly Dictionary<MeshFilter, List<Island>> islandCache = new Dictionary<MeshFilter, List<Island>>();

    private static List<Island> Islands(MeshFilter mf)
    {
        if (islandCache.TryGetValue(mf, out var cached) && cached != null) return cached;
        var mesh = mf.sharedMesh;
        var v = mesh.vertices; var tr = mesh.triangles; var M = mf.transform.localToWorldMatrix;
        var w = new Vector3[v.Length];
        for (int i = 0; i < v.Length; i++) w[i] = M.MultiplyPoint3x4(v[i]);
        var key = new Dictionary<Vector3Int, int>();
        var rep = new int[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            var k = new Vector3Int(Mathf.RoundToInt(w[i].x * 10f), Mathf.RoundToInt(w[i].y * 10f), Mathf.RoundToInt(w[i].z * 10f));
            if (!key.TryGetValue(k, out int r)) { r = i; key[k] = i; }
            rep[i] = r;
        }
        var par = new int[v.Length];
        for (int i = 0; i < par.Length; i++) par[i] = i;
        int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }
        for (int t = 0; t < tr.Length; t += 3)
        {
            int a = Find(rep[tr[t]]);
            par[Find(rep[tr[t + 1]])] = a;
            par[Find(rep[tr[t + 2]])] = a;
        }
        var map = new Dictionary<int, Island>();
        for (int t = 0; t < tr.Length; t += 3)
        {
            int r = Find(rep[tr[t]]);
            if (!map.TryGetValue(r, out var isl)) { isl = new Island { bounds = new Bounds(w[tr[t]], Vector3.zero) }; map[r] = isl; }
            for (int k = 0; k < 3; k++) { isl.tris.Add(tr[t + k]); isl.bounds.Encapsulate(w[tr[t + k]]); }
            isl.area += Vector3.Cross(w[tr[t + 1]] - w[tr[t]], w[tr[t + 2]] - w[tr[t]]).magnitude * 0.5f;
        }
        var list = map.Values.ToList();
        islandCache[mf] = list;
        return list;
    }

    private static Vector3 TopCenter(Island isl, MeshFilter mf, float frac)
    {
        var M = mf.transform.localToWorldMatrix; var v = mf.sharedMesh.vertices;
        var pts = isl.tris.Distinct().Select(i => M.MultiplyPoint3x4(v[i])).ToList();
        float cut = isl.bounds.max.y - isl.bounds.size.y * frac;
        var top = pts.Where(p => p.y >= cut).ToList();
        return new Vector3(top.Average(p => p.x), top.Average(p => p.y), top.Average(p => p.z));
    }

    private static Vector3 BottomCenter(Island isl, MeshFilter mf, float frac)
    {
        var M = mf.transform.localToWorldMatrix; var v = mf.sharedMesh.vertices;
        var pts = isl.tris.Distinct().Select(i => M.MultiplyPoint3x4(v[i])).ToList();
        float cut = isl.bounds.min.y + isl.bounds.size.y * frac;
        var bot = pts.Where(p => p.y <= cut).ToList();
        return new Vector3(bot.Average(p => p.x), bot.Average(p => p.y), bot.Average(p => p.z));
    }
}
