// ============================================================
// SteeringResponseTests.cs
// Respuesta de direccion por curvatura (BicycleController.SteeringMode.CurvatureMapped).
// ============================================================

using NUnit.Framework;
using UnityEngine;

public class SteeringResponseTests
{
    private const float MaxSteer = 45f, Expo = 0.3f, Wheelbase = 1f, MaxLean = 40f, Comfort = 90f, G = 9.81f;
    private const float Dt = 1f / 90f;

    private static float MappedDeg(float steerDeg, float speed) =>
        SteeringResponse.CurvatureMappedYawRateRad(steerDeg, speed, MaxSteer, Expo, Wheelbase, MaxLean, Comfort, G) * Mathf.Rad2Deg;

    private static float KinematicDeg(float steerDeg, float speed) =>
        SteeringResponse.KinematicYawRateRad(steerDeg, speed, MaxSteer, Wheelbase) * Mathf.Rad2Deg;

    [Test]
    public void StraightHandlebar_NoYaw()
    {
        Assert.That(MappedDeg(0f, 5f), Is.EqualTo(0f));
    }

    [Test]
    public void Stopped_NoYaw()
    {
        Assert.That(MappedDeg(30f, 0f), Is.EqualTo(0f));
    }

    [Test]
    public void IsOddFunction()
    {
        Assert.That(MappedDeg(-12f, 5f), Is.EqualTo(-MappedDeg(12f, 5f)).Within(1e-4f));
    }

    [Test]
    public void IsMonotonicInSteering()
    {
        foreach (float v in new[] { 1f, 3f, 5f, 10f })
        {
            float previous = 0f;
            for (float d = 0.5f; d <= 45f; d += 0.5f)
            {
                float w = MappedDeg(d, v);
                Assert.That(w, Is.GreaterThanOrEqualTo(previous - 1e-4f), $"v={v} delta={d}");
                previous = w;
            }
        }
    }

    [Test]
    public void NeverMoreSensitiveThanKinematic()
    {
        foreach (float v in new[] { 0.2f, 0.5f, 1f, 2f, 4f, 6f, 10f })
            for (float d = 0.5f; d <= 45f; d += 0.5f)
                Assert.That(MappedDeg(d, v), Is.LessThanOrEqualTo(KinematicDeg(d, v) + 1e-3f), $"v={v} delta={d}");
    }

    [Test]
    public void NeverExceedsMaxYawRate()
    {
        foreach (float v in new[] { 1f, 5f, 10f })
        {
            float max = SteeringResponse.MaxYawRateRad(v, MaxLean, Comfort, G) * Mathf.Rad2Deg;
            Assert.That(MappedDeg(45f, v), Is.LessThanOrEqualTo(max + 1e-3f));
        }
    }

    [Test]
    public void AtWalkingSpeed_MatchesKinematic()
    {
        // A 0.5 m/s la cinematica da menos giro que la curva: manda la cinematica en todo el recorrido
        for (float d = 5f; d <= 45f; d += 5f)
            Assert.That(MappedDeg(d, 0.5f), Is.EqualTo(KinematicDeg(d, 0.5f)).Within(1e-3f));
    }

    [Test]
    public void FullLockAtSpeed_ReachesMaxYawRate()
    {
        Assert.That(MappedDeg(45f, 5f), Is.EqualTo(90f).Within(0.01f));     // techo de comfort
        Assert.That(MappedDeg(45f, 10f), Is.EqualTo(47.2f).Within(0.1f));   // limite de inclinacion a 10 m/s
    }

    [Test]
    public void GainAtCenter_IsFarBelowKinematic()
    {
        // 1 grado de manubrio: cinematico 5 y 10 grados/s (a 5 y 10 m/s); por curvatura, ~1.4 y ~0.7
        Assert.That(KinematicDeg(1f, 5f), Is.EqualTo(5f).Within(0.01f));
        Assert.That(MappedDeg(1f, 5f), Is.EqualTo(1.4f).Within(0.05f));
        Assert.That(MappedDeg(1f, 10f), Is.LessThan(1f));
    }

    [Test]
    public void HandlebarNeededForTypicalManeuvers()
    {
        // Curva del anillo (R = 50 m a 5 m/s = 5.7 grados/s) con ~4 grados; esquina (R = 6 m a 4 m/s = 38 grados/s) con ~24
        Assert.That(MappedDeg(4.1f, 5f), Is.EqualTo(5.73f).Within(0.3f));
        Assert.That(MappedDeg(24.3f, 4f), Is.EqualTo(38.2f).Within(1f));
    }

    [Test]
    public void YawAccelLimit_BoundsChangePerTick()
    {
        float target = 60f * Mathf.Deg2Rad;
        float next = SteeringResponse.LimitYawRateRad(0f, target, 300f, Dt, 5f, MaxSteer, Wheelbase);
        Assert.That(next * Mathf.Rad2Deg, Is.EqualTo(300f * Dt).Within(1e-3f));
    }

    [Test]
    public void YawAccelLimit_Zero_MeansNoLimit()
    {
        float target = 60f * Mathf.Deg2Rad;
        Assert.That(SteeringResponse.LimitYawRateRad(0f, target, 0f, Dt, 5f, MaxSteer, Wheelbase), Is.EqualTo(target).Within(1e-5f));
    }

    [Test]
    public void BikeStops_YawStopsAtOnce()
    {
        // Venia girando a 60 grados/s y la velocidad cae a 0: el limite de aceleracion no puede dejarla girando detenida
        float previous = 60f * Mathf.Deg2Rad;
        Assert.That(SteeringResponse.LimitYawRateRad(previous, 0f, 300f, Dt, 0f, MaxSteer, Wheelbase), Is.EqualTo(0f));
    }
}
