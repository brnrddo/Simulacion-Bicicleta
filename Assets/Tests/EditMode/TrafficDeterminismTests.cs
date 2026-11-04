// ============================================================
// TrafficDeterminismTests.cs
// Misma semilla = misma secuencia de llegadas y de conductores.
// ============================================================

using NUnit.Framework;
using UnityEngine;

public class TrafficDeterminismTests
{
    private const int Seed = 12345;
    private const float Flow = 400f;
    private const float MinHeadway = 1.2f;

    private static float[] ArrivalSequence(int seed, int offset, int count)
    {
        var rng = TrafficManager.CreateRng(seed, offset);
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = TrafficSpawner.SampleHeadway(rng, Flow, MinHeadway);
        return result;
    }

    [Test]
    public void Spawner_SameSeed_SameArrivalTimes()
    {
        Assert.That(ArrivalSequence(Seed, 1, 500), Is.EqualTo(ArrivalSequence(Seed, 1, 500)));
    }

    [Test]
    public void Spawner_DifferentOffset_DifferentArrivalTimes()
    {
        Assert.That(ArrivalSequence(Seed, 1, 50), Is.Not.EqualTo(ArrivalSequence(Seed, 2, 50)));
    }

    [Test]
    public void Spawner_HeadwaysRespectMinimumAndMeanFlow()
    {
        const int n = 20000;
        float[] h = ArrivalSequence(Seed, 1, n);

        double sum = 0;
        foreach (float x in h)
        {
            Assert.That(x, Is.GreaterThanOrEqualTo(MinHeadway));
            sum += x;
        }

        // Media teorica = 3600 / flujo (la exponencial desplazada conserva la media)
        double expectedMean = 3600.0 / Flow;
        Assert.That(sum / n, Is.EqualTo(expectedMean).Within(expectedMean * 0.03));
    }

    [Test]
    public void DriverProfile_SameSeed_SameDrivers()
    {
        var profile = ScriptableObject.CreateInstance<DriverProfile>();
        try
        {
            var a = TrafficManager.CreateRng(Seed, 1);
            var b = TrafficManager.CreateRng(Seed, 1);
            for (int i = 0; i < 200; i++)
            {
                IdmParams pa = profile.Sample(a, out float fa);
                IdmParams pb = profile.Sample(b, out float fb);
                Assert.That(fa, Is.EqualTo(fb));
                Assert.That(pa.timeHeadwayS, Is.EqualTo(pb.timeHeadwayS));
                Assert.That(pa.minGapM, Is.EqualTo(pb.minGapM));
                Assert.That(pa.maxAccelMps2, Is.EqualTo(pb.maxAccelMps2));
                Assert.That(pa.comfortDecelMps2, Is.EqualTo(pb.comfortDecelMps2));
            }
        }
        finally
        {
            Object.DestroyImmediate(profile);
        }
    }
}
