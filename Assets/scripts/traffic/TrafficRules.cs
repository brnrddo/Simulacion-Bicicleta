// ============================================================
// TrafficRules.cs
// Reglas y geometria puras de giros y derecho de paso (T3), sin
// MonoBehaviour, para poder testearlas en EditMode.
// ============================================================

using System.Collections.Generic;
using UnityEngine;

/// <summary>Movimiento de un conector de interseccion. None = carril normal (no es conector).</summary>
public enum MovementKind { None, Straight, Right, Left }

/// <summary>Control del acceso de llegada.</summary>
public enum ApproachControl { Signal, Priority, Stop, Yield }

/// <summary>
/// Como trata un conector un punto de conflicto con otro conector:
///  Yield         = cede: espera brecha suficiente en el trafico que llega por el otro conector.
///  OccupancyOnly = solo espera si hay un auto DENTRO del otro conector antes del punto
///                  (conflictos separados por el semaforo o de igual prioridad).
/// </summary>
public enum ConflictMode { Yield, OccupancyOnly }

public static class TrafficRules
{
    /// <summary>Velocidad maxima en curva: v = sqrt(a_lat * R).</summary>
    public static float CurveSpeedLimitMps(float minRadiusM, float lateralAccelMps2)
    {
        if (float.IsInfinity(minRadiusM) || minRadiusM <= 0f) return float.PositiveInfinity;
        return Mathf.Sqrt(Mathf.Max(lateralAccelMps2, 0f) * minRadiusM);
    }

    /// <summary>
    /// Anticipacion: velocidad deseada efectiva para llegar a un tramo mas lento a distancia d
    /// frenando con b: v0_eff = min(v0, sqrt(v_lim^2 + 2 b d)).
    /// </summary>
    public static float AnticipatedDesiredSpeed(float v0, float nextLimitMps, float distanceM, float decelMps2)
    {
        if (distanceM < 0f) distanceM = 0f;
        float v = Mathf.Sqrt(nextLimitMps * nextLimitMps + 2f * Mathf.Max(decelMps2, 0.1f) * distanceM);
        return Mathf.Min(v0, v);
    }

    /// <summary>Aceptacion de brecha: entra si el trafico en conflicto llega en mas de criticalGapS.</summary>
    public static bool GapAccepted(float timeToConflictS, float criticalGapS) => timeToConflictS >= criticalGapS;

    /// <summary>Tiempo de llegada a un punto a distancia d con velocidad v (v &lt; 0.5 m/s cuenta como detenido).</summary>
    public static float TimeToReach(float distanceM, float speedMps, float stoppedNearM = 3f)
    {
        if (distanceM <= 0f) return 0f;
        if (speedMps < 0.5f) return distanceM <= stoppedNearM ? 0f : float.PositiveInfinity;
        return distanceM / speedMps;
    }

    /// <summary>
    /// Prioridad entre dos movimientos que se cruzan (sin separacion por semaforo):
    /// acceso secundario (PARE/CEDA) cede al principal; entre iguales, izquierda cede a todos
    /// y derecha cede a recto. Devuelve true si A debe ceder a B.
    /// </summary>
    public static bool Yields(MovementKind a, ApproachControl ca, MovementKind b, ApproachControl cb)
    {
        bool minorA = ca == ApproachControl.Stop || ca == ApproachControl.Yield;
        bool minorB = cb == ApproachControl.Stop || cb == ApproachControl.Yield;
        if (minorA != minorB) return minorA;
        return Rank(a) > Rank(b);
    }

    private static int Rank(MovementKind k) => k == MovementKind.Straight ? 0 : k == MovementKind.Right ? 1 : 2;

    /// <summary>
    /// Zona de conflicto entre dos trayectorias en planta (polilineas XZ como Vector2): devuelve, sobre
    /// cada una, la distancia recorrida hasta el PRIMER punto que queda a menos de 'zoneM' de la otra
    /// (inicio de la zona). Sirve igual para cruces (la zona empieza poco antes del cruce) y para
    /// convergencias, donde las trayectorias se acercan gradualmente antes de unirse.
    /// zoneM = suma de los medios anchos de los dos vehiculos + margen. False si nunca se acercan.
    /// </summary>
    public static bool FindConflict(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b, float zoneM,
                                    out float sA, out float sB) =>
        FindConflict(a, b, zoneM, out sA, out _, out sB, out _);

    /// <summary>Igual, devolviendo tambien el FIN de la zona sobre cada trayectoria (ultimo punto a menos de zoneM).</summary>
    public static bool FindConflict(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b, float zoneM,
                                    out float sA, out float sAEnd, out float sB, out float sBEnd)
    {
        bool fa = WithinRange(a, b, zoneM, out sA, out sAEnd);
        bool fb = WithinRange(b, a, zoneM, out sB, out sBEnd);
        return fa && fb;
    }

    /// <summary>Distancias recorridas sobre 'path' hasta el primer y el ultimo punto a menos de 'zoneM' de 'other'.</summary>
    private static bool WithinRange(IReadOnlyList<Vector2> path, IReadOnlyList<Vector2> other, float zoneM,
                                    out float first, out float last)
    {
        first = last = 0f;
        bool found = false;
        float acc = 0f;
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0) acc += Vector2.Distance(path[i - 1], path[i]);
            if (DistanceToPolyline(path[i], other) >= zoneM) continue;
            if (!found) { first = acc; found = true; }
            last = acc;
        }
        return found;
    }

    public static float DistanceToPolyline(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        float best = float.PositiveInfinity;
        for (int j = 0; j < poly.Count - 1; j++)
        {
            Vector2 a = poly[j], ab = poly[j + 1] - a;
            float t = ab.sqrMagnitude > 1e-9f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
        }
        return best;
    }

    public static bool SegmentIntersection(Vector2 p, Vector2 p2, Vector2 q, Vector2 q2, out float t, out float u)
    {
        Vector2 r = p2 - p, s = q2 - q;
        float denom = r.x * s.y - r.y * s.x;
        t = u = 0f;
        if (Mathf.Abs(denom) < 1e-9f) return false;  // paralelos
        Vector2 qp = q - p;
        t = (qp.x * s.y - qp.y * s.x) / denom;
        u = (qp.x * r.y - qp.y * r.x) / denom;
        return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
    }

}
