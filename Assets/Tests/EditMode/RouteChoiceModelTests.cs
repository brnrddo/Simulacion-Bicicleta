// ============================================================
// RouteChoiceModelTests.cs
// Eleccion de ruta (E3): curva BPR, logit, capacidad HCM de un giro que
// cede, demora de un semaforo y seleccion de rutas por par.
// ============================================================

using NUnit.Framework;
using UnityEngine;

public class RouteChoiceModelTests
{
    [Test]
    public void Bpr_ZeroFlowIsFreeTime_AtCapacityIs15PercentMore_AndMonotonic()
    {
        Assert.That(RouteChoiceModel.Bpr(40f, 0f, 1800f, 0.15f, 4f), Is.EqualTo(40f));
        Assert.That(RouteChoiceModel.Bpr(40f, 1800f, 1800f, 0.15f, 4f), Is.EqualTo(46f).Within(1e-4f));
        float prev = 40f;
        for (float v = 100f; v <= 3000f; v += 100f)
        {
            float t = RouteChoiceModel.Bpr(40f, v, 1800f, 0.15f, 4f);
            Assert.That(t, Is.GreaterThan(prev));
            prev = t;
        }
        Assert.That(RouteChoiceModel.Bpr(40f, 500f, 0f, 0.15f, 4f), Is.EqualTo(40f), "sin capacidad definida: sin congestion");
    }

    [Test]
    public void Logit_SumsToOne_EqualTimesSplitEvenly_LongerTimeLessLikely()
    {
        var p = RouteChoiceModel.Logit(new[] { 44.1f, 47.0f }, -0.1f);
        Assert.That(p[0] + p[1], Is.EqualTo(1f).Within(1e-5f));
        // Formula del dibujo: P1 = exp(b t1) / (exp(b t1) + exp(b t2))
        float expected = Mathf.Exp(-0.1f * 44.1f) / (Mathf.Exp(-0.1f * 44.1f) + Mathf.Exp(-0.1f * 47.0f));
        Assert.That(p[0], Is.EqualTo(expected).Within(1e-5f));
        Assert.That(p[0], Is.EqualTo(0.572f).Within(0.001f));   // 1 / (1 + e^-0.29)

        var even = RouteChoiceModel.Logit(new[] { 30f, 30f, 30f }, -0.1f);
        foreach (float x in even) Assert.That(x, Is.EqualTo(1f / 3f).Within(1e-5f));
    }

    [Test]
    public void Logit_StableWithLargeTimes()
    {
        var p = RouteChoiceModel.Logit(new[] { 5000f, 5003f }, -0.1f);
        Assert.That(float.IsNaN(p[0]), Is.False);
        Assert.That(p[0], Is.EqualTo(0.574f).Within(0.001f));
    }

    [Test]
    public void YieldCapacity_MatchesHcm_AndFallsWithConflictingFlow()
    {
        // Sin trafico en conflicto: 3600 / t_f
        Assert.That(RouteChoiceModel.YieldCapacity(0f, 4.5f, 2.2f), Is.EqualTo(3600f / 2.2f).Within(0.1f));
        // q_c = 500 veh/h, t_c = 5 s, t_f = 2.2 s: 500 e^(-0.694) / (1 - e^(-0.306)) = ~948 veh/h
        Assert.That(RouteChoiceModel.YieldCapacity(500f, 5f, 2.2f), Is.EqualTo(948f).Within(2f));
        Assert.That(RouteChoiceModel.YieldCapacity(1000f, 5f, 2.2f), Is.LessThan(RouteChoiceModel.YieldCapacity(500f, 5f, 2.2f)));
    }

    [Test]
    public void SignalDelay_UniformDelayOfWebster()
    {
        // Ciclo 60 s, verde 25 s: 60 (35/60)^2 / 2 = 10.2 s
        Assert.That(RouteChoiceModel.SignalDelay(60f, 25f), Is.EqualTo(60f * (35f / 60f) * (35f / 60f) / 2f).Within(1e-4f));
        Assert.That(RouteChoiceModel.SignalDelay(60f, 60f), Is.EqualTo(0f).Within(1e-5f));
    }

    [Test]
    public void SelectRoutes_KeepsFastest_UpToMax_AndDropsMuchSlower()
    {
        // Tiempos libres (s): C2 -> C1 directo 22.5, vuelta larga 56.8 (2.5 veces): con factor 2.2 queda solo el directo
        Assert.That(RouteChoiceModel.SelectRoutes(new[] { 56.8f, 22.5f }, 2, 2.2f), Is.EqualTo(new[] { 1 }));
        // C2 -> C3: 44.1 y 47.0 (1.07 veces): quedan las dos, la mas rapida primero
        Assert.That(RouteChoiceModel.SelectRoutes(new[] { 47.0f, 44.1f }, 2, 2.2f), Is.EqualTo(new[] { 1, 0 }));
        // Maximo de rutas
        Assert.That(RouteChoiceModel.SelectRoutes(new[] { 30f, 31f, 32f }, 2, 2.2f).Count, Is.EqualTo(2));
    }
}
