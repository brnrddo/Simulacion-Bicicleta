// ============================================================
// BikeSignalTests.cs
// Filtro 1 euro del manubrio y validacion de los paquetes del Arduino.
// ============================================================

using NUnit.Framework;
using UnityEngine;

public class BikeSignalTests
{
    private const float Dt = 1f / 90f;
    private const float CountDeg = 45f / 260f;   // una cuenta del ADC en grados (0.17)

    private static OneEuroFilter NewFilter() => new OneEuroFilter { MinCutoffHz = 2f, Beta = 0.1f, DCutoffHz = 1f };

    [Test]
    public void OneEuro_ConstantInput_StaysConstant()
    {
        var f = NewFilter();
        for (int i = 0; i < 200; i++) Assert.That(f.Filter(12.5f, Dt), Is.EqualTo(12.5f).Within(1e-4f));
    }

    [Test]
    public void OneEuro_AdcDitherAtRest_IsAttenuatedTenfold()
    {
        // Peor caso del temblor: el ADC alterna una cuenta en cada muestra con el manubrio quieto
        var f = NewFilter();
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < 900; i++)
        {
            float y = f.Filter((i % 2 == 0 ? 0.5f : -0.5f) * CountDeg, Dt);
            if (i < 180) continue;   // se descartan los primeros 2 s
            min = Mathf.Min(min, y); max = Mathf.Max(max, y);
        }
        Assert.That(max - min, Is.LessThan(CountDeg / 10f));
    }

    [Test]
    public void OneEuro_FastTurn_LagsLessThan50Ms()
    {
        // Rampa de 60 grados/s (maniobra rapida): el retraso en regimen es error / velocidad
        var f = NewFilter();
        float y = 0f, x = 0f;
        for (int i = 0; i < 45; i++) { x = 60f * i * Dt; y = f.Filter(x, Dt); }
        Assert.That((x - y) / 60f, Is.LessThan(0.050f));
    }

    [Test]
    public void OneEuro_Step_ConvergesToTarget()
    {
        var f = NewFilter();
        f.Filter(0f, Dt);
        float y = 0f;
        for (int i = 0; i < 90; i++) y = f.Filter(20f, Dt);
        Assert.That(y, Is.EqualTo(20f).Within(0.05f));
    }

    // ---------------- Paquetes del Arduino ----------------
    private const float Dpp = 0.35f;      // 6 imanes, rueda de 2.1 m
    private const float MaxSpeed = 25f;
    private const uint PacketUs = 11111;  // 90 Hz

    [Test]
    public void Packet_NormalPulse_IsAccepted()
    {
        // 4 m/s: un pulso cada 87.5 ms
        Assert.That(PulsePacketCheck.IsPlausible(100, 1_000_000, 1_080_000, 101, 1_087_500, 1_080_000 + PacketUs, Dpp, MaxSpeed), Is.True);
    }

    [Test]
    public void Packet_NoNewPulse_IsAccepted()
    {
        Assert.That(PulsePacketCheck.IsPlausible(100, 1_000_000, 1_080_000, 100, 1_000_000, 1_080_000 + PacketUs, Dpp, MaxSpeed), Is.True);
    }

    [Test]
    public void Packet_SamePacketReadTwice_IsAccepted()
    {
        Assert.That(PulsePacketCheck.IsPlausible(100, 1_000_000, 1_080_000, 100, 1_000_000, 1_080_000, Dpp, MaxSpeed), Is.True);
    }

    [Test]
    public void Packet_TruncatedCounter_IsRejected()
    {
        // Linea cortada: "1234,..." llega como "34,..."
        Assert.That(PulsePacketCheck.IsPlausible(1234, 5_000_000, 5_080_000, 34, 5_000_000, 5_080_000 + PacketUs, Dpp, MaxSpeed), Is.False);
    }

    [Test]
    public void Packet_CounterBackToRealValueAfterCorruptLine_IsAcceptedAgainstLastGoodOne()
    {
        // El paquete corrupto no se guarda, asi que el siguiente bueno se compara con el ultimo bueno
        Assert.That(PulsePacketCheck.IsPlausible(1234, 5_000_000, 5_080_000, 1234, 5_000_000, 5_080_000 + 2 * PacketUs, Dpp, MaxSpeed), Is.True);
    }

    [Test]
    public void Packet_ArduinoReset_IsRejected()
    {
        // Tras un reinicio el reloj y el contador vuelven a cero. Es el caso que teletransporto la bici
        Assert.That(PulsePacketCheck.IsPlausible(1234, 5_000_000, 5_080_000, 0, 0, 20_000, Dpp, MaxSpeed), Is.False);
    }

    [Test]
    public void Packet_TooManyPulsesForElapsedTime_IsRejected()
    {
        Assert.That(PulsePacketCheck.IsPlausible(100, 1_000_000, 1_080_000, 160, 1_090_000, 1_080_000 + PacketUs, Dpp, MaxSpeed), Is.False);
    }

    [Test]
    public void Packet_ImpossibleSpeed_IsRejected()
    {
        // Un pulso 1 ms despues del anterior serian 350 m/s
        Assert.That(PulsePacketCheck.IsPlausible(100, 1_085_000, 1_085_500, 101, 1_086_000, 1_085_500 + PacketUs, Dpp, MaxSpeed), Is.False);
    }

    [Test]
    public void Packet_MicrosOverflow_IsAccepted()
    {
        // micros() se desborda cada ~71 min: la resta sin signo debe seguir funcionando
        uint prevNow = uint.MaxValue - 5000;
        uint prevPulse = uint.MaxValue - 90_000;
        uint now = prevNow + PacketUs;        // ya desbordado
        uint pulse = prevPulse + 87_500;      // justo antes del desborde
        Assert.That(now, Is.LessThan(prevNow));
        Assert.That(PulsePacketCheck.IsPlausible(100, prevPulse, prevNow, 101, pulse, now, Dpp, MaxSpeed), Is.True);
    }

    [Test]
    public void Packet_LongUnityHitchAtSpeed_IsAccepted()
    {
        // 1 s sin leer a 10 m/s: ~28 pulsos de golpe, pero posibles
        Assert.That(PulsePacketCheck.IsPlausible(100, 1_000_000, 1_010_000, 128, 1_980_000, 2_010_000, Dpp, MaxSpeed), Is.True);
    }
}
