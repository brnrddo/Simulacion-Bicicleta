// ============================================================
// IntersectionCurbFitter.cs
// SimBici > Fit Intersections To Curbs: ajusta las intersecciones a las
// soleras reales de la ciudad. Antes las lineas de detencion estaban a
// 8.5-9 m del centro, dentro de la curva de las esquinas (que termina a
// 8.5-11 m): los pasos peatonales quedaban en la esquina y se superponian, y
// varios postes de semaforo quedaban en la calzada.
//
// Por brazo (solo intersecciones con semaforo y brazos con PARE/CEDA):
//   1. Mide con el NavMesh de veredas (area "Vereda") la solera derecha e
//      izquierda en el tramo recto y la distancia d_esq del centro a la que
//      termina la curva de la esquina (ambos lados).
//   2. Con semaforo: paso peatonal en [d_esq + 0.3, d_esq + 0.3 + ancho] y
//      linea de detencion 'crosswalkSetbackM' mas afuera. PARE/CEDA: linea a
//      d_esq + 0.5 (al empezar la curva).
//   3. Recorta el final de 'feedFrom' y el inicio de 'continueTo' si quedan a
//      menos de 3 m de la linea (los extremos del carril In/Out generado).
//   4. Regenera la interseccion (carriles, conflictos, postes, pintura).
// Despues: SimBici > Generate Cyclist Network, Analyze Lane Room y
// Build Pedestrian Setup (pasos peatonales). Necesita el NavMesh de peatones.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Splines;

public static class IntersectionCurbFitter
{
    private const int AreaVereda = 3;
    private const float CornerClearanceM = 0.3f;   // borde interior del paso, pasado el fin de la curva
    private const float MinorLineFromCornerM = 0.5f;
    private const float MinGeneratedLaneM = 3f;     // largo minimo del carril In / Out generado
    private const float StraightFromM = 13f, StraightToM = 25f;
    private const float CurbToleranceM = 0.15f;

    [MenuItem("SimBici/Fit Intersections To Curbs")]
    private static void Menu() => Debug.Log(FitAll());

    public static string FitAll()
    {
        var log = new StringBuilder("[IntersectionCurbFitter]\n");
        var builders = Object.FindObjectsByType<IntersectionLaneBuilder>(FindObjectsSortMode.None)
                             .OrderBy(b => b.prefix, System.StringComparer.Ordinal).ToList();
        foreach (var b in builders)
        {
            bool signalized = b.controller != null;
            bool hasMinor = b.arms.Any(a => b.EffectiveControl(a) == ApproachControl.Stop || b.EffectiveControl(a) == ApproachControl.Yield);
            if (!signalized && !hasMinor) continue;
            if (!Fit(b, log)) continue;
            log.AppendLine(IntersectionLaneBuilderEditor.Generate(b));
        }
        EditorSceneManager.MarkSceneDirty(builders[0].gameObject.scene);
        return log.ToString();
    }

    private static bool Fit(IntersectionLaneBuilder b, StringBuilder log)
    {
        Vector3 c = b.transform.position;
        bool signalized = b.controller != null;
        var so = new SerializedObject(b);
        var armsProp = so.FindProperty("arms");
        bool any = false;
        for (int i = 0; i < b.arms.Count; i++)
        {
            var arm = b.arms[i];
            ApproachControl ctrl = b.EffectiveControl(arm);
            bool crosswalk = signalized;                       // con semaforo: paso peatonal en todos los brazos
            bool minorLine = ctrl == ApproachControl.Stop || ctrl == ApproachControl.Yield;
            if (!crosswalk && !minorLine) continue;

            Vector3 dir = b.ArmDirection(arm.approach);
            Vector3 rightIn = Vector3.Cross(Vector3.up, -dir);
            float curbR = StraightCurb(c, dir, rightIn), curbL = StraightCurb(c, dir, -rightIn);
            float endR = CornerEnd(c, dir, rightIn, curbR), endL = CornerEnd(c, dir, -rightIn, curbL);
            float corner = Mathf.Max(float.IsNaN(endR) ? 0f : endR, float.IsNaN(endL) ? 0f : endL);
            if (float.IsNaN(curbR) || corner <= 0f)
            {
                log.AppendLine($"  {b.prefix} {arm.approach}: no se pudo medir la solera, sin cambios");
                continue;
            }

            float stop = crosswalk ? corner + CornerClearanceM + arm.crosswalkWidthM + arm.crosswalkSetbackM
                                   : corner + MinorLineFromCornerM;
            stop = Mathf.Round(stop * 20f) / 20f;

            var e = armsProp.GetArrayElementAtIndex(i);
            e.FindPropertyRelative("stopLineDistanceM").floatValue = stop;
            e.FindPropertyRelative("curbDistanceM").floatValue = Mathf.Round(curbR * 100f) / 100f;
            e.FindPropertyRelative("leftCurbDistanceM").floatValue = float.IsNaN(curbL) ? 0f : Mathf.Round(curbL * 100f) / 100f;
            log.AppendLine($"  {b.prefix} {arm.approach}: soleras {curbR:F2} / {curbL:F2} m, fin de la curva a {corner:F2} m, " +
                           $"linea {arm.stopLineDistanceM:F2} -> {stop:F2} m{(crosswalk ? $" (paso {stop - arm.crosswalkSetbackM - arm.crosswalkWidthM:F2}-{stop - arm.crosswalkSetbackM:F2} m)" : "")}");

            float need = stop + MinGeneratedLaneM;
            if (arm.feedFrom != null)
            {
                float along = Vector3.Dot(EndPoint(arm.feedFrom, 1f) - c, dir);
                if (along < need) { Trim(arm.feedFrom, 0f, LaneLength(arm.feedFrom) - (need - along)); log.AppendLine($"    {arm.feedFrom.name}: final recortado {need - along:F2} m"); }
            }
            if (arm.continueTo != null)
            {
                float along = Vector3.Dot(EndPoint(arm.continueTo, 0f) - c, dir);
                if (along < need) { Trim(arm.continueTo, need - along, LaneLength(arm.continueTo)); log.AppendLine($"    {arm.continueTo.name}: inicio recortado {need - along:F2} m"); }
            }
            any = true;
        }
        Undo.RecordObject(b, "Ajustar interseccion a las soleras");
        so.ApplyModifiedProperties();
        return any;
    }

    // ------------------------------------------------------------
    // Medicion con el NavMesh de veredas
    // ------------------------------------------------------------

    /// <summary>Distancia del eje del brazo a la vereda, a una distancia d del centro (NaN si no hay vereda a menos de 15 m).</summary>
    private static float Curb(Vector3 c, Vector3 dir, Vector3 side, float d)
    {
        // Busqueda gruesa (0.5 m) y fina (0.05 m)
        for (float x = 0f; x < 15f; x += 0.5f)
        {
            if (!OnSidewalk(c + dir * d + side * x)) continue;
            float lo = Mathf.Max(0f, x - 0.5f);
            for (float xf = lo; xf <= x; xf += 0.05f)
                if (OnSidewalk(c + dir * d + side * xf)) return xf;
            return x;
        }
        return float.NaN;
    }

    /// <summary>Hay vereda en este punto (en planta, a menos de 6 cm; la altura del NavMesh no coincide con la del asfalto).</summary>
    private static bool OnSidewalk(Vector3 p)
    {
        p.y += 0.15f;
        if (!NavMesh.SamplePosition(p, out NavMeshHit h, 0.3f, 1 << AreaVereda)) return false;
        float dx = h.position.x - p.x, dz = h.position.z - p.z;
        return dx * dx + dz * dz < 0.06f * 0.06f;
    }

    /// <summary>Solera en el tramo recto del brazo: mediana entre 13 y 25 m del centro.</summary>
    private static float StraightCurb(Vector3 c, Vector3 dir, Vector3 side)
    {
        var v = new List<float>();
        for (float d = StraightFromM; d <= StraightToM; d += 0.5f)
        {
            float x = Curb(c, dir, side, d);
            if (!float.IsNaN(x)) v.Add(x);
        }
        if (v.Count < 5) return float.NaN;
        v.Sort();
        return v[v.Count / 2];
    }

    /// <summary>Distancia del centro a la que termina la curva de la esquina (desde ahi la solera esta en su valor recto).</summary>
    private static float CornerEnd(Vector3 c, Vector3 dir, Vector3 side, float straight)
    {
        if (float.IsNaN(straight)) return float.NaN;
        bool Straight(float d) { float x = Curb(c, dir, side, d); return !float.IsNaN(x) && Mathf.Abs(x - straight) <= CurbToleranceM; }
        for (float d = StraightFromM; d > 2f; d -= 0.5f)
        {
            if (Straight(d)) continue;
            for (float df = d + 0.4f; df > d; df -= 0.1f)   // afinar a 0.1 m
                if (!Straight(df)) return df + 0.1f;
            return d + 0.1f;
        }
        return 2f;
    }

    // ------------------------------------------------------------
    // Recorte de carriles
    // ------------------------------------------------------------
    private static float LaneLength(TrafficLane lane) => lane.GetComponent<SplineContainer>().Spline.GetLength();

    private static Vector3 EndPoint(TrafficLane lane, float t)
    {
        var c = lane.GetComponent<SplineContainer>();
        return c.transform.TransformPoint((Vector3)c.Spline.EvaluatePosition(t));
    }

    /// <summary>Deja solo el tramo [fromS, toS] del carril (remuestreado, nudos cada 3 m).</summary>
    private static void Trim(TrafficLane lane, float fromS, float toS)
    {
        var c = lane.GetComponent<SplineContainer>();
        var sp = c.Spline;
        float len = sp.GetLength();
        fromS = Mathf.Clamp(fromS, 0f, len); toS = Mathf.Clamp(toS, fromS + 1f, len);
        int knots = Mathf.Max(2, Mathf.RoundToInt((toS - fromS) / 3f)) + 1;
        var spline = new Spline();
        for (int k = 0; k < knots; k++)
        {
            float s = Mathf.Lerp(fromS, toS, (float)k / (knots - 1));
            float t = sp.ConvertIndexUnit(s, PathIndexUnit.Distance, PathIndexUnit.Normalized);
            spline.Add(new BezierKnot(sp.EvaluatePosition(t)), TangentMode.AutoSmooth);
        }
        Undo.RecordObject(c, "Recortar carril");
        c.Spline = spline;
        EditorUtility.SetDirty(c);
    }
}
