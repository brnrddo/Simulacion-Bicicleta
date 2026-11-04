// ============================================================
// PedestrianRules.cs
// Reglas puras de los peatones (T6), testeables en EditMode.
//
// Velocidad de marcha: normal N(1.35, 0.20) m/s recortada a [0.9, 1.8] m/s
// (Knoblauch, Pietrucha y Nitzburg, 1996; percentil 15 = 1.25 m/s).
//
// Cruce con semaforo: el peaton empieza a cruzar solo con la luz "caminar" y
// si alcanza a terminar antes del rojo: tiempo de caminar restante + despeje
// (parpadeo) >= largo del cruce / su velocidad.
//
// Paso cebra (sin semaforo): el peaton tiene preferencia (Ley de Transito), pero
// no se lanza frente a un auto que no alcanza a detenerse: empieza a cruzar si
// cada auto que se acerca o bien puede detenerse comodamente antes del cruce,
// o bien llega despues de su brecha. Los autos ceden a quien espera en la cuneta
// si pueden detenerse comodamente (TrafficManager).
// ============================================================

using UnityEngine;

public static class PedestrianRules
{
    public const float SpeedMean = 1.35f;
    public const float SpeedSd = 0.20f;
    public const float SpeedMin = 0.9f;
    public const float SpeedMax = 1.8f;

    public static float SampleWalkingSpeed(System.Random rng, float mean = SpeedMean, float sd = SpeedSd,
                                           float min = SpeedMin, float max = SpeedMax)
        => Mathf.Clamp(mean + sd * DriverProfile.StandardNormal(rng), min, max);

    /// <summary>Con semaforo: solo con "caminar" y si termina el cruce antes del rojo peatonal.</summary>
    public static bool CanStartSignalized(PedestrianPhase phase, float timeToChangeS, float clearanceS,
                                          float crossingLengthM, float speedMps)
    {
        if (phase != PedestrianPhase.Walk) return false;
        float needed = crossingLengthM / Mathf.Max(speedMps, 0.1f);
        return timeToChangeS + clearanceS >= needed;
    }

    /// <summary>Distancia en que un auto se detiene comodamente (frenado + distancia minima).</summary>
    public static float ComfortStopDistance(float speedMps, float decelMps2, float minGapM)
        => speedMps * speedMps / (2f * Mathf.Max(decelMps2, 0.1f)) + minGapM;

    /// <summary>
    /// Paso cebra: el peaton puede empezar si el auto que se acerca puede detenerse comodamente
    /// antes del cruce o llega despues de 'pedGapS' segundos.
    /// </summary>
    public static bool ZebraSafeFromCar(float carDistanceM, float carSpeedMps, float carDecelMps2, float carMinGapM, float pedGapS)
    {
        if (carDistanceM < 0f) return true;  // ya paso
        if (carSpeedMps < 0.3f) return true;  // detenido (cediendo)
        if (carDistanceM >= ComfortStopDistance(carSpeedMps, carDecelMps2, carMinGapM)) return true;
        return carDistanceM / carSpeedMps > pedGapS;
    }

    /// <summary>
    /// Evento "cruce fuera del paso" (jaywalk): instante de partida cuando el tiempo de llegada del ciclista
    /// al punto de conflicto cae a tTarget (sorteado en [3, 4] s en el plan).
    /// </summary>
    public static bool JaywalkShouldStart(float cyclistDistanceM, float cyclistSpeedMps, float tTargetS)
    {
        if (cyclistSpeedMps < 0.5f) return false;
        return cyclistDistanceM / cyclistSpeedMps <= tTargetS;
    }

    /// <summary>Desplazamiento lateral del peaton (respecto del eje del ciclista) para una pasada a 'distanceM' del manubrio.</summary>
    public static float NearPassLateral(float handlebarHalfWidthM, float pedestrianRadiusM, float distanceM)
        => handlebarHalfWidthM + distanceM + pedestrianRadiusM;
}
