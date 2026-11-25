// ============================================================
// VehicleMixTests.cs
// Vehiculos de 2026-11-04: geometria de vehiculos largos (corte del eje
// trasero en curvas, radio por tres puntos), sorteo de colores con semilla y
// catalogo de rutas (RouteChoiceModel), con y sin restriccion para vehiculos largos.
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
    public void RouteCatalog_FindsBothRoutes_WithoutRepeatingAnIntersection_AndRespectsFilter()
    {
        // Anillo minimo: Entry entra a la interseccion A y puede girar a la derecha (rama este) o a la izquierda
        // (rama oeste); las dos ramas llegan a la interseccion B y salen por Exit. Desde la rama este tambien se puede
        // seguir derecho en B y volver a A (un ciclo que repite A y no debe contar).
        var entry = MakeLane("Entry"); var aRight = MakeLane("I_A_S_Conn_Right_E"); var aLeft = MakeLane("I_A_S_Conn_Left_W");
        var east = MakeLane("East"); var west = MakeLane("West");
        var bFromEast = MakeLane("I_B_E_Conn_Right_N"); var bFromWest = MakeLane("I_B_W_Conn_Left_N");
        var bLoop = MakeLane("I_B_E_Conn_Straight_W"); var back = MakeLane("Back"); var aLoop = MakeLane("I_A_E_Conn_Straight_W");
        var exit = MakeLane("Exit"); var island = MakeLane("Island");
        var all = new[] { entry, aRight, aLeft, east, west, bFromEast, bFromWest, bLoop, back, aLoop, exit, island };
        try
        {
            SetNext(entry, aRight, aLeft);
            SetNext(aRight, east); SetNext(aLeft, west);
            SetNext(east, bFromEast, bLoop); SetNext(west, bFromWest);
            SetNext(bFromEast, exit); SetNext(bFromWest, exit);
            SetNext(bLoop, back); SetNext(back, aLoop); SetNext(aLoop, west);
            System.Func<TrafficLane, string> inter = l => l.name.StartsWith("I_") ? l.name.Substring(0, 3) : null;

            var routes = RouteChoiceModel.EnumerateRoutes(entry, exit, inter);
            Assert.That(routes.Count, Is.EqualTo(2));
            Assert.That(routes[0], Is.EqualTo(new[] { entry, aRight, east, bFromEast, exit }));
            Assert.That(routes[1], Is.EqualTo(new[] { entry, aLeft, west, bFromWest, exit }));

            // El bus no cabe en el giro a la izquierda: queda una sola ruta
            var forBus = RouteChoiceModel.EnumerateRoutes(entry, exit, inter, l => l != aLeft);
            Assert.That(forBus.Count, Is.EqualTo(1));
            Assert.That(forBus[0][1], Is.EqualTo(aRight));

            Assert.That(RouteChoiceModel.EnumerateRoutes(entry, island, inter), Is.Empty);
        }
        finally
        {
            foreach (var l in all) Object.DestroyImmediate(l.gameObject);
        }
    }
}
