// ============================================================
// CyclistPath.cs
// Un tramo de la red de caminos del ciclista: un spline dibujado EN EL
// SENTIDO CORRECTO de circulacion (ciclovia, o lado derecho del carril
// si la calle no tiene ciclovia, o un giro dentro de una interseccion).
// Los tramos se conectan con 'next' formando la red que sigue RiderRoute.
// Se generan con SimBici > Generate Cyclist Network (ver CyclistNetwork).
// ============================================================

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

[RequireComponent(typeof(SplineContainer))]
public class CyclistPath : MonoBehaviour
{
    public enum Kind { BikeLane, LaneRightSide, Connector }

    [SerializeField] private Kind kind;
    [Tooltip("Carril de trafico del que se derivo este tramo (referencia para depurar).")]
    [SerializeField] private string sourceLane;
    [SerializeField] private List<CyclistPath> next = new List<CyclistPath>();

    public Kind PathKind => kind;
    public string SourceLane => sourceLane;
    public IReadOnlyList<CyclistPath> Next => next;
    public float LengthM { get; private set; }

    private SplineContainer container;
    private Spline spline;

    private void Awake() => EnsureInit();

    public void EnsureInit()
    {
        if (spline != null && container != null) return;
        container = GetComponent<SplineContainer>();
        spline = container.Spline;
        LengthM = spline.GetLength();
    }

    /// <summary>Proyecta un punto: distancia S desde el inicio del tramo y desplazamiento lateral (+ = derecha).</summary>
    public void Project(Vector3 worldPoint, out float sM, out float lateralM, out Vector3 nearestWorld, out Vector3 tangentWorld)
    {
        EnsureInit();
        float3 local = container.transform.InverseTransformPoint(worldPoint);
        SplineUtility.GetNearestPoint(spline, local, out float3 nearestLocal, out float t, 4, 2);
        sM = spline.ConvertIndexUnit(t, PathIndexUnit.Normalized, PathIndexUnit.Distance);

        tangentWorld = container.transform.TransformDirection((Vector3)spline.EvaluateTangent(t));
        tangentWorld.y = 0f;
        tangentWorld = tangentWorld.sqrMagnitude > 1e-6f ? tangentWorld.normalized : transform.forward;

        nearestWorld = container.transform.TransformPoint((Vector3)nearestLocal);
        Vector3 delta = worldPoint - nearestWorld;
        delta.y = 0f;
        lateralM = Vector3.Dot(delta, Vector3.Cross(Vector3.up, tangentWorld));
    }

    /// <summary>Punto y direccion horizontal a una distancia S desde el inicio del tramo.</summary>
    public Vector3 GetPoint(float sM, out Vector3 forwardWorld)
    {
        EnsureInit();
        float t = spline.ConvertIndexUnit(Mathf.Clamp(sM, 0f, LengthM), PathIndexUnit.Distance, PathIndexUnit.Normalized);
        forwardWorld = container.transform.TransformDirection((Vector3)spline.EvaluateTangent(t));
        forwardWorld.y = 0f;
        forwardWorld = forwardWorld.sqrMagnitude > 1e-6f ? forwardWorld.normalized : Vector3.forward;
        return container.transform.TransformPoint((Vector3)spline.EvaluatePosition(t));
    }

    private void OnDrawGizmos()
    {
        var c = GetComponent<SplineContainer>();
        if (c == null || c.Spline == null || c.Spline.Count < 2) return;
        var sp = c.Spline;
        float len = sp.GetLength();
        if (len < 0.05f) return;

        // Flechas cian cada 10 m en el sentido de circulacion
        Gizmos.color = kind == Kind.Connector ? new Color(0.4f, 1f, 1f, 0.6f) : Color.cyan;
        for (float d = 1f; d < len; d += 10f)
        {
            float t = sp.ConvertIndexUnit(d, PathIndexUnit.Distance, PathIndexUnit.Normalized);
            Vector3 p = c.transform.TransformPoint((Vector3)sp.EvaluatePosition(t)) + Vector3.up * 0.15f;
            Vector3 f = c.transform.TransformDirection((Vector3)sp.EvaluateTangent(t)).normalized;
            Vector3 r = Vector3.Cross(Vector3.up, f);
            Gizmos.DrawLine(p, p - f * 0.8f + r * 0.4f);
            Gizmos.DrawLine(p, p - f * 0.8f - r * 0.4f);
        }
    }
}
