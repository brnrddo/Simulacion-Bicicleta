// ============================================================
// VehicleMixTests.cs
// Vehiculos de 2026-11-04: geometria de vehiculos largos (corte del eje
// trasero en curvas, radio por tres puntos), sorteo de colores con semilla y
// eleccion de ruta filtrada (determinista y respetando el filtro).
// ============================================================

using NUnit.Framework;
using UnityEngine;
using UnityEngine.Splines;

public class VehicleMixTests
{
    [Test]
    public void OffTracking_MatchesGeometry()
    {
        // R = 10 m, L_e = 6 m: 10 - sqrt(100 - 36) = 2 m
        Assert.That(TrafficRules.OffTrackingM(10f, 6f), Is.EqualTo(2f).Within(1e-4f));
        // Recta: sin corte
        Assert.That(TrafficRules.OffTrackingM(float.PositiveInfinity, 6f), Is.EqualTo(0f));
        // Radio menor que la distancia entre ejes: no puede seguir la curva
        Assert.That(TrafficRules.OffTrackingM(4f, 6f), Is.EqualTo(6f));
        // Auto (L_e 2.5 m) en un giro de R = 10 m: unos 0.32 m
        Assert.That(TrafficRules.OffTrackingM(10f, 2.5f), Is.EqualTo(10f - Mathf.Sqrt(100f - 6.25f)).Within(1e-4f));
    }

    [Test]
    public void CircumRadius_OfPointsOnACircle()
    {
        float r = 12f;
        Vector3 P(float deg) => new Vector3(r * Mathf.Cos(deg * Mathf.Deg2Rad), 0f, r * Mathf.Sin(deg * Mathf.Deg2Rad));
        Assert.That(TrafficRules.CircumRadius(P(0f), P(10f), P(20f)), Is.EqualTo(r).Within(1e-3f));
        Assert.That(float.IsPositiveInfinity(TrafficRules.CircumRadius(Vector3.zero, Vector3.forward, 2f * Vector3.forward)));
    }

    [Test]
    public void BodyColor_SameSeedSameColors()
    {
        var a = TrafficManager.CreateRng(7, 4000);
        var b = TrafficManager.CreateRng(7, 4000);
        for (int i = 0; i < 50; i++)
            Assert.That(TrafficManager.SampleBodyColor(a), Is.EqualTo(TrafficManager.SampleBodyColor(b)));
    }

    private static TrafficLane MakeLane(string name)
    {
        var go = new GameObject(name);
        var c = go.AddComponent<SplineContainer>();
        c.Spline = new Spline(new[] { new BezierKnot(Vector3.zero), new BezierKnot(new Vector3(0f, 0f, 10f)) });
        return go.AddComponent<TrafficLane>();
    }

    private static void SetNext(TrafficLane lane, params TrafficLane[] next)
    {
#if UNITY_EDITOR
        var so = new UnityEditor.SerializedObject(lane);
        var list = so.FindProperty("nextLanes");
        list.arraySize = next.Length;
        for (int i = 0; i < next.Length; i++)
        {
            list.GetArrayElementAtIndex(i).FindPropertyRelative("lane").objectReferenceValue = next[i];
            list.GetArrayElementAtIndex(i).FindPropertyRelative("weight").floatValue = 1f;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
#endif
    }

    [Test]
    public void PickNext_Filtered_RespectsFilterAndUsesOneDraw()
    {
        var entry = MakeLane("Entry"); var straight = MakeLane("Straight"); var right = MakeLane("Right"); var left = MakeLane("Left");
        try
        {
            SetNext(entry, straight, right, left);
            var rng = new System.Random(3);
            for (int i = 0; i < 200; i++)
                Assert.That(entry.PickNext(rng, l => l != right), Is.Not.EqualTo(right));

            // Mismo numero de sorteos que PickNext: dos secuencias con la misma semilla quedan alineadas
            var r1 = new System.Random(11); var r2 = new System.Random(11);
            entry.PickNext(r1, l => l == straight);
            entry.PickNext(r2);
            Assert.That(r1.NextDouble(), Is.EqualTo(r2.NextDouble()));

            // Ningun destino permitido: elige entre todos (no se queda sin ruta)
            Assert.That(entry.PickNext(new System.Random(1), l => false), Is.Not.Null);
        }
        finally
        {
            foreach (var l in new[] { entry, straight, right, left }) Object.DestroyImmediate(l.gameObject);
        }
    }
}
