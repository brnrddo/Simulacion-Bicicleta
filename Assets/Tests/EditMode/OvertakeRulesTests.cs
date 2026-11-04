// ============================================================
// OvertakeRulesTests.cs
// T4: distribucion lognormal de la distancia de adelantamiento (Beck et al. 2019),
// perfil lateral quintico, posicion lateral requerida y tiempo a colision.
// ============================================================

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class OvertakeRulesTests
{
    [Test]
    public void Lognormal_MedianAndIqr_WithinFivePercent()
    {
        // Plan, seccion 11: mediana y rango intercuartil empiricos dentro de +-5 % con N = 10 000
        const float median = 1.73f, q1 = 1.37f, q3 = 2.24f;
        float sigma = OvertakeRules.SigmaFromIqr(q1, q3);
        var rng = TrafficManager.CreateRng(12345, 2000);
        var x = new List<float>(10000);
        for (int i = 0; i < 10000; i++) x.Add(OvertakeRules.SamplePassingDistance(rng, median, sigma, 0.5f, 3.0f));
        x.Sort();

        float m = x[x.Count / 2], e1 = x[x.Count / 4], e3 = x[3 * x.Count / 4];
        Assert.That(m, Is.EqualTo(median).Within(median * 0.05f), "mediana");
        Assert.That(e1, Is.EqualTo(q1).Within(q1 * 0.05f), "Q1");
        Assert.That(e3, Is.EqualTo(q3).Within(q3 * 0.05f), "Q3");
        Assert.That(x[0], Is.GreaterThanOrEqualTo(0.5f));
        Assert.That(x[x.Count - 1], Is.LessThanOrEqualTo(3.0f));
    }

    [Test]
    public void Lognormal_SameSeed_SameSequence()
    {
        float sigma = OvertakeRules.SigmaFromIqr(1.37f, 2.24f);
        var a = TrafficManager.CreateRng(1, 2000);
        var b = TrafficManager.CreateRng(1, 2000);
        for (int i = 0; i < 100; i++)
            Assert.That(OvertakeRules.SamplePassingDistance(a, 1.73f, sigma, 0.5f, 3f),
                        Is.EqualTo(OvertakeRules.SamplePassingDistance(b, 1.73f, sigma, 0.5f, 3f)));
    }

    [Test]
    public void Quintic_EndpointsAndSmoothness()
    {
        Assert.That(OvertakeRules.Quintic(0f), Is.EqualTo(0f));
        Assert.That(OvertakeRules.Quintic(1f), Is.EqualTo(1f).Within(1e-6f));
        Assert.That(OvertakeRules.Quintic(0.5f), Is.EqualTo(0.5f).Within(1e-6f));
        // Aceleracion nula al inicio y al final (arranque y llegada suaves)
        Assert.That(OvertakeRules.QuinticAccel(0f), Is.EqualTo(0f).Within(1e-6f));
        Assert.That(OvertakeRules.QuinticAccel(1f), Is.EqualTo(0f).Within(1e-4f));
    }

    [Test]
    public void Quintic_PeakLateralAccel_RespectsLimit()
    {
        // Con la duracion calculada, la aceleracion lateral maxima no supera a_max
        const float d = 2.0f, aMax = 1.0f;
        float T = OvertakeRules.ManeuverDuration(d, aMax, 0.1f, 20f);
        float peak = 0f;
        for (int i = 0; i <= 1000; i++) peak = Mathf.Max(peak, Mathf.Abs(OvertakeRules.QuinticAccel(i / 1000f)));
        float peakAccel = peak * d / (T * T);
        Assert.That(peakAccel, Is.LessThanOrEqualTo(aMax * 1.001f));
        Assert.That(peak, Is.EqualTo(OvertakeRules.QuinticPeakAccelFactor).Within(0.01f));
    }

    [Test]
    public void RequiredLateral_AndClearance_AreConsistent()
    {
        // Bici 2.4 m a la derecha del eje, manubrio 0.274, auto 0.925 de medio ancho, D = 1.73
        float L = OvertakeRules.RequiredCarLateral(2.4f, 0.274f, 0.925f, 1.73f);
        Assert.That(L, Is.EqualTo(2.4f - 0.274f - 1.73f - 0.925f).Within(1e-5f));
        Assert.That(OvertakeRules.Clearance(2.4f, L, 0.274f, 0.925f), Is.EqualTo(1.73f).Within(1e-5f));
        // Si la bici esta lejos, la geometria basta: L >= 0, sin maniobra
        Assert.That(OvertakeRules.RequiredCarLateral(4.0f, 0.274f, 0.925f, 1.73f), Is.GreaterThan(0f));
    }

    [Test]
    public void OncomingTtc_ClosingSpeeds()
    {
        Assert.That(OvertakeRules.OncomingTtc(100f, 10f, 10f), Is.EqualTo(5f).Within(1e-5f));
        Assert.That(float.IsPositiveInfinity(OvertakeRules.OncomingTtc(100f, 0f, 0f)));
        Assert.That(OvertakeRules.OncomingTtc(-1f, 10f, 10f), Is.EqualTo(0f));
    }

    [Test]
    public void Lateral_NoSidewaysMotionWhenStopped()
    {
        float slopeMax = UnityEngine.Mathf.Tan(15f * UnityEngine.Mathf.Deg2Rad);
        Assert.That(OvertakeRules.LateralProgressRate(0f, 3f, 2f, slopeMax), Is.EqualTo(0f));
        // Rapido: manda la duracion en el tiempo (1/T)
        Assert.That(OvertakeRules.LateralProgressRate(12f, 3f, 2f, slopeMax), Is.EqualTo(1f / 3f).Within(1e-5f));
        // Lento: manda el largo minimo L = 1.875 * 2 / tan(15) = 14.0 m
        float lMin = OvertakeRules.MinManeuverLength(2f, slopeMax);
        Assert.That(lMin, Is.EqualTo(1.875f * 2f / slopeMax).Within(1e-4f));
        Assert.That(OvertakeRules.LateralProgressRate(2f, 3f, 2f, slopeMax), Is.EqualTo(2f / lMin).Within(1e-5f));
    }

    [Test]
    public void Lateral_HeadingNeverExceedsMaximum()
    {
        float slopeMax = UnityEngine.Mathf.Tan(15f * UnityEngine.Mathf.Deg2Rad);
        foreach (float v in new[] { 0f, 0.5f, 2f, 5f, 10f, 15f })
            foreach (float d in new[] { -3.5f, -1f, 0.5f, 2f })
                for (float x = 0f; x <= 1f; x += 0.05f)
                {
                    float k = OvertakeRules.LateralSlope(v, 2.5f, d, slopeMax, x);
                    Assert.That(UnityEngine.Mathf.Abs(k), Is.LessThanOrEqualTo(slopeMax + 1e-4f), $"v={v} d={d} x={x}");
                }
        // Signo: a la derecha (+) pendiente positiva
        Assert.That(OvertakeRules.LateralSlope(5f, 3f, 2f, slopeMax, 0.5f), Is.GreaterThan(0f));
    }
}
