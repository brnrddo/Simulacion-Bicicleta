// ============================================================
// EntryExtenderEditor.cs
// SimBici > Extend Entries (2026-11-25, despues de E3): alarga 120 m hacia
// afuera del mapa las calles de entrada C2 (x = 4, al sur) y C4 (x = -60, al
// norte), para que la cola de un rojo o de un PARE quepa en la calle y no
// llegue al punto donde aparecen los autos.
//   - Calzada: una malla nueva de asfalto (mismo material y ancho que la
//     calle), en tramos de ~9.5 m con UV como los de la calle original, con
//     collider en la capa Ground. Sin veredas: sigue por el pasto del terreno.
//   - Carril: se agregan nudos al inicio del carril de entrada.
//   - Nodos C2 / C4 al nuevo inicio.
//   - Reaparicion tras choque: CrashResetHandler.respawnDistanceM sube en el
//     mismo largo, para que la bici reaparezca donde antes.
// Despues: Generate Cyclist Network, Analyze Lane Room y Validate Network.
// Un solo paso de Undo; no hace nada si ya se aplico.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

public static class EntryExtenderEditor
{
    public const float ExtraM = 120f;
    private const float SegmentM = 9.5f;
    private const float KnotStepM = 6f;
    private const string MeshDir = "Assets/AssetsLab/Generated";

    // Nodo, carril de entrada, calzada de la calle y sentido hacia afuera del mapa (z)
    private static readonly (string node, string lane, float roadX, float outward)[] Entries =
    {
        ("C2", "Lane_North_01", 3.7f, -1f),
        ("C4", "Lane_South_01", -60.1f, +1f),
    };

    [MenuItem("SimBici/Extend Entries")]
    private static void Menu() => Debug.Log(Run());

    public static string Run()
    {
        if (EditorApplication.isPlaying) return "[EntryExtender] Salir de Play antes de correrlo.";
        var log = new StringBuilder("[EntryExtender]\n");
        var roads = GameObject.Find("City/Roads");
        if (roads == null) return log + "  ERROR: no hay City/Roads";
        if (roads.transform.Cast<Transform>().Any(t => t.name.StartsWith("Calzada_E3_")))
            return log + "  Ya se aplico (existen las calzadas Calzada_E3_*).";

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Alargar entradas");
        int group = Undo.GetCurrentGroup();

        var nodes = Object.FindObjectsByType<OdNode>(FindObjectsSortMode.None).ToDictionary(n => n.NodeId, n => n);
        foreach (var (nodeId, laneName, roadX, outward) in Entries)
        {
            var road = roads.transform.Cast<Transform>()
                .FirstOrDefault(t => t.gameObject.activeInHierarchy && t.name.StartsWith("Calle Transversal A") && Mathf.Abs(t.position.x - roadX) < 1.5f);
            var laneGo = GameObject.Find(laneName);
            if (road == null || laneGo == null) { log.AppendLine($"  AVISO {nodeId}: falta la calzada o el carril"); continue; }

            log.AppendLine(ExtendRoad(road, nodeId, outward));
            log.AppendLine(ExtendLane(laneGo.GetComponent<SplineContainer>(), outward));
            if (nodes.TryGetValue(nodeId, out var node))
            {
                var c = laneGo.GetComponent<SplineContainer>();
                Undo.RecordObject(node.transform, "Mover nodo");
                node.transform.position = c.transform.TransformPoint((Vector3)c.Spline.EvaluatePosition(0f));
            }
        }

        var crh = Object.FindFirstObjectByType<CrashResetHandler>();
        if (crh != null)
        {
            var so = new SerializedObject(crh);
            var p = so.FindProperty("respawnDistanceM");
            float before = p.floatValue;
            p.floatValue = before + ExtraM;
            so.ApplyModifiedProperties();
            log.AppendLine($"  reaparicion tras choque: respawnDistanceM {before:F0} -> {before + ExtraM:F0} m (mismo lugar)");
        }

        Undo.CollapseUndoOperations(group);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(roads.scene);
        log.AppendLine("  Siguiente: Generate Cyclist Network, Analyze Lane Room y Validate Network.");
        return log.ToString();
    }

    /// <summary>Tramo de calzada nuevo a continuacion del extremo exterior de la calle, del mismo ancho y material.</summary>
    private static string ExtendRoad(Transform road, string nodeId, float outward)
    {
        var mf = road.GetComponent<MeshFilter>();
        var world = mf.sharedMesh.vertices.Select(v => road.TransformPoint(v)).ToList();
        float zEnd = outward < 0f ? world.Min(p => p.z) : world.Max(p => p.z);
        var endRow = world.Where(p => Mathf.Abs(p.z - zEnd) < 0.6f).ToList();
        float x0 = endRow.Min(p => p.x), x1 = endRow.Max(p => p.x), y = endRow.Average(p => p.y);

        var v = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
        int segments = Mathf.CeilToInt(ExtraM / SegmentM);
        for (int i = 0; i < segments; i++)
        {
            float za = zEnd + outward * i * ExtraM / segments, zb = zEnd + outward * (i + 1) * ExtraM / segments;
            int b = v.Count;
            v.Add(new Vector3(x0, y, za)); v.Add(new Vector3(x1, y, za)); v.Add(new Vector3(x0, y, zb)); v.Add(new Vector3(x1, y, zb));
            uv.Add(new Vector2(0f, 0f)); uv.Add(new Vector2(1f, 0f)); uv.Add(new Vector2(0f, 1f)); uv.Add(new Vector2(1f, 1f));
            // Cara hacia arriba en los dos sentidos de avance
            if (outward > 0f) tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
            else tris.AddRange(new[] { b, b + 1, b + 2, b + 1, b + 3, b + 2 });
        }
        var mesh = new Mesh { name = $"E3_Calzada_{nodeId}" };
        mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();

        RingBikeLaneEditor.EnsureFolder(MeshDir);
        string path = $"{MeshDir}/{mesh.name}.asset";
        if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);

        var go = new GameObject($"Calzada_E3_{nodeId}") { layer = road.gameObject.layer };
        Undo.RegisterCreatedObjectUndo(go, go.name);
        go.transform.SetParent(road.parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = road.GetComponent<MeshRenderer>().sharedMaterials;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(road.gameObject));
        return $"  calzada {nodeId}: x [{x0:F2}, {x1:F2}], z de {zEnd:F1} a {zEnd + outward * ExtraM:F1} ({path})";
    }

    /// <summary>Agrega nudos rectos antes del inicio del carril (el carril va hacia adentro del mapa).</summary>
    private static string ExtendLane(SplineContainer c, float outward)
    {
        Undo.RecordObject(c, "Alargar carril");
        var sp = c.Spline;
        Vector3 start = c.transform.TransformPoint((Vector3)sp.EvaluatePosition(0f));
        int added = 0;
        for (float d = KnotStepM; d <= ExtraM + 0.01f; d += KnotStepM)
        {
            Vector3 p = start + new Vector3(0f, 0f, outward * d);
            sp.Insert(0, new BezierKnot((Unity.Mathematics.float3)c.transform.InverseTransformPoint(p)), TangentMode.AutoSmooth);
            added++;
        }
        return $"  carril {c.name}: {added} nudos nuevos; ahora empieza en z = {start.z + outward * ExtraM:F1} ({sp.GetLength():F0} m)";
    }
}
