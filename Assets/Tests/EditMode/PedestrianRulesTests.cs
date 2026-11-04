// ============================================================
// PedestrianRulesTests.cs
// T6: velocidad de marcha (Knoblauch et al., 1996), inicio del cruce con
// semaforo, paso cebra frente a autos, disparo del cruce fuera del paso y
// desplazamiento lateral de la pasada cercana.
// ============================================================

using System.Collections.Generic;
using NUnit.Framework;

public class PedestrianRulesTests
{
    [Test]
    public void WalkingSpeed_MeanSdAndRange()
    {
        var rng = TrafficManager.CreateRng(12345, 3000);
        var v = new List<float>();
        for (int i = 0; i < 20000; i++) v.Add(PedestrianRules.SampleWalkingSpeed(rng));
        double mean = 0; foreach (var x in v) mean += x; mean /= v.Count;
        double var = 0; foreach (var x in v) var += (x - mean) * (x - mean); var /= v.Count;
        // El recorte a [0.9, 1.8] apenas mueve la media (simetrico) y reduce un poco la DE
        Assert.That(mean, Is.EqualTo(1.35).Within(0.01));
        Assert.That(System.Math.Sqrt(var), Is.EqualTo(0.20).Within(0.015));
        foreach (var x in v) Assert.That(x, Is.InRange(0.9f, 1.8f));
    }

    [Test]
    public void WalkingSpeed_SameSeed_SameSequence()
    {
        var a = TrafficManager.CreateRng(7, 3000);
        var b = TrafficManager.CreateRng(7, 3000);
        for (int i = 0; i < 100; i++)
            Assert.That(PedestrianRules.SampleWalkingSpeed(a), Is.EqualTo(PedestrianRules.SampleWalkingSpeed(b)));
    }

    [Test]
    public void Signalized_StartsOnlyOnWalkAndIfItCanFinish()
    {
        // Cruce de 12 m a 1.2 m/s = 10 s
        Assert.That(PedestrianRules.CanStartSignalized(PedestrianPhase.Walk, 8f, 5f, 12f, 1.2f), Is.True);   // 8 + 5 >= 10
        Assert.That(PedestrianRules.CanStartSignalized(PedestrianPhase.Walk, 4f, 5f, 12f, 1.2f), Is.False);  // 4 + 5 < 10
        Assert.That(PedestrianRules.CanStartSignalized(PedestrianPhase.FlashingDontWalk, 20f, 5f, 12f, 1.2f), Is.False);
        Assert.That(PedestrianRules.CanStartSignalized(PedestrianPhase.DontWalk, 20f, 5f, 12f, 1.2f), Is.False);
    }

    [Test]
    public void Zebra_PedestrianWaitsOnlyForCarsThatCannotStop()
    {
        // Auto a 14 m/s, b = 2.5, s0 = 2: se detiene comodamente en 196/5 + 2 = 41.2 m
        Assert.That(PedestrianRules.ComfortStopDistance(14f, 2.5f, 2f), Is.EqualTo(41.2f).Within(1e-3f));
        Assert.That(PedestrianRules.ZebraSafeFromCar(50f, 14f, 2.5f, 2f, 4f), Is.True);   // puede detenerse
        Assert.That(PedestrianRules.ZebraSafeFromCar(30f, 14f, 2.5f, 2f, 4f), Is.False);  // no alcanza y llega en 2.1 s
        Assert.That(PedestrianRules.ZebraSafeFromCar(30f, 0f, 2.5f, 2f, 4f), Is.True);    // detenido cediendo
        Assert.That(PedestrianRules.ZebraSafeFromCar(-1f, 14f, 2.5f, 2f, 4f), Is.True);   // ya paso
    }

    [Test]
    public void Jaywalk_StartsWhenTimeToArrivalReachesTarget()
    {
        Assert.That(PedestrianRules.JaywalkShouldStart(20f, 5f, 3.5f), Is.False);  // 4 s
        Assert.That(PedestrianRules.JaywalkShouldStart(17f, 5f, 3.5f), Is.True);   // 3.4 s
        Assert.That(PedestrianRules.JaywalkShouldStart(5f, 0.2f, 3.5f), Is.False); // ciclista detenido: no se dispara
    }

    [Test]
    public void NearPass_LateralIncludesHandlebarAndBody()
    {
        Assert.That(PedestrianRules.NearPassLateral(0.274f, 0.25f, 1.0f), Is.EqualTo(1.524f).Within(1e-5f));
    }
}
