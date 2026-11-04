// ============================================================
// TrafficLane.cs
// Un carril = un spline dibujado EN EL SENTIDO DE CIRCULACION.
// Los carriles se conectan entre si (nextLanes) formando la red:
// al llegar al final de un carril, el auto elige el siguiente segun
// los pesos. Los tramos dentro de una interseccion tambien son
// carriles (cortos), lo que permite modelar giros mas adelante.
// Va en un GameObject con SplineContainer.
// ============================================================

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

[Serializable]
public class LaneConnection
{
    public TrafficLane lane;
    [Tooltip("Peso relativo para elegir este destino (p. ej. 70 derecho, 30 giro).")]
    [Min(0f)] public float weight = 1f;
}

/// <summary>Punto de conflicto de un conector con otro conector de la misma interseccion (lo calcula IntersectionLaneBuilder).</summary>
[Serializable]
public class LaneConflict
{
    public TrafficLane other;
    [Tooltip("Distancia desde el inicio de este conector hasta donde EMPIEZA la zona de conflicto (m).")]
    public float sSelf;
    [Tooltip("Distancia desde el inicio de este conector hasta donde TERMINA la zona de conflicto (m).")]
    public float sSelfEnd;
    [Tooltip("Distancia desde el inicio del otro conector hasta donde empieza la zona de conflicto (m).")]
    public float sOther;
    [Tooltip("Distancia desde el inicio del otro conector hasta donde termina la zona de conflicto (m).")]
    public float sOtherEnd;
    public ConflictMode mode;
}

[RequireComponent(typeof(SplineContainer))]
public class TrafficLane : MonoBehaviour
{
    [Header("Carril")]
    [SerializeField] private float speedLimitKmh = 50f;
    [SerializeField] private float laneWidthM = 3.5f;

    [Header("Conexiones al final del carril (vacio = los autos desaparecen al final)")]
    [SerializeField] private List<LaneConnection> nextLanes = new List<LaneConnection>();

    [Header("Espacio lateral para adelantar (lo completa SimBici > Analyze Lane Room)")]
    [Tooltip("Carril paralelo en sentido contrario (null = calle de sentido unico).")]
    [SerializeField] private TrafficLane opposingLane;
    [Tooltip("Distancia del eje del carril al eje del carril contrario (m).")]
    [SerializeField] private float opposingOffsetM;
    [Tooltip("Distancia del eje del carril a la solera izquierda (m).")]
    [SerializeField] private float leftCurbM;

    [Header("Interseccion (solo conectores; lo completa IntersectionLaneBuilder)")]
    [SerializeField] private MovementKind movement = MovementKind.None;
    [Tooltip("Puntos donde este conector debe ceder o esperar a otro.")]
    [SerializeField] private List<LaneConflict> conflicts = new List<LaneConflict>();

    public float LengthM { get; private set; }
    public float SpeedLimitMps => speedLimitKmh / 3.6f;
    public float LaneWidthM => laneWidthM;
    public IReadOnlyList<LaneConnection> NextLanes => nextLanes;
    public MovementKind Movement => movement;
    public bool IsConnector => movement != MovementKind.None;
    public IReadOnlyList<LaneConflict> Conflicts => conflicts;
    public TrafficLane OpposingLane => opposingLane;
    public float OpposingOffsetM => opposingOffsetM;
    public float LeftCurbM => leftCurbM;
    public bool IsOneWay => opposingLane == null;

    /// <summary>Carril de llegada del que sale este conector (lo resuelve TrafficManager en Awake).</summary>
    public TrafficLane EntryLane { get; set; }
    /// <summary>Zonas de conflicto con caminos del ciclista (giros que cruzan la ciclovia).</summary>
    public BikeConflictZone BikeConflicts { get; private set; }

    /// <summary>Pasos peatonales que cruzan este carril: distancia S sobre el carril y coordenada u sobre el cruce (los registra Crosswalk).</summary>
    public readonly List<(Crosswalk cw, float s, float u)> Crosswalks = new List<(Crosswalk, float, float)>();
    public void AddCrosswalk(Crosswalk cw, float s, float u) => Crosswalks.Add((cw, s, u));

    /// <summary>Caja envolvente en planta (para descartar rapido carriles lejanos).</summary>
    public Bounds WorldBounds { get; private set; }

    /// <summary>Linea de detencion al final de este carril (null si no es un acceso semaforizado). La asigna StopLine.</summary>
    public StopLine StopLine { get; set; }

    /// <summary>Autos en este carril, ordenados por S ascendente. Lo mantiene TrafficManager.</summary>
    public readonly List<TrafficVehicle> Vehicles = new List<TrafficVehicle>();

    private SplineContainer container;
    private Spline spline;

    private void Awake()
    {
        container = GetComponent<SplineContainer>();
        spline = container.Spline;
        LengthM = spline.GetLength();
        BikeConflicts = GetComponent<BikeConflictZone>();

        var b = new Bounds(container.transform.TransformPoint((Vector3)spline.EvaluatePosition(0f)), Vector3.zero);
        for (float d = 0f; d <= LengthM; d += 2f)
            b.Encapsulate(container.transform.TransformPoint((Vector3)spline.EvaluatePosition(
                spline.ConvertIndexUnit(d, PathIndexUnit.Distance, PathIndexUnit.Normalized))));
        b.Encapsulate(container.transform.TransformPoint((Vector3)spline.EvaluatePosition(1f)));
        b.Expand(new Vector3(laneWidthM, 10f, laneWidthM));
        WorldBounds = b;

        Vector3 s = container.transform.lossyScale;
        if (Mathf.Abs(s.x - 1f) > 0.001f || Mathf.Abs(s.z - 1f) > 0.001f)
            Debug.LogWarning($"[TrafficLane] {name}: escala distinta de 1, las distancias no estaran en metros.");
    }

    /// <summary>Posicion y rotacion a una distancia S desde el inicio, con desplazamiento lateral (+ = derecha).</summary>
    public void GetPose(float sM, float lateralM, out Vector3 position, out Quaternion rotation)
    {
        float t = spline.ConvertIndexUnit(Mathf.Clamp(sM, 0f, LengthM), PathIndexUnit.Distance, PathIndexUnit.Normalized);

        Vector3 localPos = (Vector3)spline.EvaluatePosition(t);
        Vector3 fwd = container.transform.TransformDirection((Vector3)spline.EvaluateTangent(t));
        if (fwd.sqrMagnitude < 1e-6f) fwd = transform.forward;
        fwd.Normalize();

        Vector3 flatFwd = new Vector3(fwd.x, 0f, fwd.z);
        Vector3 right = flatFwd.sqrMagnitude > 1e-6f ? Vector3.Cross(Vector3.up, flatFwd.normalized) : transform.right;

        position = container.transform.TransformPoint(localPos) + right * lateralM;
        rotation = Quaternion.LookRotation(fwd, Vector3.up);
    }

    /// <summary>Proyecta un punto del mundo sobre el carril: distancia S y desplazamiento lateral.</summary>
    public void Project(Vector3 worldPoint, out float sM, out float lateralM)
    {
        float3 local = container.transform.InverseTransformPoint(worldPoint);
        SplineUtility.GetNearestPoint(spline, local, out float3 nearestLocal, out float t, 4, 2);
        sM = spline.ConvertIndexUnit(t, PathIndexUnit.Normalized, PathIndexUnit.Distance);

        Vector3 fwd = container.transform.TransformDirection((Vector3)spline.EvaluateTangent(t));
        fwd.y = 0f;
        Vector3 right = fwd.sqrMagnitude > 1e-6f ? Vector3.Cross(Vector3.up, fwd.normalized) : transform.right;
        Vector3 delta = worldPoint - container.transform.TransformPoint((Vector3)nearestLocal);
        delta.y = 0f;
        lateralM = Vector3.Dot(delta, right);
    }

    /// <summary>Cambia en runtime el peso de una conexion (lo usa RingExitController). False si no existe.</summary>
    public bool SetWeight(TrafficLane target, float weight)
    {
        foreach (var c in nextLanes)
            if (c.lane == target) { c.weight = Mathf.Max(0f, weight); return true; }
        return false;
    }

    /// <summary>Elige el siguiente carril segun los pesos. null = fin de la red.</summary>
    public TrafficLane PickNext(System.Random rng)
    {
        float total = 0f;
        foreach (var c in nextLanes) if (c.lane != null) total += c.weight;
        if (total <= 0f) return null;

        double r = rng.NextDouble() * total;
        foreach (var c in nextLanes)
        {
            if (c.lane == null) continue;
            r -= c.weight;
            if (r <= 0) return c.lane;
        }
        return nextLanes[nextLanes.Count - 1].lane;
    }

    /// <summary>
    /// Como PickNext, pero solo entre los destinos permitidos (p. ej. conectores donde cabe un vehiculo largo).
    /// Consume un solo numero del RNG igual que PickNext (determinismo). Si ninguno esta permitido, elige entre todos.
    /// </summary>
    public TrafficLane PickNext(System.Random rng, Predicate<TrafficLane> allowed)
    {
        float total = 0f;
        foreach (var c in nextLanes) if (c.lane != null && allowed(c.lane)) total += c.weight;
        if (total <= 0f) return PickNext(rng);

        double r = rng.NextDouble() * total;
        TrafficLane last = null;
        foreach (var c in nextLanes)
        {
            if (c.lane == null || !allowed(c.lane)) continue;
            last = c.lane;
            r -= c.weight;
            if (r <= 0) return c.lane;
        }
        return last;
    }

    // ------------------------------------------------------------
    // Gizmos: flechas de sentido y conexiones (para revisar la red en la vista Scene)
    // ------------------------------------------------------------
    private void OnDrawGizmos()
    {
        var c = GetComponent<SplineContainer>();
        if (c == null || c.Spline == null || c.Spline.Count < 2) return;
        var sp = c.Spline;
        float len = sp.GetLength();
        if (len < 0.1f) return;

        Gizmos.color = new Color(0.2f, 0.6f, 1f);
        for (float d = 2f; d < len; d += 8f)
        {
            float t = sp.ConvertIndexUnit(d, PathIndexUnit.Distance, PathIndexUnit.Normalized);
            Vector3 p = c.transform.TransformPoint((Vector3)sp.EvaluatePosition(t)) + Vector3.up * 0.1f;
            Vector3 f = c.transform.TransformDirection((Vector3)sp.EvaluateTangent(t)).normalized;
            Vector3 r = Vector3.Cross(Vector3.up, f);
            Gizmos.DrawLine(p, p - f * 1.2f + r * 0.6f);
            Gizmos.DrawLine(p, p - f * 1.2f - r * 0.6f);
        }

        // Conexiones: linea amarilla del final de este carril al inicio del siguiente
        Vector3 end = c.transform.TransformPoint((Vector3)sp.EvaluatePosition(1f));
        Gizmos.color = Color.yellow;
        foreach (var conn in nextLanes)
        {
            if (conn.lane == null) continue;
            var oc = conn.lane.GetComponent<SplineContainer>();
            if (oc == null || oc.Spline == null || oc.Spline.Count == 0) continue;
            Vector3 start = oc.transform.TransformPoint((Vector3)oc.Spline.EvaluatePosition(0f));
            Gizmos.DrawLine(end + Vector3.up * 0.2f, start + Vector3.up * 0.2f);
            Gizmos.DrawWireSphere(start + Vector3.up * 0.2f, 0.3f);
        }
    }
}
