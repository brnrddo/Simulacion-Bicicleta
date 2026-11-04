// ============================================================
// RingBikeLaneEditor.cs
// SimBici > Build Ring Bike Lane (A2, 2026-11-04): ciclovia del anillo al estilo
// de Santiago, sin mover calzada ni pistas de autos.
//   - Ciclovia EXTERIOR (un sentido, el de siempre): la linea segmentada que la
//     separa de la calzada pasa a ser continua, con tachones amarillos y negros
//     sobre ella; cada pictograma de bicicleta recibe una flecha de sentido.
//   - Ciclovia INTERIOR: se borra (pictogramas apagados). Su linea segmentada pasa
//     a ser la linea continua del borde de la berma; la bici en ese sentido va
//     junto a la solera, como en las calles sin ciclovia.
//   - Las lineas nuevas se cortan dentro de las intersecciones y sobre los pasos
//     peatonales (las segmentadas originales los atravesaban).
//   - Al final regenera la red del ciclista (CyclistNetworkEditor).
// Rehacer es seguro: primero se restauran los objetos apagados ("(A2 off)") y se
// borra el objeto generado.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class RingBikeLaneEditor
{
    public const string RootName = "A2_Ciclovia";
    public const string OffTag = " (A2 off)";
    private const string RoadPrefix = "Circuito - ";
    private const string PictogramPrefix = "Bicycle Road Marking";

    // Perfil del anillo medido en la escena (Documentacion 4.3): lineas de separacion a 3.75 m del eje,
    // pictogramas en el centro de cada ciclovia (4.75 m)
    private const float BoundaryOffsetM = 3.75f;
    private const float PictogramOffsetM = 4.75f;
    private const float ClassifyToleranceM = 0.35f;
    private const float DashJoinGapM = 1.6f;        // los segmentos son de 1 m con 1 m de espacio
    private const float SampleStepM = 0.5f;
    private const float CrosswalkMarginM = 0.6f;
    private const float MinRunM = 1.5f;

    private const float TachonSpacingM = 2.5f;      // [VERIFICAR] espaciamiento con el Manual de Senalizacion
    private const float TachonEndClearM = 1.5f;
    private const float MarkY = 0.012f;             // sobre las lineas (0.01) para no parpadear

    private const string DecorDir = "Assets/AssetsLab/Decor";
    private const string GeneratedDir = DecorDir + "/Generated";
    public const string DecorMaterialPath = DecorDir + "/Decor_VertexColor.mat";

    [MenuItem("SimBici/Build Ring Bike Lane")]
    private static void Menu() => Debug.Log(Build());

    /// <summary>Geometria del anillo: una "pista de atletismo" con eje recto en z = cz, x en [cx - L, cx + L].</summary>
    public struct Ring
    {
        public float cx, cz, L, R0;
        public float HalfWidth;  // media calzada pintada (m)

        public Vector3 SpinePoint(Vector3 p) => new Vector3(cx + Mathf.Clamp(p.x - cx, -L, L), 0f, cz);
        public float Radial(Vector3 p) { var q = SpinePoint(p); return new Vector2(p.x - q.x, p.z - q.z).magnitude; }
        /// <summary>Desplazamiento respecto del eje de la calzada (+ = hacia el exterior).</summary>
        public float Offset(Vector3 p) => Radial(p) - R0;

        public Vector3 Outward(Vector3 p)
        {
            var q = SpinePoint(p);
            var r = new Vector3(p.x - q.x, 0f, p.z - q.z);
            return r.sqrMagnitude > 1e-8f ? r.normalized : Vector3.forward;
        }

        /// <summary>Sentido de la ciclovia exterior (antihorario visto desde arriba): exterior a la derecha.</summary>
        public Vector3 Forward(Vector3 p) { var r = Outward(p); return new Vector3(-r.z, 0f, r.x); }

        public float Total => 4f * L + 2f * Mathf.PI * R0;

        /// <summary>Posicion a lo largo del anillo (0 en el extremo este de la recta norte, crece en el sentido de Forward).</summary>
        public float T(Vector3 p)
        {
            float x = p.x - cx, z = p.z - cz;
            if (x >= -L && x <= L) return z > 0f ? L - x : 2f * L + Mathf.PI * R0 + (x + L);
            if (x < -L)
            {
                float a = Mathf.Atan2(z, x + L);
                if (a < 0f) a += 2f * Mathf.PI;
                return 2f * L + R0 * (a - Mathf.PI * 0.5f);
            }
            float b = Mathf.Atan2(z, x - L);
            float t = 4f * L + Mathf.PI * R0 + R0 * (b + Mathf.PI * 0.5f);
            return t >= Total ? t - Total : t;
        }
    }

    private struct Dash { public float t0, t1; public Vector3 p0, p1; }

    public static string Build()
    {
        var log = new StringBuilder();
        if (EditorApplication.isPlaying) return "[RingBikeLane] Salir de Play antes de generar.";

        // 0) Restaurar lo apagado en una corrida anterior y borrar lo generado
        int restored = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!t.name.EndsWith(OffTag)) continue;
            Undo.RecordObject(t.gameObject, "Restore");
            t.name = t.name.Substring(0, t.name.Length - OffTag.Length);
            t.gameObject.SetActive(true);
            restored++;
        }
        var old = GameObject.Find(RootName);
        if (old != null) Undo.DestroyObjectImmediate(old);

        // 1) Geometria del anillo desde las dos rectas
        var roads = RingRoads();
        if (!TryGetRing(out Ring ring)) return "[RingBikeLane] No se encontraron las rectas del circuito.";
        log.AppendLine($"  Anillo: eje x = {ring.cx:F2} ± {ring.L:F2}, z = {ring.cz:F2}, radio al eje de la calzada {ring.R0:F2} m");

        var crosswalks = Object.FindObjectsByType<Crosswalk>(FindObjectsSortMode.None);
        bool Allowed(Vector3 p)
        {
            if (!Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 6f)) return false;
            if (!hit.collider.name.StartsWith(RoadPrefix)) return false;  // dentro de una interseccion
            return !OnCrosswalk(crosswalks, p, CrosswalkMarginM);
        }

        // 2) Lineas segmentadas de separacion: clasificar, apagar y armar cadenas
        var exterior = new List<Dash>();
        var interior = new List<Dash>();
        LineRenderer template = null;
        foreach (var road in roads)
        {
            foreach (var lr in road.GetComponentsInChildren<LineRenderer>(false))
            {
                if (lr.positionCount != 2) continue;
                Vector3 p0 = lr.GetPosition(0), p1 = lr.GetPosition(1);
                if (!lr.useWorldSpace) { p0 = lr.transform.TransformPoint(p0); p1 = lr.transform.TransformPoint(p1); }
                float o = ring.Offset((p0 + p1) * 0.5f);
                List<Dash> into = Mathf.Abs(o - BoundaryOffsetM) < ClassifyToleranceM ? exterior
                                : Mathf.Abs(o + BoundaryOffsetM) < ClassifyToleranceM ? interior : null;
                if (into == null) continue;
                float t0 = ring.T(p0), t1 = ring.T(p1);
                if (Mathf.Abs(t1 - t0) > ring.Total * 0.5f) { if (t0 < t1) t0 += ring.Total; else t1 += ring.Total; }
                into.Add(t0 <= t1 ? new Dash { t0 = t0, t1 = t1, p0 = p0, p1 = p1 } : new Dash { t0 = t1, t1 = t0, p0 = p1, p1 = p0 });
                if (template == null) template = lr;
                TurnOff(lr.gameObject);
            }
        }
        if (template == null) return "[RingBikeLane] No se encontraron lineas segmentadas de separacion.";

        // 2b) Segmentos antiguos que pasan sobre los pasos peatonales (lineas de carril del pack de la ciudad)
        int onCrossing = 0;
        foreach (var lr in Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None))
        {
            if (lr.positionCount != 2 || lr.transform.parent == null || lr.transform.parent.name != "Lane") continue;
            Vector3 m = (lr.GetPosition(0) + lr.GetPosition(1)) * 0.5f;
            if (!lr.useWorldSpace) m = lr.transform.TransformPoint(m);
            if (!OnCrosswalk(crosswalks, m, 0.2f)) continue;
            TurnOff(lr.gameObject);
            onCrossing++;
        }
        log.AppendLine($"  Segmentos sobre pasos peatonales apagados: {onCrossing}");

        var root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, RootName);
        GameObjectUtility.SetStaticEditorFlags(root, StaticEditorFlags.BatchingStatic);

        var extRuns = Runs(exterior, ring, Allowed);
        var intRuns = Runs(interior, ring, Allowed);
        foreach (var run in extRuns) MakeLine(template, root.transform, "Linea ciclovia (continua)", run);
        foreach (var run in intRuns) MakeLine(template, root.transform, "Linea berma (continua)", run);
        log.AppendLine($"  Separacion ciclovia exterior: {exterior.Count} segmentos -> {extRuns.Count} lineas continuas ({extRuns.Sum(RunLength):F0} m)");
        log.AppendLine($"  Borde de berma interior: {interior.Count} segmentos -> {intRuns.Count} lineas continuas ({intRuns.Sum(RunLength):F0} m)");

        // 3) Pictogramas: apagar los interiores, flecha de sentido junto a los exteriores
        var verts = new List<Vector3>(); var cols = new List<Color>(); var tris = new List<int>();
        int picOff = 0, arrows = 0;
        foreach (var road in roads)
        {
            foreach (Transform c in road)
            {
                if (!c.gameObject.activeSelf || !c.name.StartsWith(PictogramPrefix)) continue;
                float o = ring.Offset(c.position);
                if (Mathf.Abs(o + PictogramOffsetM) < 0.6f) { TurnOff(c.gameObject); picOff++; continue; }
                if (Mathf.Abs(o - PictogramOffsetM) >= 0.6f || c.childCount == 0) continue;  // sin imagen: dentro de interseccion
                Vector3 fwd = ring.Forward(c.position);
                Vector3 at = c.position + fwd * 1.9f;
                if (!Allowed(at)) continue;
                AddArrow(verts, cols, tris, new Vector3(at.x, MarkY, at.z), fwd);
                arrows++;
            }
        }

        // 4) Tachones sobre la linea de separacion exterior
        int tachones = 0;
        Color yellow = VC(0.95f, 0.74f, 0.08f), black = VC(0.06f, 0.06f, 0.06f);
        foreach (var run in extRuns)
        {
            float len = RunLength(run);
            for (float s = TachonEndClearM; s <= len - TachonEndClearM + 1e-3f; s += TachonSpacingM)
            {
                Vector3 p = PointOnRun(run, s, out Vector3 dir);
                AddFrustum(verts, cols, tris, new Vector3(p.x, 0f, p.z), dir,
                           new Vector2(0.15f, 0.35f), new Vector2(0.09f, 0.25f), 0.05f, yellow, black);
                tachones++;
            }
        }
        var details = SaveMesh("RingBikeLane_Details", verts, cols, tris);
        var go = new GameObject("Tachones y flechas");
        Undo.RegisterCreatedObjectUndo(go, go.name);
        go.transform.SetParent(root.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = details;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = DecorMaterial();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        log.AppendLine($"  Pictogramas interiores apagados: {picOff}; flechas: {arrows}; tachones: {tachones} (cada {TachonSpacingM} m)");

        EditorSceneManager.MarkSceneDirty(root.scene);
        log.Insert(0, $"[RingBikeLane] Restaurados {restored} objetos de una corrida anterior.\n");

        // 5) Red del ciclista: el sentido interior queda sin ciclovia (junto a la solera)
        var net = Object.FindFirstObjectByType<CyclistNetwork>();
        if (net != null) log.AppendLine(CyclistNetworkEditor.Generate(net));
        else log.AppendLine("  AVISO: no hay CyclistNetwork en la escena");
        return log.ToString();
    }

    /// <summary>True si p esta sobre algun paso peatonal (rectangulo entre soleras, ensanchado en 'margin').</summary>
    public static bool OnCrosswalk(Crosswalk[] crosswalks, Vector3 p, float margin)
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

    /// <summary>Tramos de calzada del circuito (orden estable por nombre).</summary>
    public static List<Transform> RingRoads() =>
        Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
              .Where(t => t.name.StartsWith(RoadPrefix) && t.GetComponent<MeshRenderer>() != null)
              .OrderBy(t => t.name, System.StringComparer.Ordinal).ToList();

    /// <summary>Geometria del anillo medida de las dos rectas del circuito.</summary>
    public static bool TryGetRing(out Ring ring)
    {
        ring = default;
        var roads = RingRoads();
        var north = roads.FirstOrDefault(t => t.name.StartsWith(RoadPrefix + "Recta Norte"));
        var south = roads.FirstOrDefault(t => t.name.StartsWith(RoadPrefix + "Recta Sur"));
        if (north == null || south == null) return false;
        Bounds bn = north.GetComponent<MeshRenderer>().bounds, bs = south.GetComponent<MeshRenderer>().bounds;
        ring = new Ring
        {
            cx = (bn.center.x + bs.center.x) * 0.5f,
            cz = (bn.center.z + bs.center.z) * 0.5f,
            L = (bn.extents.x + bs.extents.x) * 0.5f,
            R0 = (bn.center.z - bs.center.z) * 0.5f,
            HalfWidth = (bn.extents.z + bs.extents.z) * 0.5f,
        };
        return true;
    }

    // ------------------------------------------------------------
    // Cadenas de segmentos -> tramos continuos permitidos
    // ------------------------------------------------------------
    private static List<List<Vector3>> Runs(List<Dash> dashes, Ring ring, System.Func<Vector3, bool> allowed)
    {
        var runs = new List<List<Vector3>>();
        if (dashes.Count == 0) return runs;
        dashes.Sort((a, b) => a.t0.CompareTo(b.t0));

        // Cadenas: se unen segmentos separados por el espacio normal del segmentado
        var chains = new List<List<Dash>> { new List<Dash> { dashes[0] } };
        for (int i = 1; i < dashes.Count; i++)
        {
            var last = chains[chains.Count - 1];
            if (dashes[i].t0 - last[last.Count - 1].t1 < DashJoinGapM) last.Add(dashes[i]);
            else chains.Add(new List<Dash> { dashes[i] });
        }
        // El anillo es cerrado: unir la ultima cadena con la primera si se tocan en t = 0
        if (chains.Count > 1)
        {
            var first = chains[0]; var lastC = chains[chains.Count - 1];
            if (first[0].t0 + ring.Total - lastC[lastC.Count - 1].t1 < DashJoinGapM)
            {
                lastC.AddRange(first);
                chains.RemoveAt(0);
            }
        }

        foreach (var chain in chains)
        {
            // Polilinea por los extremos de los segmentos, muestreada cada SampleStepM
            var pts = new List<Vector3>();
            foreach (var d in chain) { pts.Add(d.p0); pts.Add(d.p1); }
            var dense = new List<Vector3> { pts[0] };
            for (int i = 1; i < pts.Count; i++)
            {
                float seg = Vector3.Distance(pts[i - 1], pts[i]);
                int n = Mathf.Max(1, Mathf.CeilToInt(seg / SampleStepM));
                for (int k = 1; k <= n; k++) dense.Add(Vector3.Lerp(pts[i - 1], pts[i], k / (float)n));
            }
            var cur = new List<Vector3>();
            foreach (var p in dense)
            {
                if (allowed(p)) { cur.Add(p); continue; }
                if (cur.Count > 1 && PolyLength(cur) >= MinRunM) runs.Add(cur);
                cur = new List<Vector3>();
            }
            if (cur.Count > 1 && PolyLength(cur) >= MinRunM) runs.Add(cur);
        }
        return runs;
    }

    private static float PolyLength(List<Vector3> p) { float s = 0f; for (int i = 1; i < p.Count; i++) s += Vector3.Distance(p[i - 1], p[i]); return s; }
    private static float RunLength(List<Vector3> p) => PolyLength(p);

    private static Vector3 PointOnRun(List<Vector3> run, float s, out Vector3 dir)
    {
        for (int i = 1; i < run.Count; i++)
        {
            float seg = Vector3.Distance(run[i - 1], run[i]);
            if (s <= seg || i == run.Count - 1)
            {
                dir = run[i] - run[i - 1]; dir.y = 0f; dir.Normalize();
                return Vector3.Lerp(run[i - 1], run[i], seg > 1e-5f ? Mathf.Clamp01(s / seg) : 0f);
            }
            s -= seg;
        }
        dir = Vector3.forward;
        return run[0];
    }

    private static void MakeLine(LineRenderer template, Transform parent, string name, List<Vector3> run)
    {
        var go = Object.Instantiate(template.gameObject, parent);
        Undo.RegisterCreatedObjectUndo(go, name);
        go.name = name;
        go.SetActive(true);
        go.transform.rotation = template.transform.rotation;
        var lr = go.GetComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = run.Count;
        float y = template.GetPosition(0).y;
        for (int i = 0; i < run.Count; i++) lr.SetPosition(i, new Vector3(run[i].x, y, run[i].z));
    }

    public static void TurnOff(GameObject go)
    {
        Undo.RecordObject(go, "A2 off");
        if (!go.name.EndsWith(OffTag)) go.name += OffTag;
        go.SetActive(false);
    }

    // ------------------------------------------------------------
    // Mallas con color por vertice (sombreador SimBici/VehicleVertexColor, alfa 0 = color del vertice)
    // ------------------------------------------------------------
    /// <summary>Color de vertice para la decoracion: lineal y con alfa 0 (el alfa 1 lo reemplaza por _BodyColor).</summary>
    public static Color VC(float r, float g, float b) { var c = new Color(r, g, b).linear; c.a = 0f; return c; }

    public static Material DecorMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(DecorMaterialPath);
        if (mat != null) return mat;
        EnsureFolder(DecorDir);
        mat = new Material(Shader.Find("SimBici/VehicleVertexColor")) { enableInstancing = true };
        mat.SetColor("_BodyColor", Color.white);
        mat.SetFloat("_Glossiness", 0.15f);
        mat.SetFloat("_Metallic", 0f);
        AssetDatabase.CreateAsset(mat, DecorMaterialPath);
        return mat;
    }

    /// <summary>Guarda (o reemplaza el contenido de) una malla en AssetsLab/Decor/Generated.</summary>
    public static Mesh SaveMesh(string name, List<Vector3> verts, List<Color> cols, List<int> tris)
    {
        EnsureFolder(GeneratedDir);
        string path = $"{GeneratedDir}/{name}.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool isNew = mesh == null;
        if (isNew) mesh = new Mesh { name = name };
        mesh.Clear();
        mesh.indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(verts);
        mesh.SetColors(cols);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        if (isNew) AssetDatabase.CreateAsset(mesh, path);
        else EditorUtility.SetDirty(mesh);
        AssetDatabase.SaveAssets();
        return mesh;
    }

    public static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int i = path.LastIndexOf('/');
        EnsureFolder(path.Substring(0, i));
        AssetDatabase.CreateFolder(path.Substring(0, i), path.Substring(i + 1));
    }

    private static void Quad(List<Vector3> v, List<Color> c, List<int> t, Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Color col)
    {
        int i = v.Count;
        v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
        c.Add(col); c.Add(col); c.Add(col); c.Add(col);
        t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
    }

    /// <summary>
    /// Tronco de piramide (sin base) sobre el suelo: base (ancho, largo), cara superior (ancho, largo), alto.
    /// Caras en sentido horario vistas desde afuera (convencion de Unity).
    /// </summary>
    public static void AddFrustum(List<Vector3> v, List<Color> c, List<int> t, Vector3 center, Vector3 fwd,
                                  Vector2 bottom, Vector2 top, float h, Color topColor, Color sideColor)
    {
        fwd.y = 0f; fwd.Normalize();
        Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
        Vector3 B(float sx, float sz) => center + right * (sx * bottom.x * 0.5f) + fwd * (sz * bottom.y * 0.5f);
        Vector3 T(float sx, float sz) => center + Vector3.up * h + right * (sx * top.x * 0.5f) + fwd * (sz * top.y * 0.5f);
        Quad(v, c, t, T(-1, -1), T(-1, 1), T(1, 1), T(1, -1), topColor);    // arriba
        Quad(v, c, t, B(-1, 1), B(1, 1), T(1, 1), T(-1, 1), sideColor);     // frente
        Quad(v, c, t, B(1, -1), B(-1, -1), T(-1, -1), T(1, -1), sideColor); // atras
        Quad(v, c, t, B(1, 1), B(1, -1), T(1, -1), T(1, 1), sideColor);     // derecha
        Quad(v, c, t, B(-1, -1), B(-1, 1), T(-1, 1), T(-1, -1), sideColor); // izquierda
    }

    /// <summary>Flecha blanca plana (1.3 m: cuerpo 0.85 x 0.15, punta 0.45 x 0.45) apuntando a 'fwd'.</summary>
    private static void AddArrow(List<Vector3> v, List<Color> c, List<int> t, Vector3 center, Vector3 fwd)
    {
        fwd.y = 0f; fwd.Normalize();
        Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
        Color white = VC(0.92f, 0.92f, 0.92f);
        Vector3 P(float x, float z) => center + right * x + fwd * z;
        Quad(v, c, t, P(-0.075f, -0.65f), P(-0.075f, 0.2f), P(0.075f, 0.2f), P(0.075f, -0.65f), white);
        int i = v.Count;
        v.Add(P(-0.225f, 0.2f)); v.Add(P(0f, 0.65f)); v.Add(P(0.225f, 0.2f));
        c.Add(white); c.Add(white); c.Add(white);
        t.Add(i); t.Add(i + 1); t.Add(i + 2);
    }
}
