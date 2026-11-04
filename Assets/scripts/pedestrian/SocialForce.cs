// ============================================================
// SocialForce.cs
// Modelo de fuerza social para los peatones (Helbing & Molnar, 1995), con la
// repulsion "eliptica II" basada en la velocidad relativa (Johansson, Helbing
// & Shukla, 2007), que anticipa el movimiento del otro. La version circular
// original no evita que dos peatones que se encuentran de frente se
// superpongan a velocidad de marcha. Reglas puras, testeables en EditMode.
//
// Aceleracion del peaton i (por unidad de masa):
//
//   a_i = (v0_i e_i - v_i) / tau                  impulso hacia la velocidad deseada
//       + sum_j f_ij                               otros peatones (y ciclista)
//
//   f_ij = A exp((r_ij - b_ij)/B) (|d| + |d - y|)/(2 b_ij) * (d/|d| + (d - y)/|d - y|)/2 * w(phi)
//   d    = x_i - x_j          y = (v_j - v_i) dt_a      (dt_a: anticipacion)
//   b_ij = 0.5 sqrt((|d| + |d - y|)^2 - |y|^2)         semieje menor de la elipse
//   r_ij = r_i + r_j          w(phi) = lambda + (1 - lambda)(1 + cos phi)/2
//
// Ademas, una componente tangencial hacia la derecha del rumbo de i, kappa |f|
// max(0, cos phi), solo para lo que viene de frente: la preferencia por
// esquivar hacia la derecha evita los bloqueos simetricos de frente.
//
// Se integra con Euler semi-implicito; |v| <= 1.3 v0.
// Valores por defecto: tau = 0.5 s, A = 3 m/s2, B = 0.3 m, dt_a = 1 s,
// lambda = 0.3, kappa = 0.3. Elegidos por simulacion: en un encuentro de
// frente, un cruce perpendicular y un peaton detenido en el camino la
// distancia minima entre centros queda en 0.7-0.8 m (sin superposicion) y el
// peaton no se traba. [VERIFICAR] con una calibracion publicada.
// ============================================================

using UnityEngine;

public static class SocialForce
{
    public struct Params
    {
        public float tauS;           // tiempo de relajacion
        public float a;              // intensidad de la repulsion (m/s2)
        public float b;              // alcance de la repulsion (m)
        public float anticipationS;  // dt_a de la especificacion eliptica II
        public float lambda;         // anisotropia (0 = solo adelante, 1 = isotropica)
        public float kappa;          // preferencia por esquivar a la derecha
        public float maxSpeedFactor; // |v| <= factor * v0
        public float range;          // distancia maxima de interaccion (m)

        public static Params Default => new Params
        {
            tauS = 0.5f, a = 3f, b = 0.3f, anticipationS = 1f, lambda = 0.3f, kappa = 0.3f,
            maxSpeedFactor = 1.3f, range = 4f,
        };
    }

    /// <summary>Termino de impulso: lleva la velocidad hacia la deseada en tau segundos.</summary>
    public static Vector2 Driving(Vector2 velocity, Vector2 desiredDir, float desiredSpeed, float tau)
        => (desiredDir * desiredSpeed - velocity) / Mathf.Max(tau, 0.05f);

    /// <summary>Peso de anisotropia: 1 para lo que esta justo adelante, lambda para lo que esta justo atras.</summary>
    public static float Anisotropy(Vector2 heading, Vector2 toOther, float lambda)
    {
        if (heading.sqrMagnitude < 1e-6f || toOther.sqrMagnitude < 1e-6f) return 1f;
        float cos = Vector2.Dot(heading.normalized, toOther.normalized);
        return lambda + (1f - lambda) * (1f + cos) * 0.5f;
    }

    /// <summary>
    /// Fuerza que recibe i (posicion pos, velocidad vel, rumbo heading) de un agente j (otherPos, otherVel).
    /// radiusSum = r_i + r_j. Cero fuera del alcance.
    /// </summary>
    public static Vector2 Repulsion(Vector2 pos, Vector2 vel, Vector2 heading, Vector2 otherPos, Vector2 otherVel,
                                   float radiusSum, in Params p)
    {
        Vector2 d = pos - otherPos;
        float dn = d.magnitude;
        if (dn > p.range || dn < 1e-4f) return dn < 1e-4f ? Vector2.right * p.a : Vector2.zero;

        Vector2 y = (otherVel - vel) * p.anticipationS;
        Vector2 dy = d - y;
        float dyn = Mathf.Max(dy.magnitude, 1e-4f);
        // b acotado: con un encuentro exacto de frente la elipse se degenera (b -> 0)
        float b = Mathf.Max(0.5f * Mathf.Sqrt(Mathf.Max((dn + dyn) * (dn + dyn) - y.sqrMagnitude, 0f)), 0.05f);
        float g = p.a * Mathf.Exp((radiusSum - b) / Mathf.Max(p.b, 0.01f));
        // De frente y en linea, d/|d| y (d-y)/|d-y| pueden anularse: se usa la direccion directa
        Vector2 dir = d / dn + dy / dyn;
        if (dir.sqrMagnitude < 0.01f) dir = 2f * d / dn;
        Vector2 f = 0.5f * g * (dn + dyn) / (2f * b) * dir;

        Vector2 h = heading.sqrMagnitude > 1e-6f ? heading.normalized : Vector2.up;
        float cos = Vector2.Dot(h, -d / dn);
        f *= p.lambda + (1f - p.lambda) * (1f + cos) * 0.5f;
        Vector2 right = new Vector2(h.y, -h.x);
        return f + right * (p.kappa * f.magnitude * Mathf.Max(0f, cos));
    }

    /// <summary>Un paso de integracion: nueva velocidad (limitada) a partir de la aceleracion total.</summary>
    public static Vector2 Integrate(Vector2 velocity, Vector2 accel, float dt, float maxSpeed)
    {
        Vector2 v = velocity + accel * dt;
        float s = v.magnitude;
        return s > maxSpeed ? v * (maxSpeed / s) : v;
    }
}
