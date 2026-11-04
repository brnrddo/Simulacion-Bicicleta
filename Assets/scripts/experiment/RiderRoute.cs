// ============================================================
// RiderRoute.cs
// Sigue al ciclista sobre la RED de caminos posibles (CyclistNetwork):
// ciclovias en su sentido, lado derecho del carril donde no hay ciclovia
// y todos los giros de las intersecciones. El ciclista elige su camino;
// esta clase identifica en que tramo va y calcula cada FixedUpdate:
//   - CurrentPath / PathS: tramo actual y metros desde su inicio
//   - DistanceAlongM: metros recorridos sobre la red (odometro) desde el
//     ultimo (re)inicio -> dispara eventos por distancia
//   - LateralOffsetM: desviacion lateral respecto del tramo
//     (+ = derecha, - = izquierda) -> variable dependiente
//
// Seguimiento:
//   1. Sin posicion conocida: busca en toda la red el tramo mas cercano con
//      la direccion de marcha del ciclista.
//   2. Al llegar al final del tramo pasa al sucesor que mejor calza.
//   3. Dentro de una interseccion todos los giros parten del mismo punto: si un
//      giro "hermano" calza mejor que el elegido, se cambia a ese.
//   4. Si queda lejos de la red demasiados ticks, vuelve a buscar (route_resync).
// ============================================================

using System;
using System.Globalization;
using UnityEngine;

[DefaultExecutionOrder(50)]  // despues de BicycleController (0), antes del logger (100)
public class RiderRoute : MonoBehaviour
{
    [Header("Referencias")]
    [SerializeField] private CyclistNetwork network;
    [SerializeField] private BicycleController bike;

    [Header("Seguimiento")]
    [Tooltip("Para la busqueda global: un tramo solo es candidato si el ciclista esta a menos de esto (m).")]
    [SerializeField] private float acquireMaxLateralM = 6f;
    [Tooltip("Un giro hermano reemplaza al actual si calza al menos esto mejor (m).")]
    [SerializeField] private float siblingSwitchMarginM = 0.5f;
    [Tooltip("Desviacion lateral a partir de la cual se considera fuera de la red.")]
    [SerializeField] private float offRouteThresholdM = 4f;
    [Tooltip("Ticks seguidos fuera de la red antes de volver a buscar en toda la red.")]
    [SerializeField] private int resyncAfterTicks = 45;

    [Header("Debug (solo lectura, visible en Play)")]
    [SerializeField] private string debugPath;
    [SerializeField] private float debugPathS;
    [SerializeField] private float debugDistanceM;
    [SerializeField] private float debugLateralM;

    // ------------------------------------------------------------
    // Estado publico
    // ------------------------------------------------------------
    /// <summary>Largo total de la red (suma de tramos).</summary>
    public float LengthM { get; private set; }
    public float DistanceAlongM { get; private set; }
    public float LateralOffsetM { get; private set; }
    public Vector3 NearestPointWorld { get; private set; }
    public Vector3 TangentWorld { get; private set; } = Vector3.forward;
    public bool IsOffRoute { get; private set; }
    /// <summary>True al llegar al final de un tramo sin sucesores (salida de la red).</summary>
    public bool HasFinished { get; private set; }
    public bool HasFix { get; private set; }
    public CyclistPath CurrentPath { get; private set; }
    public float PathS { get; private set; }
    public CyclistNetwork Network => network;

    /// <summary>Cambio de tramo: (anterior, nuevo, avanzo = true si paso al sucesor por el final del anterior).</summary>
    public event Action<CyclistPath, CyclistPath, bool> PathChanged;

    private CyclistPath previousPath;   // de donde venia (para los giros hermanos)
    private float pathBaseM;            // odometro en S = 0 del tramo actual
    private int offTicks;
    private bool wasOffRoute;

    private void Awake()
    {
        if (network == null) network = FindFirstObjectByType<CyclistNetwork>();
        if (network == null || bike == null || network.Paths.Count == 0)
        {
            Debug.LogError("[RiderRoute] Falta CyclistNetwork (con tramos generados) o BicycleController.");
            enabled = false;
            return;
        }
        LengthM = network.TotalLengthM;
    }

    private void FixedUpdate()
    {
        Vector3 rider = bike.RiderPosition;

        if (!HasFix)
        {
            Acquire(rider, publishResync: false);
            if (!HasFix) return;
        }

        CurrentPath.Project(rider, out float s, out float lat, out Vector3 nearest, out Vector3 tangent);

        // Avance: pasar al sucesor que mejor calza (puede encadenar tramos cortos)
        int guard = 0;
        while (s >= CurrentPath.LengthM - 0.01f && guard++ < 6)
        {
            if (CurrentPath.Next.Count == 0)
            {
                if (!HasFinished)
                {
                    HasFinished = true;
                    EventBus.Publish("route_finished", "rider", "path=" + CurrentPath.name);
                }
                break;
            }

            CyclistPath best = null;
            float bestScore = float.PositiveInfinity, bs = 0f, bl = 0f;
            Vector3 bn = nearest, bt = tangent;
            foreach (var n in CurrentPath.Next)
            {
                n.Project(rider, out float ns, out float nl, out Vector3 nn, out Vector3 nt);
                float score = Mathf.Abs(nl);
                if (score < bestScore) { bestScore = score; best = n; bs = ns; bl = nl; bn = nn; bt = nt; }
            }

            var prev = CurrentPath;
            pathBaseM += prev.LengthM;
            previousPath = prev;
            SetPath(best);
            s = bs; lat = bl; nearest = bn; tangent = bt;
            PathChanged?.Invoke(prev, best, true);
        }

        // Giros hermanos: todos parten del final de previousPath
        if (previousPath != null && previousPath.Next.Count > 1)
        {
            foreach (var sib in previousPath.Next)
            {
                if (sib == CurrentPath) continue;
                sib.Project(rider, out float ss, out float sl, out Vector3 sn, out Vector3 st);
                if (ss < sib.LengthM - 0.01f && Mathf.Abs(sl) + siblingSwitchMarginM < Mathf.Abs(lat))
                {
                    var prev = CurrentPath;
                    SetPath(sib);
                    s = ss; lat = sl; nearest = sn; tangent = st;
                    PathChanged?.Invoke(prev, sib, false);
                    break;
                }
            }
        }

        // Fuera de la red demasiado tiempo: volver a buscar
        if (Mathf.Abs(lat) > offRouteThresholdM)
        {
            if (++offTicks >= resyncAfterTicks)
            {
                offTicks = 0;
                Acquire(rider, publishResync: true);
                if (!HasFix) return;
                CurrentPath.Project(rider, out s, out lat, out nearest, out tangent);
            }
        }
        else offTicks = 0;

        PathS = s;
        DistanceAlongM = pathBaseM + s;
        LateralOffsetM = lat;
        NearestPointWorld = nearest;
        TangentWorld = tangent;

        IsOffRoute = Mathf.Abs(LateralOffsetM) > offRouteThresholdM;
        if (IsOffRoute != wasOffRoute)
        {
            EventBus.Publish(IsOffRoute ? "off_route_start" : "off_route_end", "rider", "path=" + CurrentPath.name);
            wasOffRoute = IsOffRoute;
        }

        debugPath = CurrentPath.name;
        debugPathS = PathS;
        debugDistanceM = DistanceAlongM;
        debugLateralM = LateralOffsetM;
    }

    /// <summary>Busca en toda la red el tramo que mejor calza (cercania + direccion de marcha).</summary>
    private void Acquire(Vector3 rider, bool publishResync)
    {
        Vector3 fwd = bike.transform.forward;
        fwd.y = 0f;
        fwd.Normalize();
        bool moving = bike.CurrentSpeedMps > 0.5f;

        CyclistPath best = null;
        float bestScore = float.PositiveInfinity, bestS = 0f;
        foreach (var p in network.Paths)
        {
            p.Project(rider, out float s, out float lat, out _, out Vector3 tangent);
            if (Mathf.Abs(lat) > acquireMaxLateralM) continue;
            // Puntos fuera del tramo (antes del inicio o despues del final) no cuentan
            if ((s <= 0.01f || s >= p.LengthM - 0.01f) && p.LengthM > 0.5f) continue;

            float align = Vector3.Dot(fwd, tangent);
            float score = Mathf.Abs(lat) + (align < 0f ? (moving ? 20f : 2f) : 0f)
                          + (p.PathKind == CyclistPath.Kind.Connector ? 0.5f : 0f);
            if (score < bestScore) { bestScore = score; best = p; bestS = s; }
        }

        if (best == null)
        {
            HasFix = false;
            return;
        }

        string from = CurrentPath != null ? CurrentPath.name : "";
        SetPath(best);
        previousPath = null;
        pathBaseM = -bestS;   // el odometro parte en 0 en el punto de (re)inicio
        HasFix = true;
        HasFinished = false;

        if (publishResync)
            EventBus.Publish("route_resync", "rider", string.Format(CultureInfo.InvariantCulture,
                "from_path={0};to_path={1};s_m={2:F2}", from, best.name, bestS));
    }

    private void SetPath(CyclistPath p)
    {
        CurrentPath = p;
    }

    // ------------------------------------------------------------
    // API para otros sistemas (reinicio tras choque, eventos)
    // ------------------------------------------------------------

    /// <summary>Pose de reaparicion: inicio de la red + extraM metros sobre el tramo de inicio.</summary>
    public Vector3 GetRespawnPose(float extraM, out Vector3 forwardWorld) => network.GetStartPose(extraM, out forwardWorld);

    /// <summary>
    /// Olvida la ultima posicion conocida. Llamar despues de teletransportar al
    /// ciclista (reinicio tras choque): el siguiente tick vuelve a buscar en la red.
    /// </summary>
    public void ForceResync()
    {
        HasFix = false;
        HasFinished = false;
        previousPath = null;
        offTicks = 0;
    }

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying || !HasFix || bike == null) return;

        // Linea amarilla: ciclista -> punto mas cercano. Flecha verde: direccion del tramo.
        Gizmos.color = IsOffRoute ? Color.red : Color.yellow;
        Gizmos.DrawLine(bike.RiderPosition, NearestPointWorld);
        Gizmos.DrawWireSphere(NearestPointWorld, 0.15f);
        Gizmos.color = Color.green;
        Gizmos.DrawLine(NearestPointWorld, NearestPointWorld + TangentWorld * 2f);
    }
}
