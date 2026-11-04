// ============================================================
// Crosswalk.cs
// Paso peatonal. Rectangulo sobre la calzada entre las dos soleras (A -> B),
// de ancho widthM. Dos tipos:
//   - Signalized: sigue la fase peatonal del IntersectionController para el brazo
//     que cruza (GetPedestrianState(arm)).
//   - Zebra: paso cebra sin semaforo; el peaton tiene preferencia (los autos que
//     pueden detenerse comodamente le ceden), pero no se lanza frente a un auto
//     que no alcanza a frenar (PedestrianRules.ZebraSafeFromCar).
// Al iniciar calcula donde lo cruza cada carril de autos y se registra en ellos:
// TrafficManager detiene a los autos antes del cruce si hay un peaton en su franja
// o, en un paso cebra, esperando para cruzar.
// Lo crea SimBici > Build Pedestrian Setup.
// ============================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

[DefaultExecutionOrder(5)]
public class Crosswalk : MonoBehaviour
{
    public enum Kind { Signalized, Zebra }

    [SerializeField] private Kind kind = Kind.Zebra;
    [Tooltip("Solo Signalized: controlador y brazo que cruza este paso.")]
    [SerializeField] private IntersectionController controller;
    [SerializeField] private Approach arm;
    [Tooltip("Extremos sobre las soleras (mundo).")]
    [SerializeField] private Vector3 endA;
    [SerializeField] private Vector3 endB;
    [SerializeField, Min(1f)] private float widthM = 3f;

    public Kind CrosswalkKind => kind;
    public IntersectionController Controller => controller;
    public Approach Arm => arm;
    public float WidthM => widthM;
    public float LengthM => Vector3.Distance(Flat(endA), Flat(endB));
    public Vector3 EndA => endA;
    public Vector3 EndB => endB;

    /// <summary>Peatones en la calzada del cruce (los actualiza PedestrianManager cada paso).</summary>
    public readonly List<PedestrianAgent> OnCrossing = new List<PedestrianAgent>();
    /// <summary>Peatones esperando en la cuneta para cruzar.</summary>
    public readonly List<PedestrianAgent> Waiting = new List<PedestrianAgent>();

    /// <summary>Carriles que pasan por el cruce: distancia sobre el carril y coordenada u sobre el cruce.</summary>
    public readonly List<(TrafficLane lane, float sLane, float u)> LaneCrossings = new List<(TrafficLane, float, float)>();

    private Vector3 dir, right;

    public void Configure(Kind k, IntersectionController c, Approach a, Vector3 a0, Vector3 b0, float width)
    {
        kind = k; controller = c; arm = a; endA = a0; endB = b0; widthM = width;
    }

    /// <summary>Altura del asfalto en el cruce (los peatones caminan a esta altura sobre la calzada).</summary>
    public float RoadY { get; private set; }

    private void Awake()
    {
        dir = Flat(endB - endA).normalized;
        right = Vector3.Cross(Vector3.up, dir);

        // Altura de la calzada en el centro del cruce: el NavMesh une las veredas (~0.2 m) en linea recta y el
        // peaton quedaba flotando sobre la calzada
        Vector3 mid = (endA + endB) * 0.5f;
        int ground = LayerMask.GetMask("Ground");
        RoadY = Physics.Raycast(mid + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 6f, ground == 0 ? ~0 : ground)
            ? hit.point.y : mid.y - 0.02f;

        // Cruce de cada carril de autos con el eje del paso (en planta)
        foreach (var lane in FindObjectsByType<TrafficLane>(FindObjectsSortMode.None))
        {
            var c = lane.GetComponent<SplineContainer>();
            if (c == null || c.Spline == null) continue;
            float len = c.Spline.GetLength();
            if (len < 0.5f) continue;
            Vector3 prev = c.transform.TransformPoint((Vector3)c.Spline.EvaluatePosition(0f));
            int n = Mathf.Max(2, Mathf.CeilToInt(len / 0.5f) + 1);
            for (int i = 1; i < n; i++)
            {
                float s = len * i / (n - 1);
                float t = c.Spline.ConvertIndexUnit(s, PathIndexUnit.Distance, PathIndexUnit.Normalized);
                Vector3 p = c.transform.TransformPoint((Vector3)c.Spline.EvaluatePosition(t));
                if (TrafficRules.SegmentIntersection(XZ(prev), XZ(p), XZ(endA), XZ(endB), out float ta, out float tb))
                {
                    float sHit = s - len / (n - 1) * (1f - ta);
                    LaneCrossings.Add((lane, sHit, tb * LengthM));
                    lane.AddCrosswalk(this, sHit, tb * LengthM);
                    break;
                }
                prev = p;
            }
        }
    }

    /// <summary>Coordenadas locales: u a lo largo de A->B (0..largo), v lateral.</summary>
    public void Local(Vector3 world, out float u, out float v)
    {
        Vector3 d = Flat(world - endA);
        u = Vector3.Dot(d, dir);
        v = Vector3.Dot(d, right);
    }

    /// <summary>True si el punto esta sobre la calzada del cruce (entre soleras) con margen lateral.</summary>
    public bool IsOnRoadPart(Vector3 world, float lateralMargin = 0f)
    {
        Local(world, out float u, out float v);
        return u >= 0f && u <= LengthM && Mathf.Abs(v) <= widthM * 0.5f + lateralMargin;
    }

    public PedestrianState PedState => controller != null ? controller.GetPedestrianState(arm)
                                                          : new PedestrianState(PedestrianPhase.Walk, 0f, float.PositiveInfinity);

    public float ClearanceS => controller != null ? controller.Timing.pedestrianClearanceS : 0f;

    // Franja del carril: medio ancho del carril + este margen (cuerpo del peaton y holgura)
    private const float BandMarginM = 1.0f;
    // Holgura de tiempo: el peaton que va hacia la franja no debe llegar antes de que el auto la deje libre + esto
    private const float ApproachSafetyS = 2.0f;

    /// <summary>
    /// Un auto en 'lane' que llega al cruce (o ya esta sobre el) debe detenerse: hay un peaton en su franja del
    /// cruce, o uno que va hacia ella y llegaria antes de que el auto termine de pasar; o (paso cebra) alguien
    /// esperando y el auto puede detenerse comodamente.
    /// distanceToStopM: del frente del auto al punto de detencion (1 m antes del borde); negativo = ya lo paso.
    /// </summary>
    public bool CarMustYield(TrafficLane lane, float u, float laneHalfWidth, float distanceToStopM,
                             float carSpeed, float carLength, float comfortDecel, float minGap)
    {
        // Tiempo hasta que la cola del auto deja libre el cruce
        float clearDist = Mathf.Max(distanceToStopM, 0f) + 1f + widthM + carLength;
        float tCar = clearDist / Mathf.Max(carSpeed, 1f);
        foreach (var p in OnCrossing)
        {
            Local(p.Position, out float pu, out _);
            float du = pu - u;
            float outside = Mathf.Abs(du) - (laneHalfWidth + BandMarginM);
            if (outside <= 0f) return true;                                   // en la franja del carril
            float toward = -Mathf.Sign(du) * Vector3.Dot(Heading(p), dir) * Mathf.Max(p.SpeedMps, p.DesiredSpeedMps);
            if (toward > 0.05f && outside / toward <= tCar + ApproachSafetyS) return true;  // va hacia ella
        }
        if (kind == Kind.Zebra && Waiting.Count > 0)
            return distanceToStopM >= PedestrianRules.ComfortStopDistance(carSpeed, comfortDecel, minGap) - minGap;
        return false;
    }

    /// <summary>
    /// Auto que ya esta sobre el cruce: hay un peaton justo delante de su frente? (Los que quedan a su costado
    /// lo esperan a el; si el auto tambien los esperara, ninguno avanzaria.)
    /// </summary>
    public bool PedestrianAheadOf(Transform car, float carLength, float carWidth)
    {
        foreach (var p in OnCrossing)
        {
            Vector3 local = car.InverseTransformPoint(p.Position);
            if (local.z > carLength * 0.5f - 0.2f && local.z < carLength * 0.5f + 3f &&
                Mathf.Abs(local.x) < carWidth * 0.5f + 0.6f) return true;
        }
        return false;
    }

    /// <summary>Direccion en que camina el peaton (hacia su esquina siguiente).</summary>
    private static Vector3 Heading(PedestrianAgent p)
    {
        if (p.Corners != null && p.CornerIndex < p.Corners.Length)
        {
            Vector3 d = Flat(p.Corners[p.CornerIndex] - p.Position);
            if (d.sqrMagnitude > 1e-4f) return d.normalized;
        }
        return Flat(p.transform.forward).normalized;
    }

    private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    private static Vector2 XZ(Vector3 v) => new Vector2(v.x, v.z);

    private void OnDrawGizmos()
    {
        Gizmos.color = kind == Kind.Zebra ? Color.white : Color.green;
        Vector3 d = Flat(endB - endA).normalized, r = Vector3.Cross(Vector3.up, d) * widthM * 0.5f;
        Vector3 a = endA + Vector3.up * 0.05f, b = endB + Vector3.up * 0.05f;
        Gizmos.DrawLine(a - r, b - r); Gizmos.DrawLine(a + r, b + r);
        Gizmos.DrawLine(a - r, a + r); Gizmos.DrawLine(b - r, b + r);
    }
}
