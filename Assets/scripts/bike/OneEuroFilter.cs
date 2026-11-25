// ============================================================
// OneEuroFilter.cs
// Filtro 1 euro (Casiez, Roussel y Vogel, CHI 2012): pasa-bajos cuya
// frecuencia de corte sube con la velocidad de la senal.
//   corte = minCutoff + beta * |dx/dt|
// Con la senal quieta filtra fuerte (quita el temblor); cuando se mueve
// rapido casi no filtra (no agrega retraso).
// Ajuste recomendado por los autores: con beta = 0, bajar minCutoff hasta
// que no tiemble en reposo; despues subir beta hasta que no se note
// retraso en los movimientos rapidos.
// Clase pura, sin MonoBehaviour, para poder testearla.
// ============================================================

using UnityEngine;

public class OneEuroFilter
{
    /// <summary>Corte con la senal quieta (Hz). Mas bajo = menos temblor y mas retraso.</summary>
    public float MinCutoffHz = 2f;
    /// <summary>Cuanto sube el corte por cada unidad/s de velocidad de la senal. Mas alto = menos retraso al moverse.</summary>
    public float Beta = 0.1f;
    /// <summary>Corte del pasa-bajos de la derivada (Hz).</summary>
    public float DCutoffHz = 1f;

    private bool hasPrevious;
    private float previousValue;
    private float previousDerivative;

    public void Reset() => hasPrevious = false;

    public float Filter(float value, float dt)
    {
        if (!hasPrevious || dt <= 0f)
        {
            hasPrevious = true;
            previousValue = value;
            previousDerivative = 0f;
            return value;
        }

        float derivative = (value - previousValue) / dt;
        float smoothedDerivative = Mathf.Lerp(previousDerivative, derivative, SmoothingFactor(DCutoffHz, dt));

        float cutoffHz = MinCutoffHz + Beta * Mathf.Abs(smoothedDerivative);
        float filtered = Mathf.Lerp(previousValue, value, SmoothingFactor(cutoffHz, dt));

        previousValue = filtered;
        previousDerivative = smoothedDerivative;
        return filtered;
    }

    /// <summary>Factor del pasa-bajos exponencial para una frecuencia de corte y un paso de tiempo.</summary>
    public static float SmoothingFactor(float cutoffHz, float dt)
    {
        float tau = 1f / (2f * Mathf.PI * Mathf.Max(cutoffHz, 1e-3f));
        return 1f / (1f + tau / dt);
    }
}
