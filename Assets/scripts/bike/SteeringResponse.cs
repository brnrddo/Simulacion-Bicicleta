// ============================================================
// SteeringResponse.cs
// Respuesta de direccion "por curvatura": el manubrio manda velocidad de
// giro (yaw rate) en vez de angulo de rueda.
//
// Problema del modelo cinematico directo (omega = v tan(delta) / L): la
// ganancia crece con la velocidad. A 5 m/s, 1 grado de manubrio gira la
// vista 5 grados/s; a 10 m/s, 10 grados/s, y el limite dinamico satura a
// los 17 y 4.7 grados de 45: casi todo el recorrido del manubrio queda
// muerto y el rango util es diminuto.
//
// Aqui:
//   h      = delta / delta_max                  (-1..1)
//   f(h)   = (1 - e) |h| + e |h|^3              (curva "expo": centro suave)
//   omega  = omega_max(v) f(h)                  (todo el recorrido sirve)
//   omega_max(v) = min(g tan(phi_max) / v, omega_comfort)
// con un tope: nunca mas curvatura que la cinematica real del manubrio,
//   |omega| <= v tan|delta| / L
// Asi el modo nunca es mas sensible que el cinematico, a baja velocidad
// coincide con el, y la bici no gira detenida.
// Funciones puras, sin MonoBehaviour, para poder testearlas.
// ============================================================

using UnityEngine;

public static class SteeringResponse
{
    /// <summary>Curva expo normalizada: f(0) = 0, f(1) = 1. expo = 0 es lineal.</summary>
    public static float Expo(float h, float expo)
    {
        h = Mathf.Clamp01(Mathf.Abs(h));
        return (1f - expo) * h + expo * h * h * h;
    }

    /// <summary>Velocidad de giro maxima a esta velocidad (rad/s): inclinacion fisica y techo de comfort.</summary>
    public static float MaxYawRateRad(float speedMps, float maxLeanDeg, float comfortMaxYawRateDeg, float gravity)
    {
        float comfort = comfortMaxYawRateDeg * Mathf.Deg2Rad;
        if (speedMps < 1e-3f) return comfort;
        return Mathf.Min(gravity * Mathf.Tan(maxLeanDeg * Mathf.Deg2Rad) / speedMps, comfort);
    }

    /// <summary>Velocidad de giro que da el modelo cinematico para este angulo (rad/s, con signo).</summary>
    public static float KinematicYawRateRad(float steeringDeg, float speedMps, float maxSteeringDeg, float wheelbaseM)
    {
        float delta = Mathf.Clamp(steeringDeg, -maxSteeringDeg, maxSteeringDeg) * Mathf.Deg2Rad;
        return speedMps * Mathf.Tan(delta) / Mathf.Max(wheelbaseM, 0.01f);
    }

    /// <summary>Velocidad de giro objetivo del modo por curvatura (rad/s, positivo = derecha).</summary>
    public static float CurvatureMappedYawRateRad(float steeringDeg, float speedMps, float maxSteeringDeg, float expo,
                                                  float wheelbaseM, float maxLeanDeg, float comfortMaxYawRateDeg,
                                                  float gravity)
    {
        if (speedMps <= 0f || maxSteeringDeg <= 0f) return 0f;

        float mapped = MaxYawRateRad(speedMps, maxLeanDeg, comfortMaxYawRateDeg, gravity)
                     * Expo(steeringDeg / maxSteeringDeg, expo);
        float kinematic = Mathf.Abs(KinematicYawRateRad(steeringDeg, speedMps, maxSteeringDeg, wheelbaseM));

        return Mathf.Sign(steeringDeg) * Mathf.Min(mapped, kinematic);
    }

    /// <summary>
    /// Aplica el limite de aceleracion de giro y el tope cinematico absoluto (a esta velocidad la bici
    /// no puede girar mas rapido que con el manubrio a fondo; detenida, no gira).
    /// </summary>
    public static float LimitYawRateRad(float previousRad, float targetRad, float maxYawAccelDegPerSec2, float dt,
                                        float speedMps, float maxSteeringDeg, float wheelbaseM)
    {
        float next = maxYawAccelDegPerSec2 > 0f
            ? Mathf.MoveTowards(previousRad, targetRad, maxYawAccelDegPerSec2 * Mathf.Deg2Rad * dt)
            : targetRad;
        float cap = Mathf.Abs(KinematicYawRateRad(maxSteeringDeg, Mathf.Max(speedMps, 0f), maxSteeringDeg, wheelbaseM));
        return Mathf.Clamp(next, -cap, cap);
    }
}
