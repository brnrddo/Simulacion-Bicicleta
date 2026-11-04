/// <summary>
/// Contrato minimo entre una fuente de input (bici real o teclado) y
/// BicycleController. Existe para que el modelo cinematico sea identico
/// en ambos casos: si la dinamica viviera dentro del processor de
/// sensores, el fallback de teclado la evitaria por completo y los
/// pilotos sin hardware no predecirian nada sobre la sesion real.
///
/// Unidades: SI. Velocidad en m/s, angulo en grados (positivo = derecha).
/// </summary>
public interface IBikeInputSource
{
    /// <summary>True si esta fuente esta entregando datos utilizables ahora.</summary>
    bool IsActive { get; }

    /// <summary>Velocidad longitudinal estimada, m/s. Nunca negativa.</summary>
    float SpeedMps { get; }

    /// <summary>
    /// Angulo fisico del manubrio en grados, SIN limitar por velocidad.
    /// El limite dinamico es decision del modelo de vehiculo, no de la
    /// fuente de input -- y el modelo visual necesita el valor fisico
    /// crudo para que manos y ojos coincidan.
    /// </summary>
    float SteeringDeg { get; }
}
