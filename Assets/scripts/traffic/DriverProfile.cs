// ============================================================
// DriverProfile.cs
// Distribucion de parametros IDM de la poblacion de conductores.
// Cada auto que aparece sortea sus propios parametros (con la semilla
// del experimento), asi el trafico es heterogeneo pero reproducible.
// Crear con: Create > SimBici > Driver Profile.
//
// Valores por defecto: punto de partida para trafico urbano, a
// calibrar en piloto. Reportar los valores usados en el paper.
// ============================================================

using UnityEngine;

[CreateAssetMenu(fileName = "DriverProfile", menuName = "SimBici/Driver Profile")]
public class DriverProfile : ScriptableObject
{
    [Header("Velocidad deseada = limite del carril x factor")]
    [Tooltip("1.0 = conduce exactamente al limite. 1.05 = 5% sobre el limite en promedio.")]
    public float speedFactorMean = 1.0f;
    public float speedFactorSd = 0.08f;
    public Vector2 speedFactorRange = new Vector2(0.8f, 1.2f);

    [Header("T: tiempo de separacion (s)")]
    public float timeHeadwayMean = 1.3f;
    public float timeHeadwaySd = 0.2f;
    public Vector2 timeHeadwayRange = new Vector2(0.8f, 2.0f);

    [Header("s0: distancia minima detenido (m)")]
    public float minGapMean = 2.0f;
    public float minGapSd = 0.3f;
    public Vector2 minGapRange = new Vector2(1.2f, 3.0f);

    [Header("a: aceleracion maxima (m/s2)")]
    public float maxAccelMean = 1.5f;
    public float maxAccelSd = 0.2f;
    public Vector2 maxAccelRange = new Vector2(0.9f, 2.2f);

    [Header("b: desaceleracion comoda (m/s2)")]
    public float comfortDecelMean = 2.0f;
    public float comfortDecelSd = 0.3f;
    public Vector2 comfortDecelRange = new Vector2(1.3f, 3.0f);

    [Header("delta: exponente")]
    public float exponent = 4f;

    [Header("Brecha critica (s): aceptacion de brecha en giros y accesos con PARE")]
    [Tooltip("Giro a la izquierda permisivo desde la via con prioridad. Punto de partida del plan: 4.5 s.")]
    public float criticalGapMean = 4.5f;
    public float criticalGapSd = 0.6f;
    public Vector2 criticalGapRange = new Vector2(3.0f, 7.0f);
    [Tooltip("Se suma al salir desde un acceso con PARE/CEDA. Referencia aproximada: el HCM da ~4 s para " +
             "el giro a la izquierda desde la via principal y ~6-7 s para maniobras desde la secundaria " +
             "(TODO: verificar valores y edicion antes de reportar).")]
    public float minorStreetExtraGapS = 2.0f;

    /// <summary>
    /// Sortea un conductor. desiredSpeedMps queda en 0: la velocidad deseada real
    /// depende del limite de cada carril (ver TrafficVehicle.SpeedFactor).
    /// </summary>
    public IdmParams Sample(System.Random rng, out float speedFactor)
    {
        speedFactor = Truncated(rng, speedFactorMean, speedFactorSd, speedFactorRange);
        return new IdmParams
        {
            desiredSpeedMps = 0f,
            timeHeadwayS = Truncated(rng, timeHeadwayMean, timeHeadwaySd, timeHeadwayRange),
            minGapM = Truncated(rng, minGapMean, minGapSd, minGapRange),
            maxAccelMps2 = Truncated(rng, maxAccelMean, maxAccelSd, maxAccelRange),
            comfortDecelMps2 = Truncated(rng, comfortDecelMean, comfortDecelSd, comfortDecelRange),
            exponent = exponent,
        };
    }

    [Header("Adelantamiento al ciclista: tiempo siguiendolo antes de adelantar, t_follow (s)")]
    [Tooltip("Solo aplica si el ciclista va dentro de la trayectoria del auto. Punto de partida; calibrar en piloto.")]
    public float followBeforeOvertakeMean = 2.0f;
    public float followBeforeOvertakeSd = 1.0f;
    public Vector2 followBeforeOvertakeRange = new Vector2(0.5f, 6.0f);

    /// <summary>Sortea t_follow del conductor (despues de SampleCriticalGap, con el mismo RNG).</summary>
    public float SampleFollowBeforeOvertake(System.Random rng) =>
        Truncated(rng, followBeforeOvertakeMean, followBeforeOvertakeSd, followBeforeOvertakeRange);

    /// <summary>Sortea la brecha critica del conductor (se llama despues de Sample, con el mismo RNG).</summary>
    public float SampleCriticalGap(System.Random rng) => Truncated(rng, criticalGapMean, criticalGapSd, criticalGapRange);

    /// <summary>Normal truncada (se recorta al rango).</summary>
    private static float Truncated(System.Random rng, float mean, float sd, Vector2 range)
    {
        return Mathf.Clamp(mean + sd * StandardNormal(rng), range.x, range.y);
    }

    /// <summary>Box-Muller con System.Random (reproducible con semilla, a diferencia de UnityEngine.Random compartido).</summary>
    public static float StandardNormal(System.Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();  // (0,1]: evita log(0)
        double u2 = rng.NextDouble();
        return (float)(System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2));
    }
}
