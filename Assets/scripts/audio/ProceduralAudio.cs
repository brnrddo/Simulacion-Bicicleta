// ============================================================
// ProceduralAudio.cs
// Sonidos generados por codigo (sin licencias de terceros), como AudioClip en
// bucle sin cortes. El ruido usa System.Random con semilla fija: siempre suena igual.
//   - Neumatico sobre asfalto: ruido filtrado pasabanda (~900 Hz) mas un rodado
//     grave. Se sube de tono y volumen con la velocidad del vehiculo.
//   - Bus electrico: zumbido del motor/inversor (tono con armonicos) y murmullo
//     grave de ventilacion o compresor.
// Bucle sin cortes: el filtro se pasa dos veces sobre el mismo ruido y se usa la
// segunda pasada (su estado inicial es el del final: la salida es periodica).
// ============================================================

using UnityEngine;

public static class ProceduralAudio
{
    /// <summary>Filtro bicuadratico (Robert Bristow-Johnson).</summary>
    private struct Biquad
    {
        private float b0, b1, b2, a1, a2, x1, x2, y1, y2;

        public static Biquad BandPass(float sr, float f, float q)
        {
            float w = 2f * Mathf.PI * f / sr, alpha = Mathf.Sin(w) / (2f * q), a0 = 1f + alpha;
            return new Biquad { b0 = alpha / a0, b1 = 0f, b2 = -alpha / a0, a1 = -2f * Mathf.Cos(w) / a0, a2 = (1f - alpha) / a0 };
        }

        public static Biquad LowPass(float sr, float f, float q)
        {
            float w = 2f * Mathf.PI * f / sr, alpha = Mathf.Sin(w) / (2f * q), c = Mathf.Cos(w), a0 = 1f + alpha;
            return new Biquad { b0 = (1f - c) * 0.5f / a0, b1 = (1f - c) / a0, b2 = (1f - c) * 0.5f / a0, a1 = -2f * c / a0, a2 = (1f - alpha) / a0 };
        }

        public float Process(float x)
        {
            float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
    }

    private static float[] Noise(int n, int seed)
    {
        var rng = new System.Random(seed);
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return w;
    }

    private static void Normalize(float[] d, float peak)
    {
        float m = 1e-6f;
        foreach (var v in d) m = Mathf.Max(m, Mathf.Abs(v));
        for (int i = 0; i < d.Length; i++) d[i] *= peak / m;
    }

    private static AudioClip Clip(string name, float[] data, int sr)
    {
        var clip = AudioClip.Create(name, data.Length, 1, sr, false);
        clip.SetData(data, 0);
        return clip;
    }

    /// <summary>Ruido de neumatico: pasabanda en 900 Hz (rodadura) + pasabajos en 180 Hz (retumbo), 4 s.</summary>
    public static AudioClip TireNoise(int sr, int seed)
    {
        int n = sr * 4;
        var w = Noise(n, seed);
        var bp = Biquad.BandPass(sr, 900f, 0.8f);
        var bp2 = Biquad.BandPass(sr, 2200f, 1.2f);
        var lp = Biquad.LowPass(sr, 180f, 0.7f);
        var outp = new float[n];
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
                outp[i] = bp.Process(w[i]) + 0.35f * bp2.Process(w[i]) + 0.9f * lp.Process(w[i]);
        Normalize(outp, 0.9f);
        return Clip("Neumatico_procedural", outp, sr);
    }

    /// <summary>
    /// Zumbido de motor electrico: fundamental de 300 Hz con armonicos (2x, 3x, 5x) y algo de ruido, 2 s.
    /// Las frecuencias completan ciclos enteros en el bucle, asi no hay salto al repetir.
    /// </summary>
    public static AudioClip ElectricWhine(int sr, int seed)
    {
        const float dur = 2f, f0 = 300f;
        int n = Mathf.RoundToInt(sr * dur);
        var w = Noise(n, seed);
        var bp = Biquad.BandPass(sr, 1500f, 2f);
        var outp = new float[n];
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                // Leve vibrato de 2 Hz (entero en 2 s): el inversor no es un tono perfecto
                float ph = 2f * Mathf.PI * f0 * t + 0.4f * Mathf.Sin(2f * Mathf.PI * 2f * t);
                outp[i] = Mathf.Sin(ph) + 0.5f * Mathf.Sin(2f * ph) + 0.3f * Mathf.Sin(3f * ph) + 0.15f * Mathf.Sin(5f * ph)
                        + 0.25f * bp.Process(w[i]);
            }
        Normalize(outp, 0.8f);
        return Clip("Bus_zumbido_procedural", outp, sr);
    }

    /// <summary>Murmullo de ventilacion o compresor: ruido grave (pasabajos 250 Hz) con un tono suave de 100 Hz, 3 s.</summary>
    public static AudioClip BusHum(int sr, int seed)
    {
        int n = sr * 3;
        var w = Noise(n, seed);
        var lp = Biquad.LowPass(sr, 250f, 0.7f);
        var outp = new float[n];
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
                outp[i] = lp.Process(w[i]) + 0.15f * Mathf.Sin(2f * Mathf.PI * 100f * i / sr);
        Normalize(outp, 0.8f);
        return Clip("Bus_murmullo_procedural", outp, sr);
    }
}
