// ============================================================
// OdMatrixTests.cs
// Matriz origen-destino (E2): lectura, validacion, sorteo normal por celda
// y reparto de destinos.
// ============================================================

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

public class OdMatrixTests
{
    private const string Json =
        "{ \"pairs\": [" +
        "  { \"from\": \"C2\", \"to\": \"C3\", \"meanVehPerHour\": 500, \"sdVehPerHour\": 50 }," +
        "  { \"from\": \"C2\", \"to\": \"C1\", \"meanVehPerHour\": 10,  \"sdVehPerHour\": 1 }," +
        "  { \"from\": \"C4\", \"to\": \"C1\", \"meanVehPerHour\": 500, \"sdVehPerHour\": 50 }," +
        "  { \"from\": \"C4\", \"to\": \"C3\", \"meanVehPerHour\": 0,   \"sdVehPerHour\": 5 } ] }";

    private static readonly string[] Entries = { "C2", "C4" };
    private static readonly string[] Exits = { "C1", "C3" };

    private static OdMatrixFile Parse(string json)
    {
        var f = OdMatrix.Parse(json, out string error);
        Assert.That(error, Is.Null);
        return f;
    }

    [Test]
    public void Parse_ReadsPairs()
    {
        var f = Parse(Json);
        Assert.That(f.pairs.Count, Is.EqualTo(4));
        Assert.That(f.pairs[0].from, Is.EqualTo("C2"));
        Assert.That(f.pairs[0].meanVehPerHour, Is.EqualTo(500f));
        Assert.That(f.pairs[1].sdVehPerHour, Is.EqualTo(1f));
    }

    [Test]
    public void Parse_EmptyOrWithoutPairs_IsError()
    {
        Assert.That(OdMatrix.Parse("", out string e1), Is.Null); Assert.That(e1, Is.Not.Null);
        Assert.That(OdMatrix.Parse("{ \"pairs\": [] }", out string e2), Is.Null); Assert.That(e2, Is.Not.Null);
    }

    [Test]
    public void Validate_AcceptsEntryToExit()
    {
        Assert.That(OdMatrix.Validate(Parse(Json), Entries, Exits, out string error), Is.True, error);
    }

    [TestCase("{ \"pairs\": [ { \"from\": \"C3\", \"to\": \"C1\", \"meanVehPerHour\": 10 } ] }")]   // C3 no es entrada
    [TestCase("{ \"pairs\": [ { \"from\": \"C2\", \"to\": \"C4\", \"meanVehPerHour\": 10 } ] }")]   // C4 no es salida
    [TestCase("{ \"pairs\": [ { \"from\": \"C2\", \"to\": \"C9\", \"meanVehPerHour\": 10 } ] }")]   // no existe
    [TestCase("{ \"pairs\": [ { \"from\": \"C2\", \"to\": \"C3\", \"meanVehPerHour\": -1 } ] }")]   // media negativa
    [TestCase("{ \"pairs\": [ { \"from\": \"C2\", \"to\": \"C3\", \"meanVehPerHour\": 10 }, { \"from\": \"C2\", \"to\": \"C3\", \"meanVehPerHour\": 5 } ] }")]  // repetido
    public void Validate_RejectsInvalidPairs(string json)
    {
        Assert.That(OdMatrix.Validate(Parse(json), Entries, Exits, out string error), Is.False);
        Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Sample_MeanAndSdMatchFile()
    {
        var f = Parse(Json);
        var rng = TrafficManager.CreateRng(12345, OdDemand.RngOffset);
        const int n = 20000;
        var values = new List<float>(n);
        for (int i = 0; i < n; i++) values.Add(OdMatrix.Sample(f, rng)[0]);
        double mean = values.Average();
        double sd = System.Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (n - 1));
        Assert.That(mean, Is.EqualTo(500.0).Within(1.5));   // error estandar 50 / sqrt(20000) = 0.35
        Assert.That(sd, Is.EqualTo(50.0).Within(1.5));
    }

    [Test]
    public void Sample_NeverNegative_AndZeroMeanStaysZero()
    {
        var f = Parse("{ \"pairs\": [ { \"from\": \"C2\", \"to\": \"C3\", \"meanVehPerHour\": 10, \"sdVehPerHour\": 40 }," +
                      "              { \"from\": \"C4\", \"to\": \"C3\", \"meanVehPerHour\": 0, \"sdVehPerHour\": 40 } ] }");
        var rng = new System.Random(1);
        for (int i = 0; i < 5000; i++)
        {
            var q = OdMatrix.Sample(f, rng);
            Assert.That(q[0], Is.GreaterThanOrEqualTo(0f));
            Assert.That(q[1], Is.EqualTo(0f));
        }
    }

    [Test]
    public void Sample_SameSeed_SameMatrix_DifferentSeed_Different()
    {
        var f = Parse(Json);
        var a = OdMatrix.Sample(f, TrafficManager.CreateRng(7, OdDemand.RngOffset));
        var b = OdMatrix.Sample(f, TrafficManager.CreateRng(7, OdDemand.RngOffset));
        var c = OdMatrix.Sample(f, TrafficManager.CreateRng(8, OdDemand.RngOffset));
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.Not.EqualTo(c));
    }

    [Test]
    public void PickIndex_ProportionalToWeights()
    {
        var w = new[] { 490f, 10f, 0f };
        var rng = new System.Random(3);
        var counts = new int[3];
        const int n = 50000;
        for (int i = 0; i < n; i++) counts[OdMatrix.PickIndex(rng, w)]++;
        Assert.That(counts[2], Is.EqualTo(0));
        Assert.That(counts[1] / (double)n, Is.EqualTo(10.0 / 500.0).Within(0.003));
    }

    [Test]
    public void PickIndex_AllZero_IsMinusOne()
    {
        Assert.That(OdMatrix.PickIndex(new System.Random(1), new[] { 0f, 0f }), Is.EqualTo(-1));
    }
}
