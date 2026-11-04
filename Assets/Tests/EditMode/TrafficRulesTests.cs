// ============================================================
// TrafficRulesTests.cs
// Reglas de T3: velocidad en curva, anticipacion, aceptacion de brecha,
// prioridad y deteccion geometrica de conflictos entre trayectorias.
// ============================================================

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class TrafficRulesTests
{
    [Test]
    public void CurveSpeed_IsSqrtOfLateralAccelTimesRadius()
    {
        Assert.That(TrafficRules.CurveSpeedLimitMps(8f, 2f), Is.EqualTo(4f).Within(1e-5f));
        Assert.That(TrafficRules.CurveSpeedLimitMps(50f, 2f), Is.EqualTo(10f).Within(1e-5f));
        Assert.That(float.IsPositiveInfinity(TrafficRules.CurveSpeedLimitMps(float.PositiveInfinity, 2f)));
    }

    [Test]
    public void Anticipation_ReachesNextLimitAtTheLaneStart()
    {
        // A 0 m del tramo lento la velocidad deseada es el limite del tramo
        Assert.That(TrafficRules.AnticipatedDesiredSpeed(13.9f, 4f, 0f, 2f), Is.EqualTo(4f).Within(1e-5f));
        // Lejos no limita: sqrt(16 + 2*2*100) = 20.4 > 13.9
        Assert.That(TrafficRules.AnticipatedDesiredSpeed(13.9f, 4f, 100f, 2f), Is.EqualTo(13.9f).Within(1e-5f));
        // Intermedio: sqrt(16 + 2*2*20) = sqrt(96)
        Assert.That(TrafficRules.AnticipatedDesiredSpeed(13.9f, 4f, 20f, 2f), Is.EqualTo(Mathf.Sqrt(96f)).Within(1e-4f));
    }

    [Test]
    public void GapAcceptance_AndTimeToReach()
    {
        Assert.That(TrafficRules.GapAccepted(5f, 4.5f), Is.True);
        Assert.That(TrafficRules.GapAccepted(4.5f, 4.5f), Is.True);
        Assert.That(TrafficRules.GapAccepted(4.4f, 4.5f), Is.False);

        Assert.That(TrafficRules.TimeToReach(30f, 10f), Is.EqualTo(3f).Within(1e-5f));
        Assert.That(float.IsPositiveInfinity(TrafficRules.TimeToReach(30f, 0f)), "detenido lejos: no llega");
        Assert.That(TrafficRules.TimeToReach(2f, 0f), Is.EqualTo(0f), "detenido encima del punto: ocupa");
    }

    [Test]
    public void Priority_MinorYieldsToMajor_LeftYieldsToStraightAndRight()
    {
        var P = ApproachControl.Priority;
        var S = ApproachControl.Stop;
        // PARE cede a la via principal, aunque vaya derecho y el otro doble
        Assert.That(TrafficRules.Yields(MovementKind.Straight, S, MovementKind.Left, P), Is.True);
        Assert.That(TrafficRules.Yields(MovementKind.Left, P, MovementKind.Straight, S), Is.False);
        // Entre iguales: izquierda cede a recto y a derecha; derecha cede a recto
        Assert.That(TrafficRules.Yields(MovementKind.Left, P, MovementKind.Straight, P), Is.True);
        Assert.That(TrafficRules.Yields(MovementKind.Left, P, MovementKind.Right, P), Is.True);
        Assert.That(TrafficRules.Yields(MovementKind.Right, P, MovementKind.Straight, P), Is.True);
        Assert.That(TrafficRules.Yields(MovementKind.Straight, P, MovementKind.Straight, P), Is.False);
    }

    private static List<Vector2> Line(Vector2 a, Vector2 b, float step = 0.25f)
    {
        int n = Mathf.Max(2, Mathf.CeilToInt(Vector2.Distance(a, b) / step) + 1);
        var pts = new List<Vector2>();
        for (int i = 0; i < n; i++) pts.Add(Vector2.Lerp(a, b, i / (float)(n - 1)));
        return pts;
    }

    [Test]
    public void Conflict_CrossingPaths_ZoneStartsBeforeTheCrossing()
    {
        // Cruzan en el origen (a 10 m del inicio de ew y a 6 m del de ns). Con zona de 1.5 m, la zona
        // empieza 1.5 m antes del cruce en cada una y termina 1.5 m despues.
        var ew = Line(new Vector2(-10, 0), new Vector2(10, 0));
        var ns = Line(new Vector2(0, -6), new Vector2(0, 6));
        Assert.That(TrafficRules.FindConflict(ew, ns, 1.5f, out float sA, out float sAEnd, out float sB, out float sBEnd), Is.True);
        Assert.That(sA, Is.EqualTo(8.5f).Within(0.3f));
        Assert.That(sAEnd, Is.EqualTo(11.5f).Within(0.3f));
        Assert.That(sB, Is.EqualTo(4.5f).Within(0.3f));
        Assert.That(sBEnd, Is.EqualTo(7.5f).Within(0.3f));
    }

    [Test]
    public void Conflict_MergingPaths_FoundNearTheMergePoint()
    {
        var a = Line(new Vector2(-10, 0), new Vector2(0, 10));
        var b = Line(new Vector2(10, 0), new Vector2(0, 10));
        Assert.That(TrafficRules.FindConflict(a, b, 1.5f, out float sA, out float sB), Is.True);
        float len = Vector2.Distance(new Vector2(-10, 0), new Vector2(0, 10));
        Assert.That(sA, Is.GreaterThan(len - 3f));
        Assert.That(sB, Is.GreaterThan(len - 3f));
    }

    [Test]
    public void Conflict_ParallelOpposingLanes_NoConflict()
    {
        // Carriles opuestos separados 4 m: no se cruzan
        var east = Line(new Vector2(-10, -2), new Vector2(10, -2));
        var west = Line(new Vector2(10, 2), new Vector2(-10, 2));
        Assert.That(TrafficRules.FindConflict(east, west, 1.8f, out _, out _), Is.False);
    }
}
