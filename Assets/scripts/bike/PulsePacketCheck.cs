// ============================================================
// PulsePacketCheck.cs
// Decide si un paquete del Arduino es fisicamente posible respecto del
// ultimo paquete aceptado. Existe porque, si el Arduino se reinicia (al
// abrir el puerto, por un corte de USB), su reloj y su contador de pulsos
// vuelven a 0: el contador "retrocede" y la resta sin signo da ~4 mil
// millones de pulsos. En tres sesiones del 2026-11-12 y 2026-11-13 eso
// produjo velocidades de mas de 200 000 m/s durante un tick y la bici quedo
// a mas de 1 km del mapa (dos veces a los 2.2 s de iniciar, que es lo que
// tarda el Arduino en arrancar tras abrirse el puerto, y una vez a mitad de
// sesion).
// Tambien cubre una linea serial cortada ("34,..." en vez de "1234,..."),
// que sigue teniendo 4 numeros validos porque el formato no tiene checksum.
// Toda la aritmetica es sin signo, asi que el desborde de micros()
// (cada ~71 min) se maneja solo.
// ============================================================

public static class PulsePacketCheck
{
    private const uint HalfRange = 0x80000000u;

    /// <param name="prevCount">Contador de pulsos del ultimo paquete aceptado.</param>
    /// <param name="prevPulseTsUs">micros() del ultimo pulso en el ultimo paquete aceptado.</param>
    /// <param name="prevNowUs">micros() del ultimo paquete aceptado.</param>
    /// <param name="maxGapSec">Tiempo maximo creible entre dos paquetes seguidos.</param>
    public static bool IsPlausible(uint prevCount, uint prevPulseTsUs, uint prevNowUs,
                                   uint count, uint pulseTsUs, uint nowUs,
                                   float distancePerPulseM, float maxSpeedMps, float maxGapSec = 2f)
    {
        uint elapsedUs = nowUs - prevNowUs;

        // Mismo paquete leido dos veces (Unity fue mas rapido que el serial): nada puede haber cambiado
        if (elapsedUs == 0) return count == prevCount && pulseTsUs == prevPulseTsUs;

        // El reloj del Arduino retrocedio (reinicio o linea corrupta) o salto demasiado
        if (elapsedUs > (uint)(maxGapSec * 1_000_000f)) return false;

        // El ultimo pulso no puede estar en el futuro
        if (nowUs - pulseTsUs >= HalfRange) return false;

        uint newPulses = count - prevCount;
        if (newPulses == 0) return pulseTsUs == prevPulseTsUs;

        // El contador retrocedio (la resta sin signo da un numero enorme) o subio mas de lo posible
        float elapsedSec = elapsedUs / 1_000_000f;
        float maxPulses = maxSpeedMps * elapsedSec / distancePerPulseM + 2f;
        if (newPulses > maxPulses) return false;

        uint pulseSpanUs = pulseTsUs - prevPulseTsUs;
        if (pulseSpanUs == 0 || pulseSpanUs >= HalfRange) return false;

        float measuredMps = newPulses * distancePerPulseM / (pulseSpanUs / 1_000_000f);
        return measuredMps <= maxSpeedMps;
    }
}
