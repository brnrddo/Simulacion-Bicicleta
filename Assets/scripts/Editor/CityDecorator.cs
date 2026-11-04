// ============================================================
// CityDecorator.cs
// SimBici > Decorate City (A2, 2026-11-04): ambientacion de Santiago sin cambiar
// la geometria de calles, veredas ni edificios. Todo es procedural, con semilla
// (TrafficManager.CreateRng(Seed, 5000)), y se rehace o se quita entero
// (SimBici > Decorate City (quitar)). Referencias: Documentacion/A2_referencias.
//   - Senal PARE (en vez de STOP) en los letreros existentes.
//   - Placas de calle negras con letras blancas y flecha de sentido, en dos
//     esquinas de cada interseccion.
//   - Paraderos de Red (refugio, banca, paneles sin marcas, senal de parada,
//     solera amarilla de 12 m). El bus no se detiene.
//   - Vereda de baldosa 40 x 40 (textura propia proyectada en coordenadas de mundo).
//   - Soleras amarillas: berma interior del anillo y 8 m junto a las esquinas.
//   - Arboles existentes: base del tronco encalada y alcorque en la vereda.
//   - Grifos amarillos y basureros en la franja junto a la solera.
//   - Cordillera nevada al oriente (cerros bajos al poniente) y bruma leve.
// Lo que va sobre la vereda lleva NavMeshModifier no caminable; al final se rehace
// el setup de peatones (conserva la densidad elegida).
// ============================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

public static class CityDecorator
{
    public const string RootName = "A2_Decoracion";
    public const int Seed = 20261104;
    public const int RngOffset = 5000;   // decoracion (editor); ver Documentacion seccion 8

    private const string DecorDir = "Assets/AssetsLab/Decor";
    private const string TexDir = DecorDir + "/Textures";
    private const string TilePath = TexDir + "/Vereda_Baldosa.png";
    private const string AtlasPath = TexDir + "/Letreros_Atlas.png";
    private const string ParePath = TexDir + "/Senal_PARE.png";
    private const string TileMatPath = DecorDir + "/Vereda_Baldosa.mat";
    private const string SignMatPath = DecorDir + "/Decor_Letreros.mat";
    private const string OriginalSidewalkMat = "Packages/com.irt.osm2unity/Editor/Resources/Sidewalk.mat";
    private const string OriginalStopSprite = "Packages/com.irt.osm2unity/Editor/Resources/Stop.png";

    // Nombres de calles [VERIFICAR] con Bernardo: nombres tipicos de Providencia, la ciudad es ficticia
    private const string RingNorthName = "AV. ANDRÉS BELLO";
    private const string RingSouthName = "AV. PROVIDENCIA";
    private static readonly (float x, string name)[] SideStreets =
    {
        (-60f, "PEDRO DE VALDIVIA"), (4f, "RICARDO LYON"), (60f, "LOS LEONES"),
    };

    private const float CurbPaintEndsM = 8f;          // solera amarilla junto a las esquinas [VERIFICAR]
    private const float ClearWalkM = 1.8f;            // paso libre minimo para peatones detras del mobiliario
    private const float CrosswalkKeepOutM = 2.5f;     // mobiliario lejos de los pasos peatonales

    [MenuItem("SimBici/Decorate City")]
    private static void Menu() => Debug.Log(Decorate(true));

    [MenuItem("SimBici/Decorate City (quitar)")]
    private static void RemoveMenu() => Debug.Log(Remove());

    // ============================================================
    // Contexto de colocacion
    // ============================================================
    private class Ctx
    {
        public System.Random rng;
        public readonly List<GameObject> temp = new List<GameObject>();
        public readonly List<(Vector2 p, float r)> obstacles = new List<(Vector2, float)>();
        public Crosswalk[] crosswalks;
        public List<TrafficLane> lanes;
        public RingBikeLaneEditor.Ring ring;
        public bool hasRing;
        public const int TempLayer = 2;   // Ignore Raycast: solo lo golpean los rayos con esta mascara

        /// <summary>Colliders temporales de las veredas (no tienen collider propio) para medir su altura.</summary>
        public void Begin()
        {
            var sw = GameObject.Find("City/Sidewalks");
            if (sw != null)
                foreach (var mf in sw.GetComponentsInChildren<MeshFilter>())
                {
                    var go = new GameObject("TmpSidewalkCollider") { hideFlags = HideFlags.HideAndDontSave, layer = TempLayer };
                    go.transform.SetPositionAndRotation(mf.transform.position, mf.transform.rotation);
                    go.transform.localScale = mf.transform.lossyScale;
                    go.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                    temp.Add(go);
                }
            Physics.SyncTransforms();
        }

        public void End() { foreach (var g in temp) if (g != null) Object.DestroyImmediate(g); temp.Clear(); }

        /// <summary>Altura de la vereda bajo p (NaN si no hay vereda).</summary>
        public float SidewalkY(Vector3 p)
        {
            return Physics.Raycast(new Vector3(p.x, 5f, p.z), Vector3.down, out RaycastHit hit, 10f, 1 << TempLayer)
                ? hit.point.y : float.NaN;
        }

        public bool OnSidewalk(Vector3 p) { float y = SidewalkY(p); return !float.IsNaN(y) && y > 0.15f; }

        public bool NearCrosswalk(Vector3 p, float margin)
        {
            foreach (var cw in crosswalks)
            {
                Vector3 a = cw.EndA, b = cw.EndB; a.y = b.y = 0f;
                Vector3 d = b - a; float len = d.magnitude; if (len < 0.1f) continue; d /= len;
                Vector3 r = new Vector3(d.z, 0f, -d.x), q = new Vector3(p.x, 0f, p.z) - a;
                float u = Vector3.Dot(q, d), v = Vector3.Dot(q, r);
                if (u >= -margin && u <= len + margin && Mathf.Abs(v) <= cw.WidthM * 0.5f + margin) return true;
            }
            return false;
        }

        public bool Clear(Vector3 p, float r)
        {
            var q = new Vector2(p.x, p.z);
            foreach (var (o, ro) in obstacles) if ((o - q).sqrMagnitude < (r + ro) * (r + ro)) return false;
            return true;
        }

        public void Block(Vector3 p, float r) => obstacles.Add((new Vector2(p.x, p.z), r));

        /// <summary>
        /// Paso libre en la vereda detras de un objeto junto a la solera: desde 'from' hasta 'from + need' metros
        /// de p hacia la vereda (n) todo es vereda y no hay otros obstaculos.
        /// </summary>
        public bool ClearPath(Vector3 p, Vector3 n, float from, float need)
        {
            for (float d = from; d <= from + need + 0.01f; d += 0.3f)
            {
                Vector3 q = p + n * d;
                if (!OnSidewalk(q) || !Clear(q, 0.25f)) return false;
            }
            return true;
        }

        /// <summary>Lugar libre en la vereda para un objeto de radio r.</summary>
        public bool Free(Vector3 p, float r, float crosswalkMargin = CrosswalkKeepOutM)
        {
            if (!OnSidewalk(p) || NearCrosswalk(p, crosswalkMargin) || !Clear(p, r)) return false;
            for (int k = 0; k < 4; k++)
            {
                float a = k * Mathf.PI * 0.5f;
                if (!OnSidewalk(p + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r)) return false;
            }
            return true;
        }
    }

    // ============================================================
    // Mallas con color por vertice o con UV (se agrupan por celdas de 80 m)
    // ============================================================
    private class Geo
    {
        public readonly List<Vector3> v = new List<Vector3>();
        public readonly List<Color> c = new List<Color>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<int> t = new List<int>();

        public void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Vector3 outward, Color col,
                         Vector2 ua = default, Vector2 ub = default, Vector2 uc = default, Vector2 ud = default)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, cc - a), outward) < 0f) { (b, d) = (d, b); (ub, ud) = (ud, ub); }
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
            c.Add(col); c.Add(col); c.Add(col); c.Add(col);
            uv.Add(ua); uv.Add(ub); uv.Add(uc); uv.Add(ud);
            t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        public void Tri(Vector3 a, Vector3 b, Vector3 cc, Vector3 outward, Color col)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, cc - a), outward) < 0f) (b, cc) = (cc, b);
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(cc);
            c.Add(col); c.Add(col); c.Add(col);
            uv.Add(Vector2.zero); uv.Add(Vector2.zero); uv.Add(Vector2.zero);
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
        }

        /// <summary>Caja: centro, eje 'fwd' (largo), tamano (ancho, alto, largo).</summary>
        public void Box(Vector3 center, Vector3 fwd, Vector3 size, Color col, bool bottom = true)
        {
            fwd.y = 0f; fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            Vector3 R = new Vector3(fwd.z, 0f, -fwd.x) * (size.x * 0.5f), U = Vector3.up * (size.y * 0.5f), F = fwd * (size.z * 0.5f);
            Vector3 P(int i, int j, int k) => center + R * i + U * j + F * k;
            Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), Vector3.up, col);
            if (bottom) Quad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), Vector3.down, col);
            Quad(P(1, -1, -1), P(1, -1, 1), P(1, 1, 1), P(1, 1, -1), R, col);
            Quad(P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1), P(-1, -1, 1), -R, col);
            Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), F, col);
            Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), -F, col);
        }

        /// <summary>Cilindro desde 'start' a lo largo de 'axis'.</summary>
        public void Cylinder(Vector3 start, Vector3 axis, float radius, float length, int seg, Color col,
                             bool capStart, bool capEnd, Color? capColor = null)
        {
            axis.Normalize();
            Vector3 a = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 b = Vector3.Cross(axis, a);
            Vector3 end = start + axis * length;
            Color cap = capColor ?? col;
            for (int i = 0; i < seg; i++)
            {
                float t0 = i * 2f * Mathf.PI / seg, t1 = (i + 1) * 2f * Mathf.PI / seg;
                Vector3 r0 = (a * Mathf.Cos(t0) + b * Mathf.Sin(t0)) * radius, r1 = (a * Mathf.Cos(t1) + b * Mathf.Sin(t1)) * radius;
                Quad(start + r0, end + r0, end + r1, start + r1, r0 + r1, col);
                if (capEnd) Tri(end, end + r0, end + r1, axis, cap);
                if (capStart) Tri(start, start + r1, start + r0, -axis, cap);
            }
        }

        public Mesh ToMesh(string name, bool withUV)
        {
            var m = new Mesh { name = name };
            m.indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(v);
            m.SetColors(c);
            if (withUV) m.SetUVs(0, uv);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }

    private class Chunks
    {
        public readonly Dictionary<(int, int), Geo> cells = new Dictionary<(int, int), Geo>();
        public Geo At(Vector3 p)
        {
            var k = (Mathf.FloorToInt(p.x / 80f), Mathf.FloorToInt(p.z / 80f));
            if (!cells.TryGetValue(k, out var g)) cells[k] = g = new Geo();
            return g;
        }
    }

    private static Color VC(float r, float g, float b) => RingBikeLaneEditor.VC(r, g, b);

    // Paleta (color por vertice, lineal)
    private static readonly Color Yellow = VC(0.93f, 0.74f, 0.06f);
    private static readonly Color CurbYellow = VC(0.92f, 0.76f, 0.12f);
    private static readonly Color PoleDark = VC(0.10f, 0.10f, 0.11f);
    private static readonly Color SteelGray = VC(0.36f, 0.37f, 0.39f);
    private static readonly Color RoofGray = VC(0.48f, 0.49f, 0.50f);
    private static readonly Color RedAccent = VC(0.72f, 0.08f, 0.10f);
    private static readonly Color BinGreen = VC(0.09f, 0.22f, 0.15f);
    private static readonly Color Whitewash = VC(0.90f, 0.90f, 0.88f);
    private static readonly Color Soil = VC(0.22f, 0.16f, 0.11f);
    private static readonly Color Concrete = VC(0.58f, 0.58f, 0.56f);
    private static readonly Color Black = VC(0.05f, 0.05f, 0.05f);

    // ============================================================
    // Entrada
    // ============================================================
    public static string Decorate(bool rebuildPedestrians)
    {
        if (EditorApplication.isPlaying) return "[CityDecorator] Salir de Play antes de decorar.";
        var log = new StringBuilder("[CityDecorator]\n");
        log.AppendLine(Remove());

        RingBikeLaneEditor.EnsureFolder(TexDir);
        var ctx = new Ctx
        {
            rng = TrafficManager.CreateRng(Seed, RngOffset),
            crosswalks = Object.FindObjectsByType<Crosswalk>(FindObjectsSortMode.None)
                               .OrderBy(c => c.name, System.StringComparer.Ordinal).ToArray(),
            lanes = Object.FindObjectsByType<TrafficLane>(FindObjectsSortMode.None)
                          .Where(l => !l.name.Contains("Conn_"))
                          .OrderBy(l => l.name, System.StringComparer.Ordinal).ToList(),
        };
        ctx.hasRing = RingBikeLaneEditor.TryGetRing(out ctx.ring);
        ctx.Begin();
        try
        {
            CollectObstacles(ctx);
            log.AppendLine($"  Obstaculos existentes: {ctx.obstacles.Count}; pasos peatonales: {ctx.crosswalks.Length}; carriles: {ctx.lanes.Count}");

            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, RootName);
            var walk = Child(root.transform, "Veredas");        // obstaculos para el NavMesh de peatones
            var road = Child(root.transform, "Soleras");
            var far = Child(root.transform, "Fondo");
            var trees = Child(root.transform, "Arboles");   // solo adorno: los arboles no eran obstaculo para los peatones
            var mod = walk.gameObject.AddComponent<NavMeshModifier>();
            mod.overrideArea = true;
            mod.area = 1;  // Not Walkable

            var deco = new Chunks();
            var treeDeco = new Chunks();
            var signs = new Geo();
            var paint = new Chunks();

            log.AppendLine(ApplyPare());
            log.AppendLine(ApplySidewalkTiles());
            var atlas = new SignAtlas();
            log.AppendLine(Trees(ctx, treeDeco));
            var stops = BusStops(ctx, deco, signs, atlas, paint, log);
            log.AppendLine(StreetPlates(ctx, deco, signs, atlas));
            log.AppendLine(CurbPaint(ctx, paint, stops));
            log.AppendLine(Hydrants(ctx, deco));
            log.AppendLine(Bins(ctx, deco));

            var decorMat = RingBikeLaneEditor.DecorMaterial();
            foreach (var kv in deco.cells.OrderBy(k => k.Key))
                AddMesh(walk, $"Mobiliario_{kv.Key.Item1}_{kv.Key.Item2}", kv.Value.ToMesh($"Decor_Mobiliario_{kv.Key.Item1}_{kv.Key.Item2}", false), decorMat, true);
            foreach (var kv in treeDeco.cells.OrderBy(k => k.Key))
                AddMesh(trees, $"Arboles_{kv.Key.Item1}_{kv.Key.Item2}", kv.Value.ToMesh($"Decor_Arboles_{kv.Key.Item1}_{kv.Key.Item2}", false), decorMat, false);
            foreach (var kv in paint.cells.OrderBy(k => k.Key))
                AddMesh(road, $"Solera_amarilla_{kv.Key.Item1}_{kv.Key.Item2}", kv.Value.ToMesh($"Decor_Solera_{kv.Key.Item1}_{kv.Key.Item2}", false), decorMat, false);
            if (signs.v.Count > 0) AddMesh(walk, "Letreros", signs.ToMesh("Decor_Letreros", true), atlas.Material(), true);
            AddMesh(far, "Cordillera", BuildMountains(ctx.rng), decorMat, false);
            log.AppendLine(ApplyFog());

            foreach (var t in root.GetComponentsInChildren<Transform>()) GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
            SaveMeshes(root);
            EditorSceneManager.MarkSceneDirty(root.scene);
        }
        finally { ctx.End(); }

        if (rebuildPedestrians) log.AppendLine(PedestrianSetupEditor.Build());
        return log.ToString();
    }

    /// <summary>Quita la decoracion y deja la escena como antes (veredas, senales PARE, bruma).</summary>
    public static string Remove()
    {
        var log = new StringBuilder();
        var old = GameObject.Find(RootName);
        if (old != null) { Undo.DestroyObjectImmediate(old); log.Append("  Decoracion anterior borrada. "); }

        var orig = AssetDatabase.LoadAssetAtPath<Material>(OriginalSidewalkMat);
        int nMat = 0;
        if (orig != null)
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var mats = r.sharedMaterials; bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && mats[i].name == "Vereda_Baldosa") { mats[i] = orig; changed = true; nMat++; }
                if (changed) { Undo.RecordObject(r, "Vereda"); r.sharedMaterials = mats; }
            }

        var stop = AssetDatabase.LoadAllAssetsAtPath(OriginalStopSprite).OfType<Sprite>().FirstOrDefault();
        int nSpr = 0;
        if (stop != null)
            foreach (var s in Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (s.sprite != null && s.sprite.name == "Senal_PARE") { Undo.RecordObject(s, "PARE"); s.sprite = stop; nSpr++; }

        RenderSettings.fog = false;
        log.Append($"Restaurados: {nMat} materiales de vereda, {nSpr} senales.");
        return log.ToString();
    }

    private static Transform Child(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    private static void AddMesh(Transform parent, string name, Mesh mesh, Material mat, bool shadows)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    /// <summary>Guarda las mallas generadas como assets (reemplaza las de una corrida anterior).</summary>
    private static void SaveMeshes(GameObject root)
    {
        string dir = DecorDir + "/Generated";
        RingBikeLaneEditor.EnsureFolder(dir);
        foreach (var guid in AssetDatabase.FindAssets("Decor_ t:Mesh", new[] { dir }))
            AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
            AssetDatabase.CreateAsset(mf.sharedMesh, $"{dir}/{mf.sharedMesh.name}.asset");
        AssetDatabase.SaveAssets();
    }

    // ============================================================
    // Obstaculos existentes (arboles, faroles, postes de senales y semaforos)
    // ============================================================
    private static void CollectObstacles(Ctx ctx)
    {
        foreach (var (path, r) in new[] { ("City/Trees", 0.8f), ("City/Street Lights", 0.45f) })
        {
            var g = GameObject.Find(path);
            if (g == null) continue;
            foreach (Transform t in g.transform) ctx.Block(t.position, r);
        }
        foreach (var path in new[] { "City/Traffic Signs", "Signals", "Cruces" })
        {
            var g = GameObject.Find(path);
            if (g == null) continue;
            foreach (var rend in g.GetComponentsInChildren<Renderer>())
            {
                var b = rend.bounds;
                if (b.min.y < 0.6f && b.size.y > 1.2f && b.size.x < 1.2f && b.size.z < 1.2f)
                    ctx.Block(b.center, Mathf.Max(b.extents.x, b.extents.z) + 0.35f);
            }
        }
    }

    // ============================================================
    // Geometria de carriles y soleras (en editor se usa el spline directo)
    // ============================================================
    private static float LaneLength(TrafficLane l) => l.GetComponent<SplineContainer>().Spline.GetLength();

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

    private static bool OnRoad(Vector3 p) =>
        Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 10f) &&
        hit.collider.gameObject.layer == LayerMask.NameToLayer("Ground") && !(hit.collider is TerrainCollider);

    /// <summary>
    /// Solera a la derecha del carril en s: cara de la solera (a nivel de calzada), normal hacia la vereda
    /// y altura de la vereda. False si a la derecha no hay vereda con solera (boca de calle, rebaje).
    /// </summary>
    private static bool CurbAt(Ctx ctx, TrafficLane l, float s, out Vector3 face, out Vector3 n, out Vector3 fwd, out float top)
    {
        face = default; top = 0f;
        Vector3 p = LanePoint(l, s, out fwd);
        n = new Vector3(fwd.z, 0f, -fwd.x);
        if (!OnRoad(p)) return false;
        float x = 0f;
        while (x < 12f && OnRoad(p + n * x)) x += 0.05f;
        if (x >= 12f) return false;
        Vector3 edge = p + n * x;
        // Cara de la solera: primer punto (cada 2 cm desde 0.3 m antes del borde de la calzada) con vereda debajo
        for (float d = -0.3f; d <= 0.6f; d += 0.02f)
        {
            if (float.IsNaN(ctx.SidewalkY(edge + n * d))) continue;
            face = new Vector3(edge.x, 0f, edge.z) + n * d;
            top = ctx.SidewalkY(face + n * 0.1f);
            return !float.IsNaN(top) && top > 0.15f;
        }
        return false;
    }

    // ============================================================
    // PARE y veredas
    // ============================================================
    private static string ApplyPare()
    {
        var sprite = MakePareSprite();
        int n = 0;
        foreach (var s in Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (s.sprite != null && s.sprite.name == "Stop") { Undo.RecordObject(s, "PARE"); s.sprite = sprite; n++; }
        return $"  Senales PARE: {n} (reemplazan a STOP)";
    }

    private static string ApplySidewalkTiles()
    {
        if (!File.Exists(TilePath)) WriteTexture(TilePath, TileTexture(), true, false);
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TilePath);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(TileMatPath);
        if (mat == null)
        {
            mat = new Material(Shader.Find("SimBici/WorldTile")) { name = "Vereda_Baldosa", enableInstancing = true };
            AssetDatabase.CreateAsset(mat, TileMatPath);
        }
        mat.SetTexture("_MainTex", tex);
        mat.SetFloat("_TileWorldM", 2.0f);  // 5 x 5 baldosas de 40 cm por repeticion
        EditorUtility.SetDirty(mat);

        int n = 0;
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            var mats = r.sharedMaterials; bool changed = false;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null && mats[i].name == "Sidewalk") { mats[i] = mat; changed = true; }
            if (changed) { Undo.RecordObject(r, "Vereda"); r.sharedMaterials = mats; n++; }
        }
        return $"  Veredas con baldosa 40 x 40: {n}";
    }

    // ============================================================
    // Arboles: base encalada y alcorque
    // ============================================================
    private static string Trees(Ctx ctx, Chunks deco)
    {
        var g = GameObject.Find("City/Trees");
        if (g == null) return "  AVISO: no hay City/Trees";
        int nTrees = 0, nPits = 0;
        foreach (Transform tree in g.transform)
        {
            Vector3 basePos = tree.position;
            float r = TrunkRadius(tree);
            float y0 = ctx.SidewalkY(basePos);
            bool onWalk = !float.IsNaN(y0) && y0 > 0.15f;
            float gy = onWalk ? y0 : basePos.y;
            var geo = deco.At(basePos);
            geo.Cylinder(new Vector3(basePos.x, gy - 0.02f, basePos.z), Vector3.up, r + 0.015f, 1.05f, 12, Whitewash, false, false);
            nTrees++;
            if (!onWalk) continue;
            // Alcorque de tierra de 1.2 m con borde de hormigon
            Vector3 fwd = tree.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            float h = 0.6f, yS = y0 + 0.004f;
            Vector3 c = new Vector3(basePos.x, yS, basePos.z);
            geo.Quad(c + (-right - fwd) * h, c + (-right + fwd) * h, c + (right + fwd) * h, c + (right - fwd) * h, Vector3.up, Soil);
            foreach (var (d, side) in new[] { (fwd, right), (-fwd, right), (right, fwd), (-right, fwd) })
                geo.Box(c + d * (h - 0.03f) + Vector3.up * 0.012f, side, new Vector3(0.06f, 0.025f, 2f * h), Concrete, false);
            nPits++;
        }
        return $"  Arboles: {nTrees} troncos encalados, {nPits} alcorques";
    }

    /// <summary>Radio del tronco: vertices entre 0.3 y 0.9 m de altura cerca del pie del arbol (percentil 80).</summary>
    private static float TrunkRadius(Transform tree)
    {
        var d = new List<float>();
        Vector2 c = new Vector2(tree.position.x, tree.position.z);
        foreach (var mf in tree.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
            var m = mf.transform.localToWorldMatrix;
            foreach (var v in mf.sharedMesh.vertices)
            {
                Vector3 w = m.MultiplyPoint3x4(v);
                float dy = w.y - tree.position.y;
                if (dy < 0.3f || dy > 0.9f) continue;
                float r = (new Vector2(w.x, w.z) - c).magnitude;
                if (r < 0.6f) d.Add(r);
            }
        }
        if (d.Count < 4) return 0.15f;
        d.Sort();
        return Mathf.Clamp(d[(int)(d.Count * 0.8f)], 0.08f, 0.4f);
    }

    // ============================================================
    // Paraderos
    // ============================================================
    private struct StopSite { public TrafficLane lane; public float s; }

    private static List<StopSite> BusStops(Ctx ctx, Chunks deco, Geo signs, SignAtlas atlas, Chunks paint, StringBuilder log)
    {
        var sites = new List<StopSite>();
        if (!ctx.hasRing) { log.AppendLine("  AVISO: sin anillo, no hay paraderos"); return sites; }
        var ring = ctx.ring;
        float inner = ring.R0 - ring.HalfWidth, outer = ring.R0 + ring.HalfWidth;
        // Objetivos (x, z de la solera): dos en la berma interior y dos en el borde exterior
        var targets = new[]
        {
            new Vector3(ring.cx + 32f, 0f, ring.cz + inner), new Vector3(ring.cx - 28f, 0f, ring.cz - inner),
            new Vector3(ring.cx - 28f, 0f, ring.cz + outer), new Vector3(ring.cx + 90f, 0f, ring.cz - outer),
        };
        foreach (var target in targets)
        {
            if (!FindCurbNear(ctx, target, out TrafficLane lane, out float s0)) { log.AppendLine($"  AVISO: no se encontro solera para el paradero en {target:F0}"); continue; }
            bool placed = false;
            foreach (float ds in new[] { 0f, 3f, -3f, 6f, -6f, 9f, -9f, 12f, -12f })
            {
                float s = s0 + ds;
                if (!ShelterFits(ctx, lane, s)) continue;
                BuildShelter(ctx, deco, signs, atlas, lane, s);
                sites.Add(new StopSite { lane = lane, s = s });
                placed = true;
                break;
            }
            if (!placed) log.AppendLine($"  AVISO: no hubo espacio libre para el paradero cerca de {target:F0}");
        }
        log.AppendLine($"  Paraderos: {sites.Count} ({string.Join(", ", sites.Select(x => $"{x.lane.name} s={x.s:F0}"))})");
        return sites;
    }

    private static bool FindCurbNear(Ctx ctx, Vector3 target, out TrafficLane best, out float bestS)
    {
        best = null; bestS = 0f; float bestD = 2.5f;
        foreach (var l in ctx.lanes)
        {
            float len = LaneLength(l);
            for (float s = 0f; s <= len; s += 1f)
            {
                Vector3 p = LanePoint(l, s, out _);
                if ((new Vector2(p.x - target.x, p.z - target.z)).magnitude > 8f) continue;
                if (!CurbAt(ctx, l, s, out Vector3 face, out _, out _, out _)) continue;
                float d = (new Vector2(face.x - target.x, face.z - target.z)).magnitude;
                if (d < bestD) { bestD = d; best = l; bestS = s; }
            }
        }
        return best != null;
    }

    private const float ShelterLengthM = 4.0f, ShelterDepthM = 1.4f, ShelterSetbackM = 0.6f;

    private static bool ShelterFits(Ctx ctx, TrafficLane lane, float s)
    {
        float len = LaneLength(lane);
        if (s < 8f || s > len - 8f) return false;
        for (float ds = -ShelterLengthM * 0.5f; ds <= ShelterLengthM * 0.5f + 0.01f; ds += 1f)
        {
            if (!CurbAt(ctx, lane, s + ds, out Vector3 face, out Vector3 n, out _, out _)) return false;
            foreach (float back in new[] { ShelterSetbackM, ShelterSetbackM + ShelterDepthM })
                if (!ctx.Free(face + n * back, 0.35f)) return false;
            if (!ctx.ClearPath(face, n, ShelterSetbackM + ShelterDepthM + 0.3f, ClearWalkM)) return false;
        }
        // Senal de parada 3 m antes
        return CurbAt(ctx, lane, s - 3f, out Vector3 f2, out Vector3 n2, out _, out _) && ctx.Free(f2 + n2 * 0.45f, 0.3f);
    }

    private static void BuildShelter(Ctx ctx, Chunks deco, Geo signs, SignAtlas atlas, TrafficLane lane, float s)
    {
        CurbAt(ctx, lane, s, out Vector3 face, out Vector3 n, out Vector3 t, out float y0);
        Vector3 c = face + n * (ShelterSetbackM + ShelterDepthM * 0.5f);
        c.y = y0;
        var g = deco.At(c);
        float halfL = ShelterLengthM * 0.5f, halfD = ShelterDepthM * 0.5f;
        Vector3 back = c + n * (halfD - 0.08f);

        // Postes traseros, techo con faja roja, barras del respaldo, banca y paneles de los extremos
        foreach (float a in new[] { -halfL + 0.1f, 0f, halfL - 0.1f })
            g.Box(back + t * a + Vector3.up * 1.27f, t, new Vector3(0.08f, 2.54f, 0.08f), SteelGray);
        g.Box(c + n * 0.05f + Vector3.up * 2.58f, t, new Vector3(ShelterDepthM + 0.3f, 0.08f, ShelterLengthM + 0.3f), RoofGray);
        g.Box(c - n * (halfD + 0.12f) + Vector3.up * 2.55f, t, new Vector3(0.05f, 0.20f, ShelterLengthM + 0.3f), RedAccent);
        foreach (float h in new[] { 0.35f, 2.15f })
            g.Box(back + Vector3.up * h, t, new Vector3(0.05f, 0.05f, ShelterLengthM - 0.2f), SteelGray);
        Vector3 seat = back - n * 0.28f + Vector3.up * 0.45f;
        g.Box(seat, t, new Vector3(0.40f, 0.05f, 2.4f), SteelGray);
        foreach (float a in new[] { -1.0f, 1.0f })
            g.Box(back - n * 0.28f + t * a + Vector3.up * 0.21f, t, new Vector3(0.35f, 0.42f, 0.05f), SteelGray);

        Rect ad = atlas.Slot(SignAtlas.AdPanel);
        foreach (float a in new[] { -halfL + 0.05f, halfL - 0.05f })
        {
            Vector3 pc = c + t * a + n * 0.05f + Vector3.up * 1.25f;
            g.Box(pc, n, new Vector3(0.10f, 1.85f, 1.25f), SteelGray);  // marco (eje largo a lo ancho del refugio)
            foreach (float side in new[] { -1f, 1f })
                TexQuad(signs, pc + t * (side * 0.052f), t * side, 1.1f, 1.7f, ad);
        }
        ctx.Block(c, Mathf.Max(halfL, halfD) + 0.2f);

        // Senal de parada: placa oscura en poste, de frente al trafico que llega
        CurbAt(ctx, lane, s - 3f, out Vector3 f2, out Vector3 n2, out Vector3 t2, out float y2);
        Vector3 pole = f2 + n2 * 0.45f; pole.y = y2;
        deco.At(pole).Cylinder(pole, Vector3.up, 0.04f, 3.0f, 10, PoleDark, false, true);
        Vector3 plateC = pole + Vector3.up * 2.35f + n2 * 0.0f;
        deco.At(pole).Box(plateC, n2, new Vector3(0.04f, 0.78f, 0.52f), PoleDark);
        Rect sr = atlas.Slot(SignAtlas.BusStopSign);
        foreach (float side in new[] { -1f, 1f })
            TexQuad(signs, plateC + t2 * (side * 0.021f), t2 * side, 0.50f, 0.76f, sr);
        ctx.Block(pole, 0.3f);
    }

    /// <summary>Quad vertical con una casilla del atlas, visible desde el lado de 'normal' (la imagen queda derecha para ese observador).</summary>
    private static void TexQuad(Geo g, Vector3 center, Vector3 normal, float w, float h, Rect uv)
    {
        normal.y = 0f; normal.Normalize();
        // Derecha del observador que mira la cara (mira en -normal)
        Vector3 right = Vector3.Cross(Vector3.up, -normal).normalized;
        Vector3 R = right * (w * 0.5f), U = Vector3.up * (h * 0.5f);
        g.Quad(center - R - U, center - R + U, center + R + U, center + R - U, normal, Color.white,
               new Vector2(uv.xMin, uv.yMin), new Vector2(uv.xMin, uv.yMax), new Vector2(uv.xMax, uv.yMax), new Vector2(uv.xMax, uv.yMin));
    }

    // ============================================================
    // Placas de calle
    // ============================================================
    private static string StreetPlates(Ctx ctx, Chunks deco, Geo signs, SignAtlas atlas)
    {
        // Centros de interseccion: promedio de sus conectores (I_XX_*)
        var groups = Object.FindObjectsByType<TrafficLane>(FindObjectsSortMode.None)
                           .Where(l => l.name.StartsWith("I_") && l.name.Contains("Conn_"))
                           .GroupBy(l => l.name.Substring(0, 4))
                           .OrderBy(gr => gr.Key, System.StringComparer.Ordinal);
        float hwRing = ctx.hasRing ? ctx.ring.HalfWidth : 6.25f, hwSide = 4.25f;
        int plates = 0, poles = 0;
        foreach (var gr in groups)
        {
            Vector3 c = Vector3.zero; int n = 0;
            foreach (var l in gr) { c += LanePoint(l, LaneLength(l) * 0.5f, out _); n++; }
            c /= n; c.y = 0f;
            string ring = c.z > (ctx.hasRing ? ctx.ring.cz : 0f) ? RingNorthName : RingSouthName;
            string side = SideStreets.OrderBy(sv => Mathf.Abs(sv.x - c.x)).First().name;
            int sideDir = SideStreetDirection(ctx, c);

            int placedHere = 0;
            foreach (var (sx, sz) in new[] { (1, 1), (-1, -1), (1, -1), (-1, 1) })
            {
                if (placedHere >= 2) break;
                if (!CornerSpot(ctx, c, sx, sz, hwSide, hwRing, out Vector3 p)) continue;
                float y = ctx.SidewalkY(p);
                p.y = y;
                var g = deco.At(p);
                g.Cylinder(p, Vector3.up, 0.04f, 2.95f, 10, PoleDark, false, true);
                // Placa de la avenida (paralela al eje x) arriba; la de la calle transversal (paralela a z) abajo
                Plate(g, signs, atlas, p + Vector3.up * 2.72f, Vector3.right, ring, 0);
                Plate(g, signs, atlas, p + Vector3.up * 2.45f, Vector3.forward, side, sideDir);
                ctx.Block(p, 0.3f);
                placedHere++; poles++; plates += 2;
            }
        }
        return $"  Placas de calle: {plates} en {poles} postes";
    }

    /// <summary>Sentido de la calle transversal en la interseccion: +1 hacia +z, -1 hacia -z, 0 doble sentido.</summary>
    private static int SideStreetDirection(Ctx ctx, Vector3 c)
    {
        bool pos = false, neg = false;
        foreach (var l in ctx.lanes)
        {
            Vector3 p = LanePoint(l, LaneLength(l) * 0.5f, out Vector3 f);
            if (Mathf.Abs(p.x - c.x) > 6f || Mathf.Abs(p.z - c.z) > 45f || Mathf.Abs(f.z) < 0.9f) continue;
            if (f.z > 0f) pos = true; else neg = true;
        }
        return pos && !neg ? 1 : neg && !pos ? -1 : 0;
    }

    private static bool CornerSpot(Ctx ctx, Vector3 c, int sx, int sz, float hwX, float hwZ, out Vector3 spot)
    {
        spot = default;
        // Por la diagonal de la esquina: primer punto con vereda, y 0.8 m mas adentro
        Vector3 dir = new Vector3(sx * hwX, 0f, sz * hwZ).normalized;
        for (float a = 0f; a < 16f; a += 0.2f)
        {
            Vector3 q = c + new Vector3(sx * hwX, 0f, sz * hwZ) + dir * a;
            if (!ctx.OnSidewalk(q)) continue;
            spot = q + dir * 0.8f;
            return ctx.Free(spot, 0.3f, 0.5f);
        }
        return false;
    }

    /// <summary>Placa doble faz 1.0 x 0.22 m. dir = sentido de transito en el mundo (0 = doble sentido).</summary>
    private static void Plate(Geo g, Geo signs, SignAtlas atlas, Vector3 center, Vector3 streetAxis, string name, int dir)
    {
        Vector3 axis = streetAxis.normalized;
        Vector3 n = new Vector3(axis.z, 0f, -axis.x);
        g.Box(center, axis, new Vector3(0.025f, 0.24f, 1.02f), Black);
        foreach (float side in new[] { 1f, -1f })
        {
            Vector3 normal = n * side;
            Vector3 viewerRight = Vector3.Cross(Vector3.up, -normal).normalized;
            int arrow = 0;
            if (dir != 0)
            {
                Vector3 travel = new Vector3(0f, 0f, dir);
                arrow = Vector3.Dot(travel, viewerRight) > 0f ? 1 : -1;
            }
            TexQuad(signs, center + normal * 0.0135f, normal, 1.0f, 0.22f, atlas.Slot(SignAtlas.PlateKey(name, arrow)));
        }
    }

    // ============================================================
    // Soleras amarillas
    // ============================================================
    private static string CurbPaint(Ctx ctx, Chunks paint, List<StopSite> stops)
    {
        float total = 0f;
        foreach (var l in ctx.lanes)
        {
            float len = LaneLength(l);
            if (len < 4f) continue;
            var strip = new List<(Vector3 face, Vector3 n, float top)>();
            for (float s = 0.25f; s <= len - 0.25f; s += 0.5f)
            {
                bool want = s < CurbPaintEndsM || s > len - CurbPaintEndsM || stops.Any(st => st.lane == l && Mathf.Abs(st.s - s) <= 6f);
                bool ok = false;
                if (CurbAt(ctx, l, s, out Vector3 face, out Vector3 n, out _, out float top))
                {
                    if (!want && ctx.hasRing)
                    {
                        float o = ctx.ring.Offset(face);   // berma interior del anillo
                        want = o < -(ctx.ring.HalfWidth - 0.6f) && o > -(ctx.ring.HalfWidth + 0.8f);
                    }
                    if (want && !ctx.NearCrosswalk(face, 0.3f)) { strip.Add((face, n, top)); ok = true; }
                }
                if (!ok) { total += EmitCurbStrip(paint, strip); strip.Clear(); }
            }
            total += EmitCurbStrip(paint, strip);
        }
        return $"  Soleras amarillas: {total:F0} m";
    }

    private static float EmitCurbStrip(Chunks paint, List<(Vector3 face, Vector3 n, float top)> pts)
    {
        if (pts.Count < 2) return 0f;
        float len = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            var (a, na, ta) = pts[i - 1];
            var (b, nb, tb) = pts[i];
            if ((b - a).magnitude > 1.2f) continue;
            len += (b - a).magnitude;
            var g = paint.At(a);
            Vector3 a0 = a - na * 0.008f, b0 = b - nb * 0.008f;
            g.Quad(a0 + Vector3.up * 0.003f, a0 + Vector3.up * (ta + 0.002f), b0 + Vector3.up * (tb + 0.002f), b0 + Vector3.up * 0.003f, -(na + nb), CurbYellow);
            g.Quad(a0 + Vector3.up * (ta + 0.004f), a + na * 0.14f + Vector3.up * (ta + 0.004f),
                   b + nb * 0.14f + Vector3.up * (tb + 0.004f), b0 + Vector3.up * (tb + 0.004f), Vector3.up, CurbYellow);
        }
        return len;
    }

    // ============================================================
    // Grifos y basureros
    // ============================================================
    private static string Hydrants(Ctx ctx, Chunks deco)
    {
        int n = 0;
        foreach (var l in ctx.lanes)
        {
            float len = LaneLength(l);
            if (len < 25f) continue;
            if (ctx.rng.NextDouble() > 0.6) continue;
            for (float s = 6f; s <= 12f; s += 1f)
            {
                if (!CurbAt(ctx, l, s, out Vector3 face, out Vector3 nrm, out Vector3 fwd, out float top)) continue;
                Vector3 p = face + nrm * 0.45f;
                if (!ctx.Free(p, 0.3f) || !ctx.ClearPath(p, nrm, 0.35f, ClearWalkM)) continue;
                p.y = top;
                Hydrant(deco.At(p), p, -nrm);
                ctx.Block(p, 0.35f);
                n++;
                break;
            }
        }
        return $"  Grifos: {n}";
    }

    private static void Hydrant(Geo g, Vector3 p, Vector3 towardRoad)
    {
        Color dark = VC(0.70f, 0.55f, 0.04f);
        g.Cylinder(p, Vector3.up, 0.16f, 0.06f, 12, dark, false, true);
        g.Cylinder(p + Vector3.up * 0.06f, Vector3.up, 0.11f, 0.52f, 12, Yellow, false, false);
        g.Cylinder(p + Vector3.up * 0.58f, Vector3.up, 0.13f, 0.05f, 12, Yellow, true, true);
        g.Cylinder(p + Vector3.up * 0.63f, Vector3.up, 0.06f, 0.08f, 8, Yellow, false, true);
        Vector3 side = new Vector3(towardRoad.z, 0f, -towardRoad.x);
        g.Cylinder(p + Vector3.up * 0.42f, towardRoad, 0.06f, 0.17f, 10, Yellow, false, true);
        g.Cylinder(p + Vector3.up * 0.40f, side, 0.04f, 0.15f, 8, Yellow, false, true);
        g.Cylinder(p + Vector3.up * 0.40f, -side, 0.04f, 0.15f, 8, Yellow, false, true);
    }

    private static string Bins(Ctx ctx, Chunks deco)
    {
        int n = 0;
        foreach (var l in ctx.lanes)
        {
            float len = LaneLength(l);
            if (len < 20f) continue;
            for (float s = 16f + (float)ctx.rng.NextDouble() * 10f; s <= len - 10f; s += 50f + (float)ctx.rng.NextDouble() * 15f)
            {
                for (float ds = 0f; ds <= 6f; ds += 1.5f)
                {
                    if (!CurbAt(ctx, l, s + ds, out Vector3 face, out Vector3 nrm, out Vector3 fwd, out float top)) continue;
                    Vector3 p = face + nrm * 0.55f;
                    if (!ctx.Free(p, 0.35f) || !ctx.ClearPath(p + nrm * 0.23f, nrm, 0.3f, ClearWalkM)) continue;
                    p.y = top;
                    Bin(deco.At(p), p, nrm, fwd);
                    ctx.Block(p, 0.4f);
                    n++;
                    break;
                }
            }
        }
        return $"  Basureros: {n}";
    }

    private static void Bin(Geo g, Vector3 p, Vector3 towardWalk, Vector3 fwd)
    {
        g.Box(p + Vector3.up * 0.55f, fwd, new Vector3(0.06f, 1.1f, 0.06f), BinGreen);
        Vector3 c = p + towardWalk * 0.23f + Vector3.up * 0.38f;
        g.Cylinder(c, Vector3.up, 0.2f, 0.5f, 14, BinGreen, true, false);
        g.Cylinder(c + Vector3.up * 0.5f, Vector3.up, 0.215f, 0.04f, 14, VC(0.14f, 0.30f, 0.20f), false, false);
        g.Cylinder(c + Vector3.up * 0.46f, Vector3.up, 0.19f, 0.001f, 14, Black, false, true);
    }

    // ============================================================
    // Cordillera y bruma
    // ============================================================
    private static Mesh BuildMountains(System.Random rng)
    {
        // Radios < 790 m: con la camara a 1000 m de plano lejano se ve entera desde cualquier punto de la ciudad
        float[] radii = { 560f, 610f, 650f, 690f, 720f, 750f, 775f, 790f };
        const int nA = 576;
        var ph = new float[8];
        for (int i = 0; i < ph.Length; i++) ph[i] = (float)(rng.NextDouble() * Mathf.PI * 2.0);
        // Ruido "de crestas" (1 - |sen|)^2: cumbres agudas y quebradas anchas. Cada anillo usa fases corridas,
        // asi se ven varias lineas de cerros en profundidad y no un muro parejo
        float Ridge(float th, int ring)
        {
            float s = 0f, w = 0f;
            int[] k = { 5, 11, 19, 31, 53, 89, 131 };
            float[] amp = { 0.30f, 0.24f, 0.18f, 0.12f, 0.08f, 0.05f, 0.03f };
            for (int i = 0; i < k.Length; i++)
            {
                float r = 1f - Mathf.Abs(Mathf.Sin(k[i] * th * 0.5f + ph[i % ph.Length] + ring * 0.29f * (i + 1)));
                s += amp[i] * Mathf.Pow(r, 1.6f); w += amp[i];
            }
            return Mathf.Clamp01((s / w - 0.12f) / 0.6f);
        }
        float Envelope(float th)
        {
            float east = Mathf.Pow(Mathf.Clamp01((Mathf.Cos(th) - 0.15f) / 0.85f), 0.7f);   // Andes al oriente (+x)
            float west = Mathf.Clamp01((-Mathf.Cos(th) - 0.4f) / 0.6f);                      // cordillera de la Costa
            // Cumbres de ~7-10 grados sobre el horizonte a 600-750 m, como los Andes vistos desde Providencia
            return 16f + 150f * east + 34f * west;
        }
        var g = new Geo();
        var grid = new Vector3[nA + 1, radii.Length];
        var col = new Color[nA + 1, radii.Length];
        for (int i = 0; i <= nA; i++)
        {
            float th = i * 2f * Mathf.PI / nA;
            float env = Envelope(th);
            for (int j = 0; j < radii.Length; j++)
            {
                float r = radii[j];
                float ridge = Ridge(th, j);
                float prof = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(560f, 720f, r)) * (r > 760f ? Mathf.Lerp(1f, 0.82f, Mathf.InverseLerp(760f, 790f, r)) : 1f);
                float h = env * (0.18f + 0.82f * ridge) * prof - 0.2f;
                grid[i, j] = new Vector3(Mathf.Cos(th) * r, h, Mathf.Sin(th) * r);
                float snow = Mathf.InverseLerp(0.50f, 0.64f, h / env) * Mathf.InverseLerp(45f, 70f, h);
                Color rock = Color.Lerp(new Color(0.36f, 0.35f, 0.28f), new Color(0.44f, 0.40f, 0.37f), Mathf.InverseLerp(10f, 50f, h));
                Color cc = Color.Lerp(rock, new Color(0.93f, 0.94f, 0.96f), snow).linear; cc.a = 0f;
                col[i, j] = cc;
            }
        }
        for (int i = 0; i < nA; i++)
            for (int j = 0; j < radii.Length - 1; j++)
            {
                int idx = g.v.Count;
                g.v.Add(grid[i, j]); g.v.Add(grid[i, j + 1]); g.v.Add(grid[i + 1, j + 1]); g.v.Add(grid[i + 1, j]);
                g.c.Add(col[i, j]); g.c.Add(col[i, j + 1]); g.c.Add(col[i + 1, j + 1]); g.c.Add(col[i + 1, j]);
                for (int k = 0; k < 4; k++) g.uv.Add(Vector2.zero);
                // Cara hacia arriba: (i,j) -> (i,j+1) -> (i+1,j+1) visto desde arriba
                Vector3 nrm = Vector3.Cross(grid[i, j + 1] - grid[i, j], grid[i + 1, j + 1] - grid[i, j]);
                if (nrm.y >= 0f) { g.t.Add(idx); g.t.Add(idx + 1); g.t.Add(idx + 2); g.t.Add(idx); g.t.Add(idx + 2); g.t.Add(idx + 3); }
                else { g.t.Add(idx); g.t.Add(idx + 2); g.t.Add(idx + 1); g.t.Add(idx); g.t.Add(idx + 3); g.t.Add(idx + 2); }
            }
        return g.ToMesh("Decor_Cordillera", false);
    }

    private static string ApplyFog()
    {
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = 0.0005f;   // [VERIFICAR] en el visor: ~95 % de visibilidad a 100 m, cordillera a ~70 %
        RenderSettings.fogColor = new Color(0.74f, 0.76f, 0.79f);
        return $"  Bruma: exponencial, densidad {RenderSettings.fogDensity}";
    }

    // ============================================================
    // Texturas
    // ============================================================
    private static void WriteTexture(string path, Texture2D tex, bool repeat, bool sprite)
    {
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var imp = (TextureImporter)AssetImporter.GetAtPath(path);
        imp.textureType = sprite ? TextureImporterType.Sprite : TextureImporterType.Default;
        if (sprite) { imp.spriteImportMode = SpriteImportMode.Single; imp.spritePixelsPerUnit = 100f; imp.alphaIsTransparency = true; }
        imp.sRGBTexture = true;
        imp.mipmapEnabled = true;
        imp.anisoLevel = 8;
        imp.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        imp.maxTextureSize = 2048;
        imp.textureCompression = TextureImporterCompression.CompressedHQ;
        imp.SaveAndReimport();
    }

    /// <summary>Baldosa microvibrada gris de 40 x 40 cm (5 x 5 por repeticion de 2 m), con 4 x 4 pastillas.</summary>
    private static Texture2D TileTexture()
    {
        const int tilePx = 256, nTiles = 5, size = tilePx * nTiles;
        var rnd = new System.Random(Seed + 1);
        var px = new Color32[size * size];
        for (int ty = 0; ty < nTiles; ty++)
            for (int tx = 0; tx < nTiles; tx++)
            {
                float tone = 0.60f + (float)(rnd.NextDouble() - 0.5) * 0.06f;
                for (int y = 0; y < tilePx; y++)
                    for (int x = 0; x < tilePx; x++)
                    {
                        float v = tone;
                        bool joint = x < 4 || y < 4;
                        int sx = (x - 4) % 63, sy = (y - 4) % 63;   // pastillas de ~10 cm
                        bool padEdge = !joint && (sx < 6 || sy < 6);
                        if (joint) v = 0.36f;
                        else if (padEdge) v = tone - 0.07f;
                        v += (float)(rnd.NextDouble() - 0.5) * 0.035f;
                        byte b = (byte)Mathf.Clamp(v * 255f, 0, 255);
                        byte bb = (byte)Mathf.Clamp(v * 0.97f * 255f, 0, 255);
                        px[(ty * tilePx + y) * size + tx * tilePx + x] = new Color32(b, b, bb, 255);
                    }
            }
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    private static Sprite MakePareSprite()
    {
        if (!File.Exists(ParePath))
        {
            const int S = 900;
            var bg = new Texture2D(S, S, TextureFormat.RGBA32, false);
            var px = new Color32[S * S];
            Color32 white = new Color32(245, 245, 245, 255), red = new Color32(196, 18, 28, 255);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = Mathf.Abs(x - S * 0.5f + 0.5f), dy = Mathf.Abs(y - S * 0.5f + 0.5f);
                    float oct = Mathf.Max(Mathf.Max(dx, dy), (dx + dy) * 0.7071f);   // distancia "octogonal"
                    float rOut = S * 0.5f - 2f;
                    px[y * S + x] = oct > rOut ? new Color32(0, 0, 0, 0) : oct > rOut - 34f ? white : red;
                }
            bg.SetPixels32(px); bg.Apply();
            var tex = TextRender.Render(bg, new[] { new TextRender.Label("PARE", new Vector2(0.5f, 0.5f), 0.36f, 0.80f, Color.white) });
            // Conservar el contorno transparente del octogono
            var outPx = tex.GetPixels32();
            for (int i = 0; i < outPx.Length; i++) outPx[i].a = px[i].a;
            tex.SetPixels32(outPx); tex.Apply();
            Object.DestroyImmediate(bg);
            WriteTexture(ParePath, tex, false, true);
        }
        var sprite = AssetDatabase.LoadAllAssetsAtPath(ParePath).OfType<Sprite>().First();
        sprite.name = "Senal_PARE";
        return sprite;
    }

    /// <summary>Atlas de placas de calle, senal de parada y panel publicitario generico (sin marcas).</summary>
    private class SignAtlas
    {
        public const string BusStopSign = "#parada", AdPanel = "#panel";
        private const int W = 2048, H = 2048, PlateW = 1024, PlateH = 224;
        private readonly Dictionary<string, RectInt> slots = new Dictionary<string, RectInt>();
        private readonly List<(string key, RectInt rect)> pending = new List<(string, RectInt)>();
        private Material material;

        public static string PlateKey(string name, int arrow) => $"{name}|{arrow}";

        public SignAtlas()
        {
            // Todas las placas posibles: avenidas con doble flecha, calles transversales con flecha a cada lado
            var keys = new List<string> { PlateKey(RingNorthName, 0), PlateKey(RingSouthName, 0) };
            foreach (var (_, name) in SideStreets) { keys.Add(PlateKey(name, 1)); keys.Add(PlateKey(name, -1)); keys.Add(PlateKey(name, 0)); }
            int row = 0, col = 0;
            foreach (var k in keys)
            {
                slots[k] = new RectInt(col * PlateW, row * PlateH, PlateW, PlateH);
                if (++row * PlateH + PlateH > H) { row = 0; col++; }
            }
            // 11 placas: 9 en la columna izquierda y 2 abajo en la derecha; senal y panel arriba a la derecha
            slots[BusStopSign] = new RectInt(PlateW, H - 768, 512, 768);
            slots[AdPanel] = new RectInt(PlateW + 512, H - 768, 512, 768);
        }

        public Rect Slot(string key)
        {
            var r = slots[key];
            const float pad = 2f;   // evita sangrado entre casillas al filtrar
            return new Rect((r.x + pad) / W, (r.y + pad) / H, (r.width - 2f * pad) / W, (r.height - 2f * pad) / H);
        }

        public Material Material()
        {
            if (material != null) return material;
            Build();
            material = AssetDatabase.LoadAssetAtPath<Material>(SignMatPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Standard")) { name = "Decor_Letreros", enableInstancing = true };
                AssetDatabase.CreateAsset(material, SignMatPath);
            }
            material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);
            material.SetFloat("_Glossiness", 0.3f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private void Build()
        {
            var atlas = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var fill = new Color32[W * H];
            for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(12, 12, 12, 255);
            atlas.SetPixels32(fill);
            foreach (var kv in slots)
            {
                Texture2D t = kv.Key == BusStopSign ? BusStopTexture()
                            : kv.Key == AdPanel ? AdTexture()
                            : PlateTexture(kv.Key.Split('|')[0], int.Parse(kv.Key.Split('|')[1]));
                atlas.SetPixels(kv.Value.x, kv.Value.y, kv.Value.width, kv.Value.height, t.GetPixels());
                Object.DestroyImmediate(t);
            }
            atlas.Apply();
            WriteTexture(AtlasPath, atlas, false, false);
        }

        private static Texture2D PlateTexture(string name, int arrow)
        {
            var bg = Canvas(PlateW, PlateH, new Color32(14, 14, 14, 255));
            var px = bg.GetPixels32();
            Color32 w = new Color32(240, 240, 240, 255);
            RectOutline(px, PlateW, 8, 8, PlateW - 16, PlateH - 16, 7, w);
            int box = PlateH - 16;
            FillRect(px, PlateW, 8 + box, 8, 7, PlateH - 16, w);   // separador del recuadro de la flecha
            DrawArrow(px, PlateW, 8 + box / 2, PlateH / 2, box - 70, 34, arrow, w);
            bg.SetPixels32(px); bg.Apply();
            float x0 = (8f + box + 7f) / PlateW;
            var tex = TextRender.Render(bg, new[] { new TextRender.Label(name, new Vector2((x0 + 1f) * 0.5f, 0.5f), 0.50f, (1f - x0) * 0.88f, Color.white) });
            Object.DestroyImmediate(bg);
            return tex;
        }

        private static Texture2D BusStopTexture()
        {
            const int w = 512, h = 768;
            var bg = Canvas(w, h, new Color32(28, 30, 34, 255));
            var px = bg.GetPixels32();
            Color32 white = new Color32(240, 240, 240, 255), red = new Color32(196, 22, 34, 255);
            FillRect(px, w, 0, h - 150, w, 150, red);                   // franja superior roja
            FillRect(px, w, 40, h - 300, 132, 132, white);               // pictograma de bus
            FillRect(px, w, 58, h - 270, 96, 56, new Color32(28, 30, 34, 255));
            FillRect(px, w, 60, h - 296, 24, 18, new Color32(28, 30, 34, 255));
            FillRect(px, w, 128, h - 296, 24, 18, new Color32(28, 30, 34, 255));
            for (int i = 0; i < 4; i++) FillRect(px, w, 40, 80 + i * 110, w - 80, 4, new Color32(90, 92, 96, 255));
            bg.SetPixels32(px); bg.Apply();
            var tex = TextRender.Render(bg, new[]
            {
                new TextRender.Label("PARADA", new Vector2(0.5f, 1f - 75f / h), 0.12f, 0.8f, Color.white),
                new TextRender.Label("PC1145", new Vector2(0.67f, 1f - 234f / h), 0.08f, 0.5f, Color.white),
                new TextRender.Label("210   405", new Vector2(0.5f, 0.46f), 0.09f, 0.8f, Color.white),
                new TextRender.Label("421   502", new Vector2(0.5f, 0.32f), 0.09f, 0.8f, Color.white),
                new TextRender.Label("516   C01", new Vector2(0.5f, 0.18f), 0.09f, 0.8f, Color.white),
            });
            Object.DestroyImmediate(bg);
            return tex;
        }

        private static Texture2D AdTexture()
        {
            const int w = 512, h = 768;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float fy = y / (float)h, fx = x / (float)w;
                    Color c = Color.Lerp(new Color(0.86f, 0.90f, 0.93f), new Color(0.35f, 0.62f, 0.78f), fy);
                    float d = new Vector2(fx - 0.55f, fy - 0.62f).magnitude;
                    if (d < 0.22f) c = Color.Lerp(c, new Color(0.98f, 0.80f, 0.35f), 0.85f);     // sol abstracto
                    if (fy < 0.30f + 0.05f * Mathf.Sin(fx * 9f)) c = Color.Lerp(c, new Color(0.30f, 0.33f, 0.40f), 0.9f); // cerros
                    px[y * w + x] = c;
                }
            t.SetPixels32(px); t.Apply();
            return t;
        }
    }

    // ------------------------------------------------------------
    // Dibujo de pixeles
    // ------------------------------------------------------------
    private static Texture2D Canvas(int w, int h, Color32 c)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = c;
        t.SetPixels32(px); t.Apply();
        return t;
    }

    private static void FillRect(Color32[] px, int w, int x0, int y0, int rw, int rh, Color32 c)
    {
        int h = px.Length / w;
        for (int y = Mathf.Max(0, y0); y < Mathf.Min(h, y0 + rh); y++)
            for (int x = Mathf.Max(0, x0); x < Mathf.Min(w, x0 + rw); x++) px[y * w + x] = c;
    }

    private static void RectOutline(Color32[] px, int w, int x0, int y0, int rw, int rh, int t, Color32 c)
    {
        FillRect(px, w, x0, y0, rw, t, c); FillRect(px, w, x0, y0 + rh - t, rw, t, c);
        FillRect(px, w, x0, y0, t, rh, c); FillRect(px, w, x0 + rw - t, y0, t, rh, c);
    }

    /// <summary>Flecha horizontal centrada en (cx, cy): dir 1 = derecha, -1 = izquierda, 0 = doble.</summary>
    private static void DrawArrow(Color32[] px, int w, int cx, int cy, int len, int thick, int dir, Color32 c)
    {
        int head = (int)(len * 0.34f), half = len / 2;
        int x0 = cx - half + (dir <= 0 ? head : 0), x1 = cx + half - (dir >= 0 ? head : 0);
        FillRect(px, w, x0, cy - thick / 2, x1 - x0, thick, c);
        void Head(int tipX, int sign)
        {
            for (int i = 0; i <= head; i++)
            {
                int hh = (int)(i * 0.85f);
                FillRect(px, w, tipX - sign * i, cy - hh, 1, 2 * hh + 1, c);
            }
        }
        if (dir >= 0) Head(cx + half, 1);
        if (dir <= 0) Head(cx - half, -1);
    }

    // ------------------------------------------------------------
    // Texto: TextMesh sobre un fondo, renderizado con una camara temporal
    // ------------------------------------------------------------
    private static class TextRender
    {
        public struct Label
        {
            public string text; public Vector2 center; public float maxH, maxW; public Color color;
            /// <param name="center">Centro en fraccion de la imagen (0..1).</param>
            /// <param name="maxH">Alto maximo de la linea en fraccion del alto.</param>
            /// <param name="maxW">Ancho maximo en fraccion del ancho.</param>
            public Label(string t, Vector2 c, float h, float w, Color col) { text = t; center = c; maxH = h; maxW = w; color = col; }
        }

        private const int Layer = 31;

        public static Texture2D Render(Texture2D background, IEnumerable<Label> labels)
        {
            int W = background.width, H = background.height;
            float wu = W / 100f, hu = H / 100f;
            var rig = new GameObject("TmpTextRig") { hideFlags = HideFlags.HideAndDontSave };
            rig.transform.position = new Vector3(0f, -2000f, 0f);
            try
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.hideFlags = HideFlags.HideAndDontSave;
                Object.DestroyImmediate(quad.GetComponent<Collider>());
                quad.layer = Layer;
                quad.transform.SetParent(rig.transform, false);
                quad.transform.localScale = new Vector3(wu, hu, 1f);
                var qm = new Material(Shader.Find("Unlit/Texture")) { mainTexture = background };
                quad.GetComponent<MeshRenderer>().sharedMaterial = qm;

                var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                foreach (var l in labels)
                {
                    var go = new GameObject("t") { hideFlags = HideFlags.HideAndDontSave, layer = Layer };
                    go.transform.SetParent(rig.transform, false);
                    var tm = go.AddComponent<TextMesh>();
                    tm.font = font; tm.fontSize = 128; tm.characterSize = 0.1f; tm.fontStyle = FontStyle.Bold;
                    tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
                    tm.color = l.color; tm.text = l.text;
                    var mr = go.GetComponent<MeshRenderer>();
                    mr.sharedMaterial = font.material;
                    Vector3 size = mr.bounds.size;
                    // La caja del TextMesh incluye el interlineado: las mayusculas ocupan ~70 %
                    float k = Mathf.Min(l.maxH * hu / Mathf.Max(size.y * 0.72f, 1e-4f), l.maxW * wu / Mathf.Max(size.x, 1e-4f));
                    go.transform.localScale = Vector3.one * k;
                    go.transform.localPosition = new Vector3((l.center.x - 0.5f) * wu, (l.center.y - 0.5f) * hu + size.y * k * 0.04f, -0.01f);
                }

                var camGo = new GameObject("TmpTextCam") { hideFlags = HideFlags.HideAndDontSave };
                camGo.transform.SetParent(rig.transform, false);
                camGo.transform.localPosition = new Vector3(0f, 0f, -5f);
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true; cam.orthographicSize = hu * 0.5f; cam.aspect = wu / hu;
                cam.cullingMask = 1 << Layer; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
                cam.nearClipPlane = 0.1f; cam.farClipPlane = 20f; cam.stereoTargetEye = StereoTargetEyeMask.None;
                var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
                cam.targetTexture = rt;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(qm);
                return tex;
            }
            finally { Object.DestroyImmediate(rig); }
        }
    }
}
