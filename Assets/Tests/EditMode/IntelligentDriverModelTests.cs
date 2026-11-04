// ============================================================
// IntelligentDriverModelTests.cs
// Tests del IDM y de la integracion balistica.
// ============================================================

using NUnit.Framework;

public class IntelligentDriverModelTests
{
    private static IdmParams UrbanDriver(float desiredSpeedMps) => new IdmParams
    {
        desiredSpeedMps = desiredSpeedMps,
        timeHeadwayS = 1.3f,
        minGapM = 2.0f,
        maxAccelMps2 = 1.5f,
        comfortDecelMps2 = 2.0f,
        exponent = 4f,
    };

    [Test]
    public void Equilibrium_GapS0PlusVT_SameSpeedAsLeader_ZeroAcceleration()
    {
        // Con v0 muy alto el termino libre es ~1, asi que en s = s0 + vT y dv = 0 la aceleracion es ~0
        var p = UrbanDriver(1000f);
        const float v = 12f;
        float gap = p.minGapM + v * p.timeHeadwayS;

        float acc = IntelligentDriverModel.Acceleration(p, v, gap, 0f);

        Assert.That(acc, Is.EqualTo(0f).Within(1e-3f));
    }

    [Test]
    public void Equilibrium_IsStable_LargerGapAccelerates_SmallerGapBrakes()
    {
        var p = UrbanDriver(1000f);
        const float v = 12f;
        float gap = p.minGapM + v * p.timeHeadwayS;

        Assert.That(IntelligentDriverModel.Acceleration(p, v, gap * 1.2f, 0f), Is.GreaterThan(0f));
        Assert.That(IntelligentDriverModel.Acceleration(p, v, gap * 0.8f, 0f), Is.LessThan(0f));
    }

    [Test]
    public void FreeRoad_AtDesiredSpeed_ZeroAcceleration()
    {
        var p = UrbanDriver(13.9f);
        float acc = IntelligentDriverModel.Acceleration(p, 13.9f, float.PositiveInfinity, 0f);
        Assert.That(acc, Is.EqualTo(0f).Within(1e-4f));
    }

    [Test]
    public void Emergency_IsBoundedByMaxBrake()
    {
        // A 15 m/s contra un obstaculo detenido a 0.5 m el IDM pide muchisimo mas que el tope fisico
        var p = UrbanDriver(13.9f);
        float acc = IntelligentDriverModel.Acceleration(p, 15f, 0.5f, 15f);
        Assert.That(acc, Is.EqualTo(-IntelligentDriverModel.MaxBrakeMps2).Within(1e-4f));
    }

    [Test]
    public void Ballistic_StopsWithinStep_NoNegativeSpeed()
    {
        // 1 m/s con -9 m/s2 y dt = 1 s: se detiene en 1/9 s y avanza v^2 / (2|a|)
        float speed = 1f;
        float advance = IntelligentDriverModel.IntegrateBallistic(ref speed, -9f, 1f);

        Assert.That(speed, Is.EqualTo(0f));
        Assert.That(advance, Is.EqualTo(1f / 18f).Within(1e-5f));
    }

    [Test]
    public void Ballistic_NeverNegativeSpeedOrAdvance()
    {
        var rng = new System.Random(42);
        for (int i = 0; i < 10000; i++)
        {
            float speed = (float)(rng.NextDouble() * 20.0);
            float acc = (float)(rng.NextDouble() * 20.0 - 10.0);
            float dt = (float)(rng.NextDouble() * 0.5);

            float advance = IntelligentDriverModel.IntegrateBallistic(ref speed, acc, dt);

            Assert.That(speed, Is.GreaterThanOrEqualTo(0f));
            Assert.That(advance, Is.GreaterThanOrEqualTo(0f));
        }
    }

    [Test]
    public void Ballistic_StoppedCarWithBraking_StaysStopped()
    {
        float speed = 0f;
        float advance = IntelligentDriverModel.IntegrateBallistic(ref speed, -2f, 0.0111f);
        Assert.That(speed, Is.EqualTo(0f));
        Assert.That(advance, Is.EqualTo(0f));
    }
}
