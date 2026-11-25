// ============================================================
// OdDemand.cs
// Demanda del trafico por matriz origen-destino (E2 del plan de rutas).
//   1. Al iniciar lee la matriz (JSON, ver OdMatrix) y la valida contra los
//      nodos de la escena (OdNode C1..C4). Si hay un error, lo informa y el
//      trafico NO se genera.
//   2. Sortea la matriz de esta repeticion (normal por celda, semilla
//      TrafficManager.CreateRng(seed, 7000)).
//   3. Cada spawner del carril de un nodo de entrada recibe su fila: su flujo
//      es la suma de la fila y cada llegada se sortea un destino con
//      probabilidad q(i,j) / suma.
//   4. Registra la matriz (evento od_matrix y _meta.txt) y, cada 60 s, los
//      autos generados y llegados por par (evento od_flow).
// Va en el objeto Traffic, junto a TrafficManager.
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

[DefaultExecutionOrder(15)]  // antes de TrafficManager (20) y de ExperimentLogger (100)
public class OdDemand : MonoBehaviour
{
    public const int RngOffset = 7000;

    [Header("Matriz origen-destino")]
    [Tooltip("Archivo JSON de la matriz. Ruta absoluta o relativa a Documentos/SimBici/Config. " +
             "Vacio = la matriz por defecto del proyecto (abajo).")]
    [SerializeField] private string matrixPath = "";
    [Tooltip("Matriz por defecto, dentro del proyecto.")]
    [SerializeField] private TextAsset defaultMatrix;

    [Header("Registro")]
    [SerializeField, Min(5f)] private float flowLogIntervalS = 60f;

    public bool IsValid { get; private set; }
    public string Source { get; private set; } = "";
    public OdMatrixFile File { get; private set; }
    /// <summary>Flujo sorteado de cada par (veh/h), en el orden del archivo.</summary>
    public IReadOnlyList<float> Sampled => sampled;

    private float[] sampled = new float[0];
    private readonly Dictionary<string, OdNode> nodes = new Dictionary<string, OdNode>();
    private readonly Dictionary<string, int> pairIndex = new Dictionary<string, int>();
    private int[] generated = new int[0];
    private int[] arrived = new int[0];
    private readonly List<TrafficSpawner> spawners = new List<TrafficSpawner>();
    private double countingSinceS;
    private float sinceLog;
    private TrafficManager manager;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string ConfigFolder =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "SimBici", "Config");

    private void Awake()
    {
        manager = GetComponent<TrafficManager>();
        foreach (var n in FindObjectsByType<OdNode>(FindObjectsSortMode.None).OrderBy(n => n.NodeId, System.StringComparer.Ordinal))
        {
            if (n.Lane == null) { Debug.LogError($"[OdDemand] Nodo {n.NodeId} sin carril."); continue; }
            if (nodes.ContainsKey(n.NodeId)) { Debug.LogError($"[OdDemand] Nodo repetido: {n.NodeId}."); continue; }
            nodes.Add(n.NodeId, n);
        }

        IsValid = Load(out string error);
        if (!IsValid)
        {
            Debug.LogError($"[OdDemand] No se genera trafico: {error}");
            ExperimentLogger.RegisterMeta("od_error", error);
            return;
        }

        int seed = manager != null ? manager.MasterSeed : 0;
        sampled = OdMatrix.Sample(File, TrafficManager.CreateRng(seed, RngOffset));
        generated = new int[sampled.Length];
        arrived = new int[sampled.Length];
        for (int i = 0; i < File.pairs.Count; i++) pairIndex[Key(File.pairs[i].from, File.pairs[i].to)] = i;

        ExperimentLogger.RegisterMeta("od_source", Source);
        ExperimentLogger.RegisterMeta("od_seed_offset", RngOffset.ToString(Inv));
        for (int i = 0; i < File.pairs.Count; i++)
        {
            var p = File.pairs[i];
            ExperimentLogger.RegisterMeta($"od_{p.from}_{p.to}", string.Format(Inv, "mean={0:F1};sd={1:F1};sampled={2:F1}",
                                          p.meanVehPerHour, p.sdVehPerHour, sampled[i]));
        }
        Debug.Log($"[OdDemand] Matriz {Source}: {OdMatrix.Describe(File, sampled)}");
    }

    private void Start()
    {
        if (!IsValid)
        {
            EventBus.Publish("od_error", "traffic", "source=" + Source);
            return;
        }
        for (int i = 0; i < File.pairs.Count; i++)
        {
            var p = File.pairs[i];
            EventBus.Publish("od_matrix", Key(p.from, p.to), string.Format(Inv,
                "from={0};to={1};mean_vph={2:F1};sd_vph={3:F1};sampled_vph={4:F1};source={5}",
                p.from, p.to, p.meanVehPerHour, p.sdVehPerHour, sampled[i], Source));
        }
    }

    private bool Load(out string error)
    {
        string json;
        if (string.IsNullOrWhiteSpace(matrixPath))
        {
            if (defaultMatrix == null) { error = "no hay archivo de matriz (ni ruta ni matriz por defecto)"; return false; }
            Source = defaultMatrix.name;
            json = defaultMatrix.text;
        }
        else
        {
            string path = Path.IsPathRooted(matrixPath) ? matrixPath : Path.Combine(ConfigFolder, matrixPath);
            Source = path;
            if (!System.IO.File.Exists(path)) { error = "no existe " + path; return false; }
            json = System.IO.File.ReadAllText(path);
        }

        File = OdMatrix.Parse(json, out error);
        if (File == null) { error = Source + ": " + error; return false; }

        var entries = nodes.Values.Where(n => n.Kind == OdNode.NodeKind.Entry).Select(n => n.NodeId).ToList();
        var exits = nodes.Values.Where(n => n.Kind == OdNode.NodeKind.Exit).Select(n => n.NodeId).ToList();
        if (!OdMatrix.Validate(File, entries, exits, out error)) { error = Source + ": " + error; return false; }
        return true;
    }

    /// <summary>Asigna a cada spawner su fila de la matriz sorteada. Lo llama TrafficManager antes de iniciar los spawners.</summary>
    public void ConfigureSpawners(IEnumerable<TrafficSpawner> all)
    {
        spawners.Clear();
        foreach (var sp in all)
        {
            var node = nodes.Values.FirstOrDefault(n => n.Kind == OdNode.NodeKind.Entry && n.Lane == sp.Lane);
            if (node == null)
            {
                Debug.LogWarning($"[OdDemand] {sp.name} no esta en un nodo de entrada: no genera autos.");
                sp.SetDemand(null, new OdNode[0], new float[0]);
                continue;
            }
            var dests = new List<OdNode>();
            var flows = new List<float>();
            if (IsValid)
                for (int i = 0; i < File.pairs.Count; i++)
                    if (File.pairs[i].from == node.NodeId && nodes.TryGetValue(File.pairs[i].to, out var to))
                    {
                        dests.Add(to);
                        flows.Add(sampled[i]);
                    }
            sp.SetDemand(node.NodeId, dests, flows);
            spawners.Add(sp);
        }
    }

    public void OnSpawned(string from, string to)
    {
        if (pairIndex.TryGetValue(Key(from, to), out int i)) generated[i]++;
    }

    public void OnArrived(string from, string to)
    {
        if (pairIndex.TryGetValue(Key(from, to), out int i)) arrived[i]++;
    }

    /// <summary>Al terminar el precalentamiento los contadores parten de cero (la medicion es de la sesion).</summary>
    public void ResetCounters(double simTimeS)
    {
        System.Array.Clear(generated, 0, generated.Length);
        System.Array.Clear(arrived, 0, arrived.Length);
        foreach (var sp in spawners) sp.ResetCounters();
        countingSinceS = simTimeS;
        sinceLog = 0f;
    }

    /// <summary>Lo avanza TrafficManager en cada paso, fuera del precalentamiento.</summary>
    public void Tick(float dt, double simTimeS)
    {
        if (!IsValid) return;
        sinceLog += dt;
        if (sinceLog < flowLogIntervalS) return;
        sinceLog = 0f;
        double hours = (simTimeS - countingSinceS) / 3600.0;
        for (int i = 0; i < File.pairs.Count; i++)
        {
            var p = File.pairs[i];
            var sp = spawners.FirstOrDefault(s => s.OriginId == p.from);
            EventBus.Publish("od_flow", Key(p.from, p.to), string.Format(Inv,
                "from={0};to={1};sampled_vph={2:F1};generated={3};generated_vph={4:F1};arrived={5};origin_pending={6};origin_dropped={7};elapsed_s={8:F0}",
                p.from, p.to, sampled[i], generated[i], hours > 0 ? generated[i] / hours : 0.0, arrived[i],
                sp != null ? sp.PendingCount : 0, sp != null ? sp.DroppedCount : 0, simTimeS - countingSinceS));
        }
    }

    public int GeneratedCount(int pair) => generated[pair];
    public int ArrivedCount(int pair) => arrived[pair];

    private static string Key(string from, string to) => from + "->" + to;
}
