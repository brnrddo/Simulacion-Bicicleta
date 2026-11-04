// ============================================================
// SessionClock.cs
// Reloj de pared de alta resolucion para toda la sesion.
// Time.time de Unity es tiempo SIMULADO (se detiene en pausa y en
// FixedUpdate avanza en pasos fijos). Para sincronizar despues con
// fisiologia (EDA, HR, LSL) se necesita tiempo real: por eso se
// loguean ambos.
// ============================================================

using System;
using System.Diagnostics;

public static class SessionClock
{
    private static readonly Stopwatch stopwatch = new Stopwatch();

    /// <summary>Hora UTC en que arranco la sesion. Va al archivo _meta.</summary>
    public static DateTime StartUtc { get; private set; }

    public static bool IsRunning => stopwatch.IsRunning;

    /// <summary>Segundos de reloj real desde el inicio de la sesion.</summary>
    public static double Now => stopwatch.Elapsed.TotalSeconds;

    /// <summary>Reinicia el reloj. Lo llama ExperimentLogger en su Awake.</summary>
    public static void Start()
    {
        StartUtc = DateTime.UtcNow;
        stopwatch.Restart();
    }
}
