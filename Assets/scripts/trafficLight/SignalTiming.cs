// ============================================================
// SignalTiming.cs
// Tipos y logica pura de los semaforos (sin MonoBehaviour), para que el
// ciclo y la regla del amarillo se puedan testear en EditMode.
//
// Ciclo de dos ejes (el primero es startingAxis):
//   verde eje 1 -> amarillo eje 1 -> todo-rojo -> verde eje 2 -> amarillo eje 2 -> todo-rojo
// El rojo de un eje queda implicito: dura amarillo + todo-rojo + verde + amarillo + todo-rojo del otro.
//
// Tiempos por defecto (verde 25 s, amarillo 3 s, todo-rojo 2 s): punto de partida
// acordado con Bernardo. El amarillo de 3 s es la practica habitual en Chile.
// TODO: verificar y citar el valor normativo exacto en el Manual de Senalizacion
// de Transito del MTT (Capitulo 4, Semaforos) antes de reportarlo en el paper.
// ============================================================

using System;
using UnityEngine;

/// <summary>Acceso = brazo por el que LLEGAN los autos (North = vienen desde el norte).</summary>
public enum Approach { North, South, East, West }

public enum SignalAxis { NorthSouth, EastWest }

public enum SignalPhase { Red, Yellow, Green }

public enum PedestrianPhase { DontWalk, Walk, FlashingDontWalk }

public readonly struct SignalState
{
    public readonly SignalPhase Phase;
    public readonly float TimeInPhaseS;
    public readonly float TimeToChangeS;

    public SignalState(SignalPhase phase, float timeInPhaseS, float timeToChangeS)
    {
        Phase = phase;
        TimeInPhaseS = timeInPhaseS;
        TimeToChangeS = timeToChangeS;
    }
}

public readonly struct PedestrianState
{
    public readonly PedestrianPhase Phase;
    public readonly float TimeInPhaseS;
    public readonly float TimeToChangeS;

    public PedestrianState(PedestrianPhase phase, float timeInPhaseS, float timeToChangeS)
    {
        Phase = phase;
        TimeInPhaseS = timeInPhaseS;
        TimeToChangeS = timeToChangeS;
    }
}

[Serializable]
public struct SignalTimingConfig
{
    [Min(1f)] public float greenNorthSouthS;
    [Min(1f)] public float greenEastWestS;
    [Min(0f)] public float yellowS;
    [Min(0f)] public float allRedS;
    [Tooltip("Desfase del ciclo (s), para coordinar varias intersecciones.")]
    public float offsetS;
    public SignalAxis startingAxis;
    [Tooltip("Ultimos segundos del verde vehicular paralelo en que la luz peatonal parpadea.")]
    [Min(0f)] public float pedestrianClearanceS;

    public static SignalTimingConfig Default => new SignalTimingConfig
    {
        greenNorthSouthS = 25f,
        greenEastWestS = 25f,
        yellowS = 3f,
        allRedS = 2f,
        offsetS = 0f,
        startingAxis = SignalAxis.NorthSouth,
        pedestrianClearanceS = 5f,
    };

    public float CycleS => greenNorthSouthS + greenEastWestS + 2f * (yellowS + allRedS);
}

public static class SignalCycle
{
    public static SignalAxis AxisOf(Approach a) =>
        a == Approach.North || a == Approach.South ? SignalAxis.NorthSouth : SignalAxis.EastWest;

    /// <summary>Estado de la senal vehicular de un eje en el tiempo t (s desde el inicio de la simulacion).</summary>
    public static SignalState Evaluate(in SignalTimingConfig c, double t, SignalAxis axis)
    {
        float cycle = c.CycleS;
        SignalAxis first = c.startingAxis;
        float greenFirst = first == SignalAxis.NorthSouth ? c.greenNorthSouthS : c.greenEastWestS;
        float greenAxis = axis == SignalAxis.NorthSouth ? c.greenNorthSouthS : c.greenEastWestS;

        // Inicio del verde de este eje dentro del ciclo
        float greenStart = axis == first ? 0f : greenFirst + c.yellowS + c.allRedS;

        float tc = Mod(t + c.offsetS - greenStart, cycle);  // tiempo desde el inicio del verde de este eje

        if (tc < greenAxis)
            return new SignalState(SignalPhase.Green, tc, greenAxis - tc);
        if (tc < greenAxis + c.yellowS)
            return new SignalState(SignalPhase.Yellow, tc - greenAxis, greenAxis + c.yellowS - tc);
        return new SignalState(SignalPhase.Red, tc - greenAxis - c.yellowS, cycle - tc);
    }

    public static SignalState Evaluate(in SignalTimingConfig c, double t, Approach a) => Evaluate(c, t, AxisOf(a));

    /// <summary>
    /// Luz peatonal del cruce que atraviesa el brazo 'arm'. Los peatones de ese cruce caminan en
    /// paralelo al eje perpendicular al brazo (cruzar el brazo Norte = caminar Este-Oeste), asi que
    /// "caminar" va durante el verde de ese eje, menos el despeje con parpadeo al final.
    /// </summary>
    public static PedestrianState EvaluatePedestrian(in SignalTimingConfig c, double t, Approach arm)
    {
        SignalAxis parallel = AxisOf(arm) == SignalAxis.NorthSouth ? SignalAxis.EastWest : SignalAxis.NorthSouth;
        SignalState v = Evaluate(c, t, parallel);
        float green = parallel == SignalAxis.NorthSouth ? c.greenNorthSouthS : c.greenEastWestS;
        float clearance = Mathf.Min(c.pedestrianClearanceS, green);
        float walk = green - clearance;

        if (v.Phase == SignalPhase.Green && v.TimeInPhaseS < walk)
            return new PedestrianState(PedestrianPhase.Walk, v.TimeInPhaseS, walk - v.TimeInPhaseS);
        if (v.Phase == SignalPhase.Green)
            return new PedestrianState(PedestrianPhase.FlashingDontWalk, v.TimeInPhaseS - walk, v.TimeToChangeS);

        // Rojo peatonal: desde el fin del verde vehicular paralelo hasta su proximo verde
        float sinceGreenEnd = v.Phase == SignalPhase.Yellow ? v.TimeInPhaseS : c.yellowS + v.TimeInPhaseS;
        float toWalk = v.Phase == SignalPhase.Yellow ? v.TimeToChangeS + (c.CycleS - green - c.yellowS) : v.TimeToChangeS;
        return new PedestrianState(PedestrianPhase.DontWalk, sinceGreenEnd, toWalk);
    }

    private static float Mod(double x, float m) => (float)(x - m * Math.Floor(x / m));
}

public static class SignalRules
{
    /// <summary>
    /// Decision en amarillo: se detiene si puede hacerlo frenando como maximo con bSafe
    /// (v^2 / (2 bSafe) &lt;= distancia a la linea). Si no, sigue.
    /// </summary>
    public static bool ShouldStopOnYellow(float speedMps, float distanceToLineM, float safeDecelMps2)
    {
        if (distanceToLineM < 0f) return false;            // ya paso la linea
        if (safeDecelMps2 <= 0f) return false;
        float brakingDistance = speedMps * speedMps / (2f * safeDecelMps2);
        return brakingDistance <= distanceToLineM;
    }
}
