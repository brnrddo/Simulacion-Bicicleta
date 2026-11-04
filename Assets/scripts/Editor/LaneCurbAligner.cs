// ============================================================
// LaneCurbAligner.cs
// SimBici > Align Lanes To Curb: corrige la geometria de los carriles de
// trafico dibujados a mano para que vayan PARALELOS a la calzada.
// Para cada carril mide con rayos la distancia a la solera derecha; en sus
// tramos rectos esa distancia es la de referencia, y en las curvas desplaza el
// carril para mantenerla. Asi la distancia entre el auto y la ciclovia (que
// sigue las marcas pintadas) queda constante tambien en las curvas.
// Los carriles del circuito (Lane_Main*) se llevan a un perfil fijo: eje a 4.2 m de la
// solera (calzada pintada de 12 m). No toca conectores ni carriles generados por
// IntersectionLaneBuilder. Al final aplica LaneSmoother (quita el escalonado
// de la medicion). Un solo paso de Undo.
// Despues: regenerar intersecciones y SimBici > Generate Cyclist Network.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

public static class LaneCurbAligner
{
    private const float SampleM = 2f;
    private const float EndRampM = 4f;
    private const float StraightCurvature = 0.002f;  // 1/R: R > 500 m cuenta como recto

    // Perfil del circuito (calzada de 12 m pintada): ciclovia 2.4 m + carril 3.6 m + carril 3.6 m + ciclovia 2.4 m.
    // El eje de cada carril de autos queda a 2.4 + 1.8 = 4.2 m de su solera derecha.
    public const string RingLanePrefix = "Lane_Main";
    public const float RingLaneCurbDistanceM = 4.2f;

    [MenuItem("SimBici/Align Lanes To Curb")]
    private static void Menu() => Debug.Log(AlignAll());

    /// <summary>
    /// Carriles cuyo nombre empieza con fixedPrefix: a fixedCurbM de la solera en todo su largo (perfil fijo).
    /// Los demas: a la distancia que ya tienen en sus tramos rectos (solo se corrigen las curvas, extremos fijos).
    /// </summary>
    public static string AlignAll(string fixedPrefix = RingLanePrefix, float fixedCurbM = RingLaneCurbDistanceM)
    {
        var log = new StringBuilder("[LaneCurbAligner]\n");
        var lanes = Object.FindObjectsByType<TrafficLane>(FindObjectsSortMode.None)
                          .Where(l => !l.IsConnector && l.GetComponentInParent<IntersectionLaneBuilder>() == null)
                          .OrderBy(l => l.name, System.StringComparer.Ordinal).ToList();

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Alinear carriles a la solera");
        int group = Undo.GetCurrentGroup();
        int changed = 0;

        foreach (var lane in lanes)
        {
            var c = lane.GetComponent<SplineContainer>();
            var sp = c.Spline;
            float len = sp.GetLength();
            if (len < 30f) continue;

            int n = Mathf.CeilToInt(len / SampleM) + 1;
            var pts = new Vector3[n];
            var right = new Vector3[n];
            var curb = new float[n];
            var straight = new bool[n];
            for (int i = 0; i < n; i++)
            {
                float t = sp.ConvertIndexUnit(len * i / (n - 1), PathIndexUnit.Distance, PathIndexUnit.Normalized);
                pts[i] = c.transform.TransformPoint((Vector3)sp.EvaluatePosition(t));
                Vector3 f = c.transform.TransformDirection((Vector3)sp.EvaluateTangent(t));
                f.y = 0f;
                right[i] = Vector3.Cross(Vector3.up, f.normalized);
                curb[i] = CurbDistance(pts[i], right[i]);
                straight[i] = Mathf.Abs(SplineUtility.EvaluateCurvature(sp, t)) < StraightCurvature;
            }

            bool fixedProfile = !string.IsNullOrEmpty(fixedPrefix) && lane.name.StartsWith(fixedPrefix);
            var refs = Enumerable.Range(0, n).Where(i => straight[i] && !float.IsNaN(curb[i])).Select(i => curb[i]).OrderBy(x => x).ToList();
            if (!fixedProfile && refs.Count < 3) { log.AppendLine($"  {lane.name}: sin tramo recto medible, sin cambios"); continue; }
            float cRef = fixedProfile ? fixedCurbM : refs[refs.Count / 2];

            // Correccion por muestra (mediana movil, huecos interpolados, rampa a 0 en los extremos)
            var delta = new float[n];
            for (int i = 0; i < n; i++) delta[i] = float.IsNaN(curb[i]) ? float.NaN : curb[i] - cRef;
            delta = MedianFilter(delta, 2);
            FillGaps(delta);
            float maxAbs = 0f;
            for (int i = 0; i < n; i++)
            {
                float s = len * i / (n - 1);
                float ramp = fixedProfile ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.Min(s, len - s) / EndRampM);
                delta[i] = Mathf.Clamp(delta[i], -4f, 4f) * ramp;
                maxAbs = Mathf.Max(maxAbs, Mathf.Abs(delta[i]));
            }
            if (maxAbs < 0.1f) continue;

            var spline = new Spline();
            for (int i = 0; i < n; i++)
            {
                Vector3 p = (!fixedProfile && (i == 0 || i == n - 1)) ? pts[i] : pts[i] + right[i] * delta[i];
                spline.Add(new BezierKnot(c.transform.InverseTransformPoint(p)), TangentMode.AutoSmooth);
            }
            Undo.RecordObject(c, "Alinear carril");
            c.Spline = spline;
            EditorUtility.SetDirty(c);
            changed++;
            log.AppendLine(fixedProfile
                ? $"  {lane.name}: perfil del circuito, eje a {cRef:F2} m de la solera (correccion maxima {maxAbs:F2} m)"
                : $"  {lane.name}: solera derecha a {cRef:F2} m del eje en los rectos; correccion maxima en curvas {maxAbs:F2} m");
        }

        Undo.CollapseUndoOperations(group);
        if (changed > 0 && lanes.Count > 0) EditorSceneManager.MarkSceneDirty(lanes[0].gameObject.scene);
        log.Insert(0, $"{changed} carriles corregidos. ");
        // La medicion de la solera es escalonada (5 cm cada 2 m): suavizar para que los autos no serpenteen
        log.Append(LaneSmoother.SmoothAll());
        return log.ToString();
    }

    private static float CurbDistance(Vector3 p, Vector3 right)
    {
        if (!OnRoad(p)) return float.NaN;
        for (float x = 0f; x < 12f; x += 0.05f)
            if (!OnRoad(p + right * x)) return x;
        return float.NaN;
    }

    private static bool OnRoad(Vector3 p) =>
        Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 10f) &&
        hit.collider.gameObject.layer == LayerMask.NameToLayer("Ground") && !(hit.collider is TerrainCollider);

    private static float[] MedianFilter(float[] v, int half)
    {
        var r = new float[v.Length];
        var win = new List<float>();
        for (int i = 0; i < v.Length; i++)
        {
            win.Clear();
            for (int k = i - half; k <= i + half; k++)
                if (k >= 0 && k < v.Length && !float.IsNaN(v[k])) win.Add(v[k]);
            if (win.Count == 0) { r[i] = float.NaN; continue; }
            win.Sort();
            r[i] = win[win.Count / 2];
        }
        return r;
    }

    private static void FillGaps(float[] v)
    {
        for (int i = 0; i < v.Length; i++)
        {
            if (!float.IsNaN(v[i])) continue;
            int a = i - 1, b = i + 1;
            while (a >= 0 && float.IsNaN(v[a])) a--;
            while (b < v.Length && float.IsNaN(v[b])) b++;
            float va = a >= 0 ? v[a] : 0f, vb = b < v.Length ? v[b] : 0f;
            v[i] = (a >= 0 && b < v.Length) ? Mathf.Lerp(va, vb, (i - a) / (float)(b - a)) : (a >= 0 ? va : vb);
        }
    }
}
