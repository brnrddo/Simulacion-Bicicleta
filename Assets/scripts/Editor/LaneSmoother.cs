// ============================================================
// LaneSmoother.cs
// SimBici > Smooth Lanes: quita las ondulaciones de los carriles alineados a
// la solera. LaneCurbAligner mide la solera cada 2 m en pasos de 5 cm y deja un
// nudo por muestra; ese escalonado hace oscilar el rumbo (hasta ~5 grados/m) y
// los autos, que se orientan con la tangente, serpentean en su carril.
//
// Metodo (por carril, extremos y su direccion fijos para no romper las
// conexiones con los conectores de las intersecciones):
//   1. Muestreo cada 0.5 m: rumbo theta(s) de cada segmento (desenrollado).
//   2. Filtro gaussiano sobre theta (sigma = 4 m, extremos por reflexion par).
//      Suavizar el rumbo, y no la posicion, conserva el giro total y el radio
//      de las curvas (no las "achica") y crea transiciones de curvatura suaves.
//   3. Correccion del rumbo cerca de cada extremo hacia el rumbo original del
//      extremo (rampa de 3 m) para empalmar sin quiebre con los conectores.
//   4. Reintegracion de posiciones desde el inicio y reparto lineal del error
//      de cierre para que el final coincida con el original.
//   5. Spline nuevo con nudos cada 3 m (AutoSmooth).
// No toca conectores ni carriles de IntersectionLaneBuilder. Un solo Undo.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

public static class LaneSmoother
{
    private const float SampleM = 0.5f;
    private const float SigmaM = 4f;
    private const float EndBlendM = 3f;
    private const float KnotSpacingM = 3f;

    [MenuItem("SimBici/Smooth Lanes")]
    private static void Menu() => Debug.Log(SmoothAll());

    public static string SmoothAll()
    {
        var log = new StringBuilder("[LaneSmoother]\n");
        var lanes = Object.FindObjectsByType<TrafficLane>(FindObjectsSortMode.None)
                          .Where(l => !l.IsConnector && l.GetComponentInParent<IntersectionLaneBuilder>() == null)
                          .OrderBy(l => l.name, System.StringComparer.Ordinal).ToList();

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Suavizar carriles");
        int group = Undo.GetCurrentGroup();
        int changed = 0;
        foreach (var lane in lanes)
        {
            var c = lane.GetComponent<SplineContainer>();
            if (!Smooth(c, out float maxDev, out float closure, out int before, out int after)) continue;
            changed++;
            log.AppendLine($"  {lane.name}: cambios de signo de la curvatura {before} -> {after}, " +
                           $"desplazamiento maximo {maxDev:F3} m, cierre {closure:F3} m");
        }
        Undo.CollapseUndoOperations(group);
        if (changed > 0) EditorSceneManager.MarkSceneDirty(lanes[0].gameObject.scene);
        log.Insert(0, $"{changed} carriles suavizados. ");
        return log.ToString();
    }

    /// <summary>Suaviza un spline en planta. Devuelve false si es muy corto.</summary>
    public static bool Smooth(SplineContainer c, out float maxDeviation, out float closureError,
                              out int wigglesBefore, out int wigglesAfter)
    {
        maxDeviation = closureError = 0f;
        wigglesBefore = wigglesAfter = 0;
        var sp = c.Spline;
        float len = sp.GetLength();
        if (len < 4f * SampleM || sp.Count < 3) return false;

        // 1. Muestreo
        int n = Mathf.CeilToInt(len / SampleM) + 1;
        var p = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float t = sp.ConvertIndexUnit(len * i / (n - 1), PathIndexUnit.Distance, PathIndexUnit.Normalized);
            p[i] = c.transform.TransformPoint((Vector3)sp.EvaluatePosition(t));
        }
        Vector3 startDir = Flat(c.transform.TransformDirection((Vector3)sp.EvaluateTangent(0f)));
        Vector3 endDir = Flat(c.transform.TransformDirection((Vector3)sp.EvaluateTangent(1f)));

        int m = n - 1;                       // segmentos
        var theta = new float[m];
        var segLen = new float[m];
        for (int i = 0; i < m; i++)
        {
            Vector3 d = Flat(p[i + 1] - p[i], normalize: false);
            segLen[i] = d.magnitude;
            float a = Mathf.Atan2(d.x, d.z);
            theta[i] = i == 0 ? a : theta[i - 1] + Mathf.DeltaAngle(theta[i - 1] * Mathf.Rad2Deg, a * Mathf.Rad2Deg) * Mathf.Deg2Rad;
        }
        wigglesBefore = CurvatureSignChanges(theta);

        // 2. Filtro gaussiano del rumbo (reflexion par en los extremos)
        float sigma = SigmaM / SampleM;
        int half = Mathf.CeilToInt(3f * sigma);
        var w = new float[half + 1];
        for (int k = 0; k <= half; k++) w[k] = Mathf.Exp(-0.5f * k * k / (sigma * sigma));
        var sm = new float[m];
        for (int i = 0; i < m; i++)
        {
            float acc = 0f, ws = 0f;
            for (int k = -half; k <= half; k++)
            {
                int j = i + k;
                if (j < 0) j = -j - 1;
                if (j >= m) j = 2 * m - j - 1;
                j = Mathf.Clamp(j, 0, m - 1);
                float wk = w[Mathf.Abs(k)];
                acc += wk * theta[j]; ws += wk;
            }
            sm[i] = acc / ws;
        }

        // 3. Empalme con el rumbo original de cada extremo
        float a0 = Unwrap(Mathf.Atan2(startDir.x, startDir.z), sm[0]);
        float a1 = Unwrap(Mathf.Atan2(endDir.x, endDir.z), sm[m - 1]);
        float c0 = a0 - sm[0], c1 = a1 - sm[m - 1];
        for (int i = 0; i < m; i++)
        {
            float sFrom0 = (i + 0.5f) * SampleM, sFromEnd = (m - i - 0.5f) * SampleM;
            sm[i] += c0 * Mathf.SmoothStep(1f, 0f, sFrom0 / EndBlendM) + c1 * Mathf.SmoothStep(1f, 0f, sFromEnd / EndBlendM);
        }
        wigglesAfter = CurvatureSignChanges(sm);

        // 4. Reintegracion y reparto del error de cierre
        var q = new Vector3[n];
        q[0] = p[0];
        var cum = new float[n];
        for (int i = 0; i < m; i++)
        {
            q[i + 1] = q[i] + new Vector3(Mathf.Sin(sm[i]), 0f, Mathf.Cos(sm[i])) * segLen[i];
            cum[i + 1] = cum[i] + segLen[i];
        }
        Vector3 err = Flat(p[n - 1] - q[n - 1], normalize: false);
        closureError = err.magnitude;
        for (int i = 0; i < n; i++)
        {
            q[i] += err * (cum[i] / cum[n - 1]);
            q[i].y = p[i].y;
            maxDeviation = Mathf.Max(maxDeviation, Flat(q[i] - p[i], normalize: false).magnitude);
        }
        q[0] = p[0]; q[n - 1] = p[n - 1];

        // 5. Spline nuevo
        int knots = Mathf.Max(2, Mathf.RoundToInt(len / KnotSpacingM)) + 1;
        var spline = new Spline();
        for (int k = 0; k < knots; k++)
        {
            float idx = (float)k / (knots - 1) * (n - 1);
            int i0 = Mathf.FloorToInt(idx), i1 = Mathf.Min(i0 + 1, n - 1);
            Vector3 pos = Vector3.Lerp(q[i0], q[i1], idx - i0);
            spline.Add(new BezierKnot(c.transform.InverseTransformPoint(pos)), TangentMode.AutoSmooth);
        }
        Undo.RecordObject(c, "Suavizar carril");
        c.Spline = spline;
        EditorUtility.SetDirty(c);
        return true;
    }

    /// <summary>Cambios de signo de la curvatura con |k| > 0.5 grados/m (ondulaciones).</summary>
    public static int CurvatureSignChanges(IReadOnlyList<float> theta)
    {
        int changes = 0; float prev = 0f;
        for (int i = 1; i < theta.Count; i++)
        {
            float k = (theta[i] - theta[i - 1]) * Mathf.Rad2Deg / SampleM;
            if (Mathf.Abs(k) <= 0.5f) continue;
            if (prev != 0f && Mathf.Sign(k) != Mathf.Sign(prev)) changes++;
            prev = k;
        }
        return changes;
    }

    private static float Unwrap(float a, float reference) =>
        reference + Mathf.DeltaAngle(reference * Mathf.Rad2Deg, a * Mathf.Rad2Deg) * Mathf.Deg2Rad;

    private static Vector3 Flat(Vector3 v, bool normalize = true)
    {
        v.y = 0f;
        return normalize && v.sqrMagnitude > 1e-8f ? v.normalized : v;
    }
}
