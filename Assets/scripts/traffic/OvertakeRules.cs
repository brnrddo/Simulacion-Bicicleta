// ============================================================
// OvertakeRules.cs
// Reglas puras del adelantamiento al ciclista (T4), testeables en EditMode.
//
// Distancia lateral objetivo: lognormal ajustada a Beck et al. (2019),
// Accident Analysis & Prevention 128, 253-260: mediana 1.73 m, rango
// intercuartil 1.37-2.24 m, recortada a [0.5, 3.0] m. La distancia es del
// extremo del manubrio al costado del auto.
//   mu    = ln(mediana)
//   sigma = ln(Q3/Q1) / (2 * 0.6745)   (0.6745 = cuantil 0.75 de la normal estandar)
//
// Maniobra lateral: perfil quintico (polinomio minimo-jerk) de duracion T; su
// aceleracion lateral maxima es 5.7735 * d / T^2, asi que para no superar a_max
// T >= sqrt(5.7735 * d / a_max).
// Acople con el avance: la maniobra no puede superar un angulo maximo con el
// carril (pendiente lateral dLat/ds <= tan(angulo)); su pendiente maxima es
// 1.875 * d / L, asi que necesita al menos L_min = 1.875 * d / tan(angulo)
// metros de avance. A baja velocidad se alarga y detenido se congela.
// ============================================================

using UnityEngine;

public static class OvertakeRules
{
    public const float QuinticPeakAccelFactor = 5.7735f;  // 10 / sqrt(3)

    /// <summary>sigma de la lognormal a partir del rango intercuartil.</summary>
    public static float SigmaFromIqr(float q1, float q3) => Mathf.Log(q3 / q1) / (2f * 0.6745f);

    /// <summary>Sortea una distancia lateral lognormal recortada a [min, max].</summary>
    public static float SamplePassingDistance(System.Random rng, float median, float sigma, float min, float max)
    {
        float z = DriverProfile.StandardNormal(rng);
        return Mathf.Clamp(median * Mathf.Exp(sigma * z), min, max);
    }

    /// <summary>
    /// Desplazamiento lateral del eje del auto (respecto del eje de su carril, + = derecha) para pasar
    /// por la IZQUIERDA del ciclista dejando 'passingDistance' entre el extremo del manubrio y el costado.
    /// bikeLateral = posicion lateral del ciclista respecto del eje del carril (+ = derecha).
    /// </summary>
    public static float RequiredCarLateral(float bikeLateral, float handlebarHalfWidth, float carHalfWidth, float passingDistance)
        => bikeLateral - handlebarHalfWidth - passingDistance - carHalfWidth;

    /// <summary>Distancia manubrio-costado del auto con el auto en carLateral.</summary>
    public static float Clearance(float bikeLateral, float carLateral, float handlebarHalfWidth, float carHalfWidth)
        => bikeLateral - handlebarHalfWidth - (carLateral + carHalfWidth);

    /// <summary>Duracion de la maniobra para desplazarse 'distance' sin superar a_max (y al menos minDuration).</summary>
    public static float ManeuverDuration(float distance, float maxLateralAccel, float minDuration, float maxDuration)
    {
        float t = Mathf.Sqrt(QuinticPeakAccelFactor * Mathf.Abs(distance) / Mathf.Max(maxLateralAccel, 0.05f));
        return Mathf.Clamp(t, minDuration, maxDuration);
    }

    /// <summary>Perfil quintico: fraccion recorrida (0..1) en el instante normalizado x = t / T.</summary>
    public static float Quintic(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * x * (10f + x * (-15f + 6f * x));
    }

    /// <summary>Pendiente maxima del perfil quintico: max dQ/dx = 1.875 (en x = 0.5).</summary>
    public const float QuinticPeakSlopeFactor = 1.875f;

    /// <summary>Derivada del perfil quintico dQ/dx.</summary>
    public static float QuinticSlope(float x)
    {
        x = Mathf.Clamp01(x);
        return 30f * x * x * (1f - x) * (1f - x);
    }

    /// <summary>
    /// Largo minimo de la maniobra (m) para desplazarse 'distance' sin que el angulo del auto con su
    /// carril supere atan(maxSlope): max dLat/ds = 1.875 * d / L  ->  L = 1.875 * d / maxSlope.
    /// </summary>
    public static float MinManeuverLength(float distance, float maxSlope)
        => QuinticPeakSlopeFactor * Mathf.Abs(distance) / Mathf.Max(maxSlope, 0.01f);

    /// <summary>
    /// Ritmo de avance de la maniobra (1/s): el del perfil en el tiempo (1/T) o, si el auto va lento,
    /// el que da su avance longitudinal sobre el largo minimo (v / L_min). Detenido = 0.
    /// </summary>
    public static float LateralProgressRate(float speed, float duration, float distance, float maxSlope)
    {
        float byTime = 1f / Mathf.Max(duration, 0.01f);
        float lMin = MinManeuverLength(distance, maxSlope);
        float byDistance = lMin > 1e-4f ? Mathf.Max(speed, 0f) / lMin : float.PositiveInfinity;
        return Mathf.Min(byTime, byDistance);
    }

    /// <summary>Pendiente lateral dLat/ds de la trayectoria en el punto x de la maniobra (orienta el auto).</summary>
    public static float LateralSlope(float speed, float duration, float distance, float maxSlope, float x)
    {
        // dLat/ds = d * Q'(x) * (dx/dt) / v ; con v -> 0 el limite es d * Q'(x) / L_min
        float lMin = MinManeuverLength(distance, maxSlope);
        float dxds = speed > 1e-3f ? LateralProgressRate(speed, duration, distance, maxSlope) / speed
                                   : (lMin > 1e-4f ? 1f / lMin : 0f);
        return distance * QuinticSlope(x) * dxds;
    }

    /// <summary>Aceleracion del perfil quintico (por unidad de desplazamiento y con T = 1).</summary>
    public static float QuinticAccel(float x)
    {
        x = Mathf.Clamp01(x);
        return 60f * x - 180f * x * x + 120f * x * x * x;
    }

    /// <summary>Tiempo a colision con un vehiculo de frente (PositiveInfinity si no se acercan).</summary>
    public static float OncomingTtc(float distanceM, float ownSpeed, float oncomingSpeed)
    {
        float closing = ownSpeed + oncomingSpeed;
        if (distanceM <= 0f) return 0f;
        return closing > 0.1f ? distanceM / closing : float.PositiveInfinity;
    }
}
