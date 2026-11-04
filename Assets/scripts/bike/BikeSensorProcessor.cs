// ============================================================
// BikeSensorProcessor.cs
// ESTIMACION FISICA: pulsos Hall -> m/s, ADC -> grados.
// NO contiene modelo de vehiculo. La cinematica de la bici vive
// en BicycleController, para que el fallback de teclado pase por
// exactamente la misma dinamica y para poder cambiar el modelo
// entre condiciones sin tocar el codigo de sensores.
//
// DIAGNOSTICO: todo apagado por defecto. Los avisos que quedan
// activos son unicos o con cooldown -- ninguno puede inundar la
// consola por frame.
// ============================================================

using UnityEngine;

[DefaultExecutionOrder(-100)]  // debe correr ANTES que BicycleController
public class BikeSensorProcessor : MonoBehaviour, IBikeInputSource
{
    [Header("Referencia")]
    [SerializeField] private SerialBikeInput serialInput;

    // ------------------------------------------------------------
    [Header("Calibracion - Rueda / Hall")]
    [Tooltip("Circunferencia de la rueda en metros. Medir rodando una vuelta completa " +
             "con el peso del participante encima -- el radio bajo carga es menor que 2*pi*r nominal.")]
    [SerializeField] private float wheelCircumferenceM = 2.1f;

    [Tooltip("Cantidad de imanes N-S-N-S montados en la rueda.")]
    [SerializeField, Min(1)] private int magnetCount = 4;

    [Tooltip("Factor de correccion por iman. Los imanes pegados a mano rara vez quedan " +
             "equiespaciados; un error del 10% produce un rizo de velocidad de 4 ciclos " +
             "por vuelta. Usar el boton de calibracion, no editarlo a mano.")]
    [SerializeField] private float[] magnetSpacingScale = { 1f, 1f, 1f, 1f };

    // ------------------------------------------------------------
    [Header("Calibracion - Potenciometro (steering)")]
    [Tooltip("Tope izquierdo. Capturar con el boton del menu contextual.")]
    [SerializeField] private int potRawMin = 190;

    [Tooltip("Manubrio recto. Capturar con ZeroSteering antes de cada sesion.")]
    [SerializeField] private int potRawCenter = 450;

    [Tooltip("Tope derecho. Capturar con el boton del menu contextual.")]
    [SerializeField] private int potRawMax = 710;

    [Tooltip("Parametro de diseno, no medicion de hardware.")]
    [SerializeField] private float maxSteeringDeg = 45f;

    [Tooltip("Zona muerta en cuentas ADC alrededor del centro. Sin esto, una deriva " +
             "mecanica de 3-4 cuentas produce un giro constante minimo y el participante " +
             "se va del carril sin tocar el manubrio.")]
    [SerializeField, Range(0, 30)] private int potCenterDeadbandRaw = 4;

    [Tooltip("Suavizado del angulo de direccion (dither del ADC). Mantener bajo: " +
             "esto es acondicionamiento de senal, no comfort. El comfort se ajusta " +
             "en BicycleController.")]
    [SerializeField, Range(0f, 0.15f)] private float steeringSmoothTime = 0.02f;

    // ------------------------------------------------------------
    [Header("Rechazo de outliers (potenciometro)")]
    [Tooltip("Margen tolerado fuera de [potRawMin, potRawMax]. El manubrio no puede pasar " +
             "sus topes mecanicos, asi que una lectura fuera de rango es electricamente " +
             "imposible, no un giro extremo. Con min=190 y max=710, un margen de 15 " +
             "rechaza todo lo que caiga fuera de 175-725.")]
    [SerializeField, Range(0, 100)] private int potRangeMarginRaw = 15;

    [Tooltip("Salto maximo en cuentas entre muestras consecutivas (11 ms). Dejarlo laxo: " +
             "el filtro de rango hace el trabajo pesado y no tiene falsos positivos en " +
             "giros rapidos. Muy bajo recorta maniobras evasivas reales.")]
    [SerializeField, Range(5, 300)] private int maxPotJumpPerSample = 120;

    [Tooltip("Muestras consecutivas con salto grande tras las cuales se acepta el valor. " +
             "Sin esto, si el potenciometro salta y se QUEDA en el valor nuevo (montaje " +
             "corrido), el manubrio queda congelado el resto de la sesion.")]
    [SerializeField, Range(2, 30)] private int outlierAcceptAfterSamples = 8;

    // ------------------------------------------------------------
    [Header("Filtro Alpha-Beta (velocidad)")]
    [Tooltip("Ganancia de correccion de velocidad.")]
    [SerializeField, Range(0f, 1f)] private float alpha = 0.30f;

    [Tooltip("Ganancia de correccion de aceleracion. Regla practica para respuesta " +
             "criticamente amortiguada: beta = alpha^2 / (2 - alpha). Con alpha=0.30 -> 0.053.")]
    [SerializeField, Range(0f, 1f)] private float beta = 0.053f;

    [Tooltip("Techo de aceleracion estimada. Un ciclista urbano rara vez supera 2 m/s^2. " +
             "Sin este clamp, un pulso ruidoso genera una aceleracion enorme que despues " +
             "se integra durante todo el intervalo siguiente.")]
    [SerializeField] private float maxAccelMps2 = 4f;

    [Tooltip("La medicion por pulsos es la velocidad MEDIA del intervalo, valida en su punto " +
             "medio: llega con ~dt/2 de atraso. Activar extrapola al presente usando la " +
             "aceleracion estimada. Reduce lag pero puede sobreoscilar -- medir antes de " +
             "dejarlo activo en sesiones.")]
    [SerializeField] private bool compensateMeasurementLag = false;

    // ------------------------------------------------------------
    [Header("Frenada / parada")]
    [Tooltip("Deceleracion modelada cuando la rueda claramente se frena.")]
    [SerializeField] private float coastDownDecelMps2 = 2f;

    [Tooltip("Multiplo del ultimo intervalo real tras el cual se aplica la deceleracion " +
             "modelada. >1 evita dispararla en el jitter normal de pedaleo constante.")]
    [SerializeField, Range(1f, 3f)] private float coastDownIntervalFactor = 1.3f;

    [Tooltip("Tiempo sin pulso tras el cual se fuerza velocidad = 0.")]
    [SerializeField] private float stopTimeoutSec = 1.5f;

    // ------------------------------------------------------------
    [Header("Deteccion de conexion")]
    [SerializeField] private float dataStaleTimeoutSec = 0.5f;

    // ------------------------------------------------------------
    [Header("Diagnostico (dejar apagado en sesiones)")]
    [Tooltip("Imprime un resumen por segundo. Solo para debug de calibracion.")]
    [SerializeField] private bool showDebugReadout = false;

    [Tooltip("Avisa cuando se acumulan rechazos del potenciometro, con cooldown de 10 s. " +
             "Conviene dejarlo activo: un aumento de rechazos indica hardware degradandose.")]
    [SerializeField] private bool warnOnPotRejections = true;

    // ------------------------------------------------------------
    // IBikeInputSource
    // ------------------------------------------------------------
    public bool IsActive => IsReceivingData;
    public float SpeedMps => EstimatedSpeedMps;
    public float SteeringDeg { get; private set; }

    // --- Salidas de estimacion ---
    public float EstimatedSpeedMps { get; private set; }
    public float EstimatedAccelMps2 { get; private set; }

    // --- Crudos para el logger (Fase 5) ---
    // Loguear SIEMPRE el crudo ademas del filtrado: permite re-derivar la
    // velocidad offline con otros parametros sin repetir la sesion.
    public uint RawPulseCount { get; private set; }
    public uint RawPulseTimestampUs { get; private set; }
    public uint RawNowUs { get; private set; }
    public int RawPotValue { get; private set; }
    public float LastMeasuredSpeedMps { get; private set; }
    public float TimeSinceLastPulseSec { get; private set; }

    /// <summary>Contador acumulado de muestras de potenciometro descartadas. Loguear por trial.</summary>
    public int RejectedPotSamples { get; private set; }

    public bool IsReceivingData =>
        serialInput != null && serialInput.IsConnected &&
        (Time.time - lastDataReceivedTime) < dataStaleTimeoutSec;

    // ------------------------------------------------------------
    // Estado interno
    // ------------------------------------------------------------
    private float distancePerPulseM;

    private uint lastPulseCount;
    private uint lastPulseTimestampUs;
    private bool hasReceivedFirstPulse;
    private float lastPulseIntervalSec;
    private float lastDataReceivedTime = -999f;
    private int lastSeenReadSequence = -1;

    private float steeringVelocityRef;
    private int lastAcceptedPotRaw = -1;
    private int consecutiveOutliers;

    private float lastReadoutTime;
    private float lastRejectionWarnTime = -999f;
    private int rejectionsAtLastWarn;

    private double[] calibIntervalSum;
    private int[] calibIntervalCount;
    private int calibPulsesRemaining;
    public bool IsCalibratingMagnets => calibPulsesRemaining > 0;

    // ------------------------------------------------------------
    // Ciclo de vida
    // ------------------------------------------------------------

    private void Awake()
    {
        if (serialInput == null)
            Debug.LogError("[BikeSensorProcessor] serialInput SIN ASIGNAR. " +
                           "FixedUpdate sale en la primera linea y nada se mueve.", this);

        if (magnetSpacingScale == null || magnetSpacingScale.Length != magnetCount)
            ResizeMagnetArray();

        // Validacion de rango aca y no en OnValidate: OnValidate corre en cada
        // tecla, asi que reescribir un campo mientras se tipea lo vuelve ineditable.
        potRawMin = Mathf.Clamp(potRawMin, 0, 1021);
        potRawMax = Mathf.Clamp(potRawMax, potRawMin + 2, 1023);
        potRawCenter = Mathf.Clamp(potRawCenter, potRawMin + 1, potRawMax - 1);

        RecomputeDerived();
    }

    private void OnValidate() => RecomputeDerived();

    private void RecomputeDerived()
    {
        distancePerPulseM = wheelCircumferenceM / Mathf.Max(1, magnetCount);
    }

    private void FixedUpdate()
    {
        if (serialInput == null || !serialInput.IsConnected)
        {
            ResetEstimates();
            return;
        }

        // Comparar contra la copia propia del contador en vez de un HasNewData()
        // que consuma el flag: asi el logger puede leer lo mismo sin robar lecturas.
        int seq = serialInput.ReadSequence;
        if (seq != lastSeenReadSequence)
        {
            lastSeenReadSequence = seq;
            lastDataReceivedTime = Time.time;
        }

        SerialBikeInput.RawSensorData raw = serialInput.GetLatestData();
        if (!raw.isValid) return;

        RawPulseCount = raw.pulseCount;
        RawPulseTimestampUs = raw.pulseTimestampUs;
        RawNowUs = raw.nowUs;
        RawPotValue = raw.potRaw;   // crudo al log, sin filtrar

        UpdateSteering(ValidatePotReading(raw.potRaw));
        UpdateSpeedEstimate(raw);

        ReportDiagnostics();
    }

    // ------------------------------------------------------------
    // DIRECCION
    // ------------------------------------------------------------

    /// <summary>
    /// Dos capas de rechazo:
    ///  1. Rango fisico -- fuera de los topes mecanicos del manubrio es
    ///     electricamente imposible. Se rechaza siempre, sin importar
    ///     cuantas veces se repita.
    ///  2. Salto maximo -- filtra picos transitorios, pero cede si el valor
    ///     nuevo se sostiene: un rechazo permanente convierte un glitch en
    ///     un manubrio muerto.
    /// El orden importa. Si el salto se evaluara primero, ocho glitches
    /// seguidos a 1023 bastarian para que la capa 2 los aceptara como validos.
    /// </summary>
    private int ValidatePotReading(int potRaw)
    {
        int fallback = lastAcceptedPotRaw >= 0 ? lastAcceptedPotRaw : potRawCenter;

        // --- Capa 1: rango fisico ---
        if (potRaw < potRawMin - potRangeMarginRaw || potRaw > potRawMax + potRangeMarginRaw)
        {
            RejectedPotSamples++;
            consecutiveOutliers = 0;   // fuera de rango nunca es movimiento real
            return fallback;
        }

        if (lastAcceptedPotRaw < 0)
        {
            lastAcceptedPotRaw = potRaw;   // primera lectura de la sesion
            return potRaw;
        }

        // --- Capa 2: salto maximo por muestra ---
        if (Mathf.Abs(potRaw - lastAcceptedPotRaw) > maxPotJumpPerSample)
        {
            consecutiveOutliers++;
            RejectedPotSamples++;

            if (consecutiveOutliers < outlierAcceptAfterSamples)
                return lastAcceptedPotRaw;

            // Se sostuvo: movimiento real o montaje corrido. Resincronizar.
            Debug.LogWarning($"[BikeSensorProcessor] Salto sostenido de potenciometro: " +
                             $"{lastAcceptedPotRaw} -> {potRaw}. Aceptado. Revisar cableado.", this);
        }

        consecutiveOutliers = 0;
        lastAcceptedPotRaw = potRaw;
        return potRaw;
    }

    private void UpdateSteering(int potRaw)
    {
        float target = CalculateSteeringDeg(potRaw);

        SteeringDeg = steeringSmoothTime > 0f
            ? Mathf.SmoothDamp(SteeringDeg, target, ref steeringVelocityRef,
                               steeringSmoothTime, Mathf.Infinity, Time.fixedDeltaTime)
            : target;
    }

    private float CalculateSteeringDeg(int potRaw)
    {
        int delta = potRaw - potRawCenter;
        if (Mathf.Abs(delta) <= potCenterDeadbandRaw) return 0f;

        // Se resta el ancho de la zona muerta en vez de saltar de 0 al valor
        // completo: sin esto hay un escalon discreto al salir de la zona muerta.
        int corrected = potRawCenter + delta - (int)Mathf.Sign(delta) * potCenterDeadbandRaw;

        // Mapeo asimetrico de 3 puntos. InverseLerp clampea a 0..1, asi que un
        // valor de borde no extrapola a un angulo absurdo.
        float normalized = corrected <= potRawCenter
            ? Mathf.InverseLerp(potRawMin, potRawCenter, corrected) - 1f   // -1..0
            : Mathf.InverseLerp(potRawCenter, potRawMax, corrected);       //  0..1

        return normalized * maxSteeringDeg;
    }

    /// <summary>
    /// Fija el centro a la lectura actual. Llamar con el manubrio recto, una vez
    /// por sesion, antes del primer trial. La deriva mecanica entre sesiones es la
    /// causa mas comun de sesgo lateral sistematico en los datos de offset.
    /// </summary>
    [ContextMenu("1. Cero de direccion (manubrio recto)")]
    public void ZeroSteering() => CapturePot(ref potRawCenter, "centro");

    [ContextMenu("2. Capturar tope izquierdo")]
    public void CapturePotMin() => CapturePot(ref potRawMin, "minimo");

    [ContextMenu("3. Capturar tope derecho")]
    public void CapturePotMax() => CapturePot(ref potRawMax, "maximo");

    private void CapturePot(ref int target, string label)
    {
        if (serialInput == null || !serialInput.IsConnected)
        {
            Debug.LogWarning("[BikeSensorProcessor] Sin conexion serial: no se puede capturar.", this);
            return;
        }

        var raw = serialInput.GetLatestData();
        if (!raw.isValid) return;

        target = raw.potRaw;
        lastAcceptedPotRaw = -1;   // resincronizar el filtro tras recalibrar
        Debug.Log($"[BikeSensorProcessor] Tope {label} capturado: {target}", this);
    }

    // ------------------------------------------------------------
    // VELOCIDAD
    // ------------------------------------------------------------

    private void UpdateSpeedEstimate(SerialBikeInput.RawSensorData raw)
    {
        float dt = Time.fixedDeltaTime;

        // Tiempo sin pulso medido en el reloj del Arduino (estable) mas la edad
        // del paquete medida en Unity. Acumular solo con fixedDeltaTime
        // arrastraria el jitter del driver USB.
        if (hasReceivedFirstPulse)
        {
            float arduinoGapSec = (raw.nowUs - raw.pulseTimestampUs) / 1_000_000f;
            float packetAgeSec = Mathf.Max(0f, Time.time - lastDataReceivedTime);
            TimeSinceLastPulseSec = arduinoGapSec + packetAgeSec;
        }

        // --- Prediccion: todos los ticks, haya o no pulso nuevo ---
        EstimatedSpeedMps += EstimatedAccelMps2 * dt;

        // --- Correccion: solo si cambio el contador de pulsos ---
        if (raw.pulseCount != lastPulseCount)
        {
            if (hasReceivedFirstPulse)
            {
                // Resta unsigned: envuelve correctamente el overflow de micros().
                uint deltaPulses = raw.pulseCount - lastPulseCount;
                uint deltaTimeUs = raw.pulseTimestampUs - lastPulseTimestampUs;

                if (deltaTimeUs > 0 && deltaPulses > 0)
                {
                    float dtSec = deltaTimeUs / 1_000_000f;
                    float measured = DistanceForPulses(lastPulseCount, raw.pulseCount) / dtSec;

                    if (compensateMeasurementLag)
                        measured += EstimatedAccelMps2 * dtSec * 0.5f;

                    LastMeasuredSpeedMps = measured;

                    float residual = measured - EstimatedSpeedMps;
                    EstimatedSpeedMps += alpha * residual;
                    EstimatedAccelMps2 += (beta * residual) / dtSec;

                    lastPulseIntervalSec = dtSec / deltaPulses;

                    if (deltaPulses == 1) AccumulateMagnetCalibration(raw.pulseCount, dtSec);
                }
            }
            else
            {
                // Primer pulso: solo sincronizar referencia, sin corregir contra
                // un estado inicial arbitrario.
                hasReceivedFirstPulse = true;
            }

            lastPulseCount = raw.pulseCount;
            lastPulseTimestampUs = raw.pulseTimestampUs;
            TimeSinceLastPulseSec = 0f;
        }

        ApplyNoPulseConstraints(dt);

        EstimatedAccelMps2 = Mathf.Clamp(EstimatedAccelMps2, -maxAccelMps2, maxAccelMps2);

        if (EstimatedSpeedMps <= 0f)
        {
            // La bici no retrocede. Ademas hay que matar la aceleracion negativa:
            // dejarla hace que el proximo pulso arranque con un residual enorme
            // y la velocidad pegue un salto.
            EstimatedSpeedMps = 0f;
            EstimatedAccelMps2 = Mathf.Max(0f, EstimatedAccelMps2);
        }
    }

    /// <summary>
    /// Cota fisica: si no llego un pulso en T segundos, la rueda recorrio menos
    /// de una distancia-por-pulso en T, asi que la velocidad media es como maximo
    /// d/T. Da un decaimiento hiperbolico natural, y en T = ultimo intervalo la
    /// cota coincide con la ultima velocidad medida, asi que no interfiere con el
    /// pedaleo constante.
    /// </summary>
    private void ApplyNoPulseConstraints(float dt)
    {
        if (!hasReceivedFirstPulse) return;
        if (TimeSinceLastPulseSec <= lastPulseIntervalSec) return;

        float upperBound = distancePerPulseM / Mathf.Max(TimeSinceLastPulseSec, 1e-4f);
        if (EstimatedSpeedMps > upperBound)
        {
            EstimatedSpeedMps = upperBound;
            EstimatedAccelMps2 = Mathf.Min(EstimatedAccelMps2, 0f);
        }

        if (TimeSinceLastPulseSec > lastPulseIntervalSec * coastDownIntervalFactor)
        {
            EstimatedAccelMps2 = Mathf.Min(EstimatedAccelMps2, 0f);
            EstimatedSpeedMps = Mathf.MoveTowards(EstimatedSpeedMps, 0f, coastDownDecelMps2 * dt);
        }

        if (TimeSinceLastPulseSec > stopTimeoutSec)
        {
            EstimatedSpeedMps = 0f;
            EstimatedAccelMps2 = 0f;
        }
    }

    private float DistanceForPulses(uint fromExclusive, uint toInclusive)
    {
        uint delta = toInclusive - fromExclusive;

        // Salto grande (hitch de Unity): usar el promedio, recorrer el array
        // no aporta nada y el costo crece.
        if (delta > 64 || magnetCount <= 1) return delta * distancePerPulseM;

        float total = 0f;
        for (uint p = fromExclusive + 1; p <= toInclusive; p++)
            total += distancePerPulseM * magnetSpacingScale[(int)(p % (uint)magnetCount)];

        return total;
    }

    private void ResetEstimates()
    {
        // Sin conexion la estimacion NO debe quedar congelada en el ultimo valor:
        // eso deja la bici deslizandose a velocidad constante.
        EstimatedSpeedMps = 0f;
        EstimatedAccelMps2 = 0f;
        hasReceivedFirstPulse = false;
    }

    // ------------------------------------------------------------
    // DIAGNOSTICO
    // ------------------------------------------------------------

    private void ReportDiagnostics()
    {
        if (showDebugReadout && Time.time - lastReadoutTime >= 1f)
        {
            lastReadoutTime = Time.time;
            Debug.Log($"[Processor] pulsos={RawPulseCount} pot={RawPotValue} " +
                      $"v={EstimatedSpeedMps:F2} m/s steer={SteeringDeg:F1} " +
                      $"sinPulso={TimeSinceLastPulseSec:F2}s rechazos={RejectedPotSamples}");
        }

        // Cooldown de 10 s y solo si hubo rechazos nuevos: no puede inundar la
        // consola aunque el potenciometro falle continuamente.
        if (warnOnPotRejections &&
            RejectedPotSamples > rejectionsAtLastWarn &&
            Time.time - lastRejectionWarnTime >= 10f)
        {
            int nuevos = RejectedPotSamples - rejectionsAtLastWarn;
            lastRejectionWarnTime = Time.time;
            rejectionsAtLastWarn = RejectedPotSamples;
            Debug.LogWarning($"[BikeSensorProcessor] {nuevos} muestras de potenciometro " +
                             $"descartadas en los ultimos 10 s (total {RejectedPotSamples}).", this);
        }
    }

    // ------------------------------------------------------------
    // CALIBRACION DE ESPACIADO DE IMANES
    // ------------------------------------------------------------

    /// <summary>
    /// Arrancar, pedalear a velocidad lo mas constante posible durante ~10 vueltas
    /// y esperar. Compara el intervalo medio de cada iman contra el promedio y
    /// deriva un factor de arco por iman.
    /// </summary>
    [ContextMenu("4. Calibrar espaciado de imanes (10 vueltas)")]
    public void BeginMagnetCalibration() => BeginMagnetCalibration(10);

    public void BeginMagnetCalibration(int revolutions)
    {
        calibIntervalSum = new double[magnetCount];
        calibIntervalCount = new int[magnetCount];
        calibPulsesRemaining = revolutions * magnetCount;
        Debug.Log($"[BikeSensorProcessor] Calibrando imanes: pedalear parejo, " +
                  $"{calibPulsesRemaining} pulsos.", this);
    }

    [ContextMenu("Reiniciar array de calibracion de imanes")]
    private void ResizeMagnetArray()
    {
        // No va en OnValidate: tipear "40" en magnetCount pasa por el valor
        // intermedio 4, y redimensionar en cada tecla borraria una calibracion hecha.
        var resized = new float[Mathf.Max(1, magnetCount)];
        for (int i = 0; i < resized.Length; i++)
            resized[i] = (magnetSpacingScale != null && i < magnetSpacingScale.Length)
                ? magnetSpacingScale[i] : 1f;
        magnetSpacingScale = resized;
    }

    private void AccumulateMagnetCalibration(uint pulseIndex, float intervalSec)
    {
        if (calibPulsesRemaining <= 0) return;

        int idx = (int)(pulseIndex % (uint)magnetCount);
        calibIntervalSum[idx] += intervalSec;
        calibIntervalCount[idx]++;
        calibPulsesRemaining--;

        if (calibPulsesRemaining == 0) FinishMagnetCalibration();
    }

    private void FinishMagnetCalibration()
    {
        var means = new double[magnetCount];
        double grandMean = 0d;

        for (int i = 0; i < magnetCount; i++)
        {
            if (calibIntervalCount[i] == 0)
            {
                Debug.LogWarning("[BikeSensorProcessor] Calibracion incompleta.", this);
                return;
            }
            means[i] = calibIntervalSum[i] / calibIntervalCount[i];
            grandMean += means[i];
        }
        grandMean /= magnetCount;

        // A velocidad constante el intervalo es proporcional al arco recorrido,
        // asi que el cociente contra la media es directamente el factor de arco.
        for (int i = 0; i < magnetCount; i++)
            magnetSpacingScale[i] = (float)(means[i] / grandMean);

        Debug.Log("[BikeSensorProcessor] Espaciado calibrado: " +
                  string.Join(", ", System.Array.ConvertAll(magnetSpacingScale, f => f.ToString("F3"))), this);
    }
}