// ============================================================
// IntelligentDriverModel.cs
// Modelo de seguimiento vehicular IDM (Treiber, Hennecke & Helbing, 2000,
// Physical Review E 62, 1805). Calcula la aceleracion longitudinal de un
// auto a partir de su velocidad, la distancia al vehiculo de adelante
// (o a la linea de detencion / al ciclista) y la velocidad relativa.
//
//   a_IDM = a * [ 1 - (v/v0)^delta - (s*/s)^2 ]
//   s*    = s0 + max(0, v*T + v*dv / (2*sqrt(a*b)))
//
// v  = velocidad propia          v0 = velocidad deseada
// s  = distancia libre (gap)     dv = v - v_lider (velocidad de acercamiento)
// T  = tiempo de separacion      s0 = distancia minima detenido
// a  = aceleracion maxima        b  = desaceleracion comoda
// ============================================================

using System;
using UnityEngine;

[Serializable]
public struct IdmParams
{
    [Tooltip("v0: velocidad deseada (m/s). Se calcula como limite del carril x factor del conductor.")]
    public float desiredSpeedMps;
    [Tooltip("T: tiempo de separacion deseado con el vehiculo de adelante (s).")]
    public float timeHeadwayS;
    [Tooltip("s0: distancia minima al detenerse detras de otro vehiculo (m).")]
    public float minGapM;
    [Tooltip("a: aceleracion maxima (m/s2).")]
    public float maxAccelMps2;
    [Tooltip("b: desaceleracion comoda (m/s2).")]
    public float comfortDecelMps2;
    [Tooltip("delta: exponente de aceleracion. 4 es el valor estandar.")]
    public float exponent;
}

public static class IntelligentDriverModel
{
    /// <summary>Tope de frenado fisico (m/s2). El IDM puede pedir mas en emergencias.</summary>
    public const float MaxBrakeMps2 = 9f;

    /// <summary>
    /// Aceleracion IDM.
    /// gapM = distancia libre al obstaculo (float.PositiveInfinity si no hay).
    /// approachRateMps = v_propia - v_lider (positivo = me acerco).
    /// </summary>
    public static float Acceleration(in IdmParams p, float speedMps, float gapM, float approachRateMps)
    {
        float v0 = Mathf.Max(p.desiredSpeedMps, 0.1f);
        float freeTerm = 1f - Mathf.Pow(Mathf.Max(speedMps, 0f) / v0, p.exponent);

        if (float.IsPositiveInfinity(gapM))
            return Mathf.Max(p.maxAccelMps2 * freeTerm, -MaxBrakeMps2);

        float sStar = p.minGapM + Mathf.Max(0f,
            speedMps * p.timeHeadwayS +
            speedMps * approachRateMps / (2f * Mathf.Sqrt(p.maxAccelMps2 * p.comfortDecelMps2)));

        float gap = Mathf.Max(gapM, 0.01f);
        float interaction = sStar / gap;
        float acc = p.maxAccelMps2 * (freeTerm - interaction * interaction);

        return Mathf.Max(acc, -MaxBrakeMps2);
    }

    /// <summary>
    /// Integracion "balistica" recomendada por Treiber & Kanagaraj (2015) para el IDM:
    /// mas estable que Euler con pasos de tiempo finitos y nunca produce velocidad negativa.
    /// Devuelve el avance en metros y actualiza la velocidad.
    /// </summary>
    public static float IntegrateBallistic(ref float speedMps, float accMps2, float dt)
    {
        float newSpeed = speedMps + accMps2 * dt;
        float advance;

        if (newSpeed < 0f)
        {
            // Se detiene dentro del paso: avanza solo hasta v = 0
            advance = accMps2 < 0f ? -0.5f * speedMps * speedMps / accMps2 : 0f;
            newSpeed = 0f;
        }
        else
        {
            advance = speedMps * dt + 0.5f * accMps2 * dt * dt;
        }

        speedMps = newSpeed;
        return Mathf.Max(advance, 0f);
    }
}
