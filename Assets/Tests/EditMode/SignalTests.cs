// ============================================================
// SignalTests.cs
// Ciclo del IntersectionController (secuencia, duraciones, todo-rojo,
// desfase, peatones) y regla de decision en amarillo.
// ============================================================

using NUnit.Framework;

public class SignalTests
{
    private static SignalTimingConfig Config(float offset = 0f) => new SignalTimingConfig
    {
        greenNorthSouthS = 25f,
        greenEastWestS = 20f,
        yellowS = 3f,
        allRedS = 2f,
        offsetS = offset,
        startingAxis = SignalAxis.NorthSouth,
        pedestrianClearanceS = 5f,
    };

    private static SignalPhase Ns(in SignalTimingConfig c, double t) => SignalCycle.Evaluate(c, t, SignalAxis.NorthSouth).Phase;
    private static SignalPhase Ew(in SignalTimingConfig c, double t) => SignalCycle.Evaluate(c, t, SignalAxis.EastWest).Phase;

    [Test]
    public void Cycle_LengthIsSumOfAllIntervals()
    {
        Assert.That(Config().CycleS, Is.EqualTo(25f + 20f + 2f * (3f + 2f)));
    }

    [Test]
    public void Cycle_SequenceAndDurations()
    {
        var c = Config();
        // NS verde [0,25), amarillo [25,28), todo-rojo [28,30), EW verde [30,50), amarillo [50,53), todo-rojo [53,55)
        (double t, SignalPhase ns, SignalPhase ew)[] expected =
        {
            (0.0, SignalPhase.Green, SignalPhase.Red),
            (24.9, SignalPhase.Green, SignalPhase.Red),
            (25.1, SignalPhase.Yellow, SignalPhase.Red),
            (27.9, SignalPhase.Yellow, SignalPhase.Red),
            (28.5, SignalPhase.Red, SignalPhase.Red),     // todo-rojo
            (29.9, SignalPhase.Red, SignalPhase.Red),
            (30.1, SignalPhase.Red, SignalPhase.Green),
            (49.9, SignalPhase.Red, SignalPhase.Green),
            (50.1, SignalPhase.Red, SignalPhase.Yellow),
            (53.5, SignalPhase.Red, SignalPhase.Red),     // todo-rojo
            (55.1, SignalPhase.Green, SignalPhase.Red),   // ciclo siguiente
        };
        foreach (var e in expected)
        {
            Assert.That(Ns(c, e.t), Is.EqualTo(e.ns), $"NS en t={e.t}");
            Assert.That(Ew(c, e.t), Is.EqualTo(e.ew), $"EW en t={e.t}");
        }
    }

    [Test]
    public void Cycle_NeverBothAxesNonRed()
    {
        var c = Config();
        for (double t = 0; t < 3 * c.CycleS; t += 0.0111)
            Assert.That(Ns(c, t) == SignalPhase.Red || Ew(c, t) == SignalPhase.Red, $"conflicto en t={t:F3}");
    }

    [Test]
    public void Cycle_TimeInPhaseAndTimeToChange()
    {
        var c = Config();
        SignalState s = SignalCycle.Evaluate(c, 10.0, SignalAxis.NorthSouth);
        Assert.That(s.TimeInPhaseS, Is.EqualTo(10f).Within(1e-4f));
        Assert.That(s.TimeToChangeS, Is.EqualTo(15f).Within(1e-4f));

        // Rojo de NS: empieza al terminar su amarillo (t=28) y dura hasta el fin del ciclo (t=55)
        s = SignalCycle.Evaluate(c, 40.0, SignalAxis.NorthSouth);
        Assert.That(s.Phase, Is.EqualTo(SignalPhase.Red));
        Assert.That(s.TimeInPhaseS, Is.EqualTo(12f).Within(1e-4f));
        Assert.That(s.TimeToChangeS, Is.EqualTo(15f).Within(1e-4f));
    }

    [Test]
    public void Cycle_OffsetShiftsTheCycle()
    {
        var a = Config(0f);
        var b = Config(10f);
        for (double t = 0; t < 2 * a.CycleS; t += 0.5)
            Assert.That(Ns(b, t), Is.EqualTo(Ns(a, t + 10.0)), $"t={t}");
    }

    [Test]
    public void Cycle_StartingAxisEastWest()
    {
        var c = Config();
        c.startingAxis = SignalAxis.EastWest;
        Assert.That(Ew(c, 1.0), Is.EqualTo(SignalPhase.Green));
        Assert.That(Ns(c, 1.0), Is.EqualTo(SignalPhase.Red));
        Assert.That(Ns(c, 20f + 3f + 2f + 1f), Is.EqualTo(SignalPhase.Green));
    }

    [Test]
    public void Pedestrian_WalkDuringParallelGreenMinusClearance()
    {
        var c = Config();
        // Cruzar el brazo Norte = caminar Este-Oeste = paralelo al verde EW [30,50), parpadeo en los ultimos 5 s
        Assert.That(SignalCycle.EvaluatePedestrian(c, 31.0, Approach.North).Phase, Is.EqualTo(PedestrianPhase.Walk));
        Assert.That(SignalCycle.EvaluatePedestrian(c, 46.0, Approach.North).Phase, Is.EqualTo(PedestrianPhase.FlashingDontWalk));
        Assert.That(SignalCycle.EvaluatePedestrian(c, 51.0, Approach.North).Phase, Is.EqualTo(PedestrianPhase.DontWalk));
        Assert.That(SignalCycle.EvaluatePedestrian(c, 10.0, Approach.North).Phase, Is.EqualTo(PedestrianPhase.DontWalk));
        // Cruzar el brazo Este = paralelo al verde NS
        Assert.That(SignalCycle.EvaluatePedestrian(c, 10.0, Approach.East).Phase, Is.EqualTo(PedestrianPhase.Walk));
    }

    [Test]
    public void Yellow_StopsWhenBrakingDistanceFits()
    {
        // 14 m/s con b_safe = 4: distancia de frenado = 196 / 8 = 24.5 m
        Assert.That(SignalRules.ShouldStopOnYellow(14f, 30f, 4f), Is.True);
        Assert.That(SignalRules.ShouldStopOnYellow(14f, 24.5f, 4f), Is.True);   // borde: justo alcanza
        Assert.That(SignalRules.ShouldStopOnYellow(14f, 24.4f, 4f), Is.False);  // no alcanza: sigue
        Assert.That(SignalRules.ShouldStopOnYellow(14f, 5f, 4f), Is.False);
    }

    [Test]
    public void Yellow_EdgeCases()
    {
        Assert.That(SignalRules.ShouldStopOnYellow(0f, 0f, 4f), Is.True);      // detenido en la linea: se queda
        Assert.That(SignalRules.ShouldStopOnYellow(0f, 50f, 4f), Is.True);
        Assert.That(SignalRules.ShouldStopOnYellow(10f, -0.5f, 4f), Is.False); // ya paso la linea
    }
}
