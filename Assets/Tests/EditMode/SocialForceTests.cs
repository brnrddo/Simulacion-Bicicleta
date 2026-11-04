// ============================================================
// SocialForceTests.cs
// Modelo de fuerza social (Helbing & Molnar, 1995; eliptica II de Johansson,
// Helbing & Shukla, 2007): impulso, repulsion, anisotropia, limite de
// velocidad y tres interacciones (de frente, cruce, peaton detenido).
// ============================================================

using NUnit.Framework;
using UnityEngine;

public class SocialForceTests
{
    private static readonly SocialForce.Params P = SocialForce.Params.Default;

    [Test]
    public void Driving_RelaxesToDesiredVelocity()
    {
        Vector2 v = Vector2.zero;
        for (int i = 0; i < 300; i++)   // 3 s con dt = 0.01 (6 tau)
            v = SocialForce.Integrate(v, SocialForce.Driving(v, Vector2.up, 1.3f, P.tauS), 0.01f, 10f);
        Assert.That(v.y, Is.EqualTo(1.3f).Within(0.01f));
        Assert.That(v.x, Is.EqualTo(0f).Within(1e-4f));
    }

    [Test]
    public void Repulsion_DecaysWithDistanceAndPointsAway()
    {
        // Ambos quietos (y = 0): la elipse es un circulo, b = |d|
        Vector2 near = SocialForce.Repulsion(Vector2.zero, Vector2.zero, Vector2.up, new Vector2(0f, 0.8f), Vector2.zero, 0.5f, P);
        Vector2 far = SocialForce.Repulsion(Vector2.zero, Vector2.zero, Vector2.up, new Vector2(0f, 1.6f), Vector2.zero, 0.5f, P);
        Assert.That(near.magnitude, Is.GreaterThan(far.magnitude));
        Assert.That(near.y, Is.LessThan(0f));   // empuja lejos del otro
        Assert.That(SocialForce.Repulsion(Vector2.zero, Vector2.zero, Vector2.up, new Vector2(0f, 9f), Vector2.zero, 0.5f, P),
                    Is.EqualTo(Vector2.zero));
    }

    [Test]
    public void Repulsion_AnticipatesApproachingPedestrian()
    {
        // A 2 m: uno que viene de frente empuja mas que uno quieto en el mismo lugar
        Vector2 still = SocialForce.Repulsion(Vector2.zero, Vector2.up * 1.3f, Vector2.up, new Vector2(0f, 2f), Vector2.zero, 0.5f, P);
        Vector2 coming = SocialForce.Repulsion(Vector2.zero, Vector2.up * 1.3f, Vector2.up, new Vector2(0f, 2f), Vector2.down * 1.3f, 0.5f, P);
        Assert.That(coming.magnitude, Is.GreaterThan(still.magnitude));
    }

    [Test]
    public void Anisotropy_FrontWeighsMoreThanBack()
    {
        Assert.That(SocialForce.Anisotropy(Vector2.up, Vector2.up, P.lambda), Is.EqualTo(1f).Within(1e-5f));
        Assert.That(SocialForce.Anisotropy(Vector2.up, Vector2.down, P.lambda), Is.EqualTo(P.lambda).Within(1e-5f));
    }

    [Test]
    public void Integrate_LimitsSpeed()
    {
        Vector2 v = SocialForce.Integrate(Vector2.zero, new Vector2(100f, 0f), 1f, 1.7f);
        Assert.That(v.magnitude, Is.EqualTo(1.7f).Within(1e-5f));
    }

    /// <summary>Dos peatones (radio 0.25 m) con el modelo; devuelve la distancia minima entre centros y el avance del primero.</summary>
    private static float Simulate(Vector2 p1, Vector2 d1, float v1, Vector2 p2, Vector2 d2, float v2, float seconds, out float advance1)
    {
        Vector2 vel1 = d1 * v1, vel2 = d2 * v2, start = p1;
        float minD = float.PositiveInfinity, dt = 0.0111f;
        for (float t = 0f; t < seconds; t += dt)
        {
            Vector2 h1 = vel1.sqrMagnitude > 1e-4f ? vel1 : d1, h2 = vel2.sqrMagnitude > 1e-4f ? vel2 : d2;
            Vector2 a1 = SocialForce.Driving(vel1, d1, v1, P.tauS) + SocialForce.Repulsion(p1, vel1, h1, p2, vel2, 0.5f, P);
            Vector2 a2 = SocialForce.Driving(vel2, d2, v2, P.tauS) + SocialForce.Repulsion(p2, vel2, h2, p1, vel1, 0.5f, P);
            vel1 = SocialForce.Integrate(vel1, a1, dt, P.maxSpeedFactor * Mathf.Max(v1, 0.5f));
            vel2 = SocialForce.Integrate(vel2, a2, dt, P.maxSpeedFactor * Mathf.Max(v2, 0.5f));
            p1 += vel1 * dt; p2 += vel2 * dt;
            minD = Mathf.Min(minD, (p1 - p2).magnitude);
        }
        advance1 = Vector2.Dot(p1 - start, d1);
        return minD;
    }

    [Test]
    public void HeadOn_NoOverlapAndBothKeepWalking()
    {
        float minD = Simulate(Vector2.zero, Vector2.up, 1.35f, new Vector2(0f, 12f), Vector2.down, 1.35f, 14f, out float adv);
        Assert.That(minD, Is.GreaterThan(0.6f));   // suma de radios 0.5 m + holgura
        Assert.That(adv, Is.GreaterThan(15f));     // no se traba
    }

    [Test]
    public void Crossing_NoOverlap()
    {
        float minD = Simulate(Vector2.zero, Vector2.up, 1.35f, new Vector2(-6f, 6f), Vector2.right, 1.35f, 14f, out _);
        Assert.That(minD, Is.GreaterThan(0.6f));
    }

    [Test]
    public void StandingPedestrian_IsWalkedAround()
    {
        float minD = Simulate(Vector2.zero, Vector2.up, 1.35f, new Vector2(0.05f, 8f), Vector2.up, 0f, 14f, out float adv);
        Assert.That(minD, Is.GreaterThan(0.6f));
        Assert.That(adv, Is.GreaterThan(12f));
    }
}
