// ============================================================
// BicycleController.cs
// MODELO DE VEHICULO: consume una IBikeInputSource (velocidad +
// angulo de manubrio en unidades fisicas) y mueve el Rigidbody.
// No sabe nada de puertos serie, pulsos ni cuentas de ADC.
// ============================================================

using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[DefaultExecutionOrder(0)]  // despues de las fuentes de input (-100)
public class BicycleController : MonoBehaviour
{
    [Header("Fuentes de input (por prioridad)")]
    [SerializeField] private BikeSensorProcessor sensorProcessor;

    [Tooltip("Solo se usa si la bici real no esta entregando datos Y " +
             "allowKeyboardFallback esta activo.")]
    [SerializeField] private KeyboardBikeInput keyboardInput;

    [Tooltip("APAGAR en sesiones con participantes. Con esto activo, una caida del USB " +
             "a mitad de trial hace que el sistema pase a teclado en silencio: el " +
             "participante ve la bici frenar y el log queda con datos que parecen validos.")]
    [SerializeField] private bool allowKeyboardFallback = true;

    [Header("Geometria de la bici")]
    [Tooltip("Distancia entre eje trasero y delantero, en metros.")]
    [SerializeField] private float wheelbase = 1.0f;

    [Tooltip("Distancia del eje trasero al punto de referencia del ciclista (cadera/manubrio). " +
             "El modelo cinematico integra la posicion del EJE TRASERO. Si el pivot del " +
             "GameObject no coincide con el eje trasero, la posicion que loguees para el " +
             "offset lateral tiene un error de hasta este valor durante los giros.")]
    [SerializeField] private float rearAxleToRiderM = 0.55f;

    [Header("Limites de direccion")]
    [SerializeField, Range(5f, 80f)] private float maxSteeringAngleDeg = 45f;

    [Tooltip("Inclinacion maxima que un ciclista real sostendria en un giro. " +
             "Reemplaza el techo arbitrario de velocidad angular por uno derivado de fisica: " +
             "tan(phi) = v*omega/g  ->  omega_max = g*tan(phi)/v. " +
             "Con 40 grados: 94 deg/s a 5 m/s, 47 deg/s a 10 m/s.")]
    [SerializeField, Range(10f, 50f)] private float maxLeanAngleDeg = 40f;

    [Tooltip("Techo adicional por comfort VR, encima del limite fisico. La rotacion de yaw " +
             "es el principal disparador de malestar. Reportar este valor en el paper.")]
    [SerializeField] private float comfortMaxYawRateDegPerSec = 90f;

    [Tooltip("Por debajo de esta velocidad se habilita el rango completo del manubrio " +
             "(evita dividir por valores cercanos a cero).")]
    [SerializeField] private float lowSpeedThresholdMps = 0.5f;

    [Header("Suavizado de la dinamica")]
    [Tooltip("Suavizado del angulo de direccion USADO POR LA DINAMICA. " +
             "No afecta al manubrio visual, que copia el fisico.")]
    [SerializeField, Range(0f, 0.3f)] private float steeringSmoothTime = 0.05f;

    [Tooltip("Suavizado extra de velocidad. Dejar en 0 con bici real: BikeSensorProcessor " +
             "ya entrega velocidad filtrada y volver a suavizar reintroduce el mismo lag " +
             "que el filtro alpha-beta existe para eliminar.")]
    [SerializeField, Range(0f, 0.3f)] private float speedSmoothTime = 0f;

    [Header("Seguimiento de terreno")]
    [Tooltip("Debe apuntar SOLO a la layer del suelo, nunca a Everything: " +
             "con Everything la bici hace raycast contra sus propios colliders.")]
    [SerializeField] private LayerMask groundLayerMask = 0;
    [SerializeField] private float groundRayOriginHeight = 2.0f;
    [SerializeField] private float groundRayMaxDistance = 6f;
    [SerializeField] private float groundOffset = 0f;

    [Tooltip("Suavizado de la altura. Un snap duro se siente como un salto vertical " +
             "en VR, mucho mas mareante que un desnivel percibido gradualmente.")]
    [SerializeField] private float groundFollowSmoothTime = 0.1f;

    // ------------------------------------------------------------
    // Estado publico -- consumido por BicycleAnimator y por el logger
    // ------------------------------------------------------------

    /// <summary>Velocidad usada por la dinamica, m/s.</summary>
    public float CurrentSpeedMps { get; private set; }

    /// <summary>Angulo de direccion efectivo tras limitar y suavizar, grados.</summary>
    public float EffectiveSteeringDeg { get; private set; }

    /// <summary>Angulo fisico del manubrio, sin limitar. Es lo que el animator debe mostrar.</summary>
    public float PhysicalSteeringDeg { get; private set; }

    /// <summary>Velocidad de guinada aplicada este tick, grados/s.</summary>
    public float YawRateDegPerSec { get; private set; }

    /// <summary>Inclinacion implicada por el giro, grados (positivo = derecha). Solo visual.</summary>
    public float LeanAngleDeg { get; private set; }

    /// <summary>Normal del suelo bajo la bici. Util para inclinar el modelo en pendientes.</summary>
    public Vector3 GroundNormal { get; private set; } = Vector3.up;

    /// <summary>Posicion del ciclista, no del pivot. Es lo que hay que loguear para offset lateral.</summary>
    public Vector3 RiderPosition => transform.position + transform.forward * rearAxleToRiderM;

    /// <summary>Fuente activa este frame. Loguear su cambio como evento.</summary>
    public string ActiveSourceName { get; private set; } = "none";

    public bool UsingRealBike { get; private set; }

    /// <summary>True mientras la bici esta congelada (p. ej. durante el fade de un reinicio).</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    /// Velocidad que entrega la fuente de input AHORA (rueda real o teclado), aunque la
    /// bici este congelada. Sirve para saber si el participante sigue pedaleando o si la
    /// rueda del rodillo sigue girando por inercia.
    /// </summary>
    public float InputSpeedMps => activeSource != null ? activeSource.SpeedMps : 0f;

    private Rigidbody rb;
    private IBikeInputSource activeSource;
    private float headingDeg;
    private float smoothedSpeed, speedVelocityRef;
    private float steeringVelocityRef;
    private float groundYVelocityRef;
    private bool wasUsingRealBike;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        headingDeg = transform.eulerAngles.y;

        if (groundLayerMask == 0)
            Debug.LogWarning("[BicycleController] groundLayerMask sin asignar: " +
                             "el seguimiento de terreno esta desactivado.");
    }

    private void FixedUpdate()
    {
        // La seleccion de fuente corre siempre, incluso congelada: una perdida
        // de senal durante un reinicio tambien tiene que quedar registrada.
        SelectInputSource();

        if (IsFrozen)
        {
            ClearDynamicsState();
            return;
        }

        UpdateDynamicsInputs();
        MoveBicycle(Time.fixedDeltaTime);
    }

    // ------------------------------------------------------------
    // API de control externo (reinicio tras choque, inicio de ensayo)
    // ------------------------------------------------------------

    /// <summary>
    /// Congela la bici: no se mueve aunque el participante pedalee.
    /// La velocidad reportada queda en 0 para que el log refleje lo que ve el participante.
    /// </summary>
    public void SetFrozen(bool frozen)
    {
        IsFrozen = frozen;
        if (frozen) ClearDynamicsState();
    }

    /// <summary>
    /// Teletransporta la bici para que el PUNTO DEL CICLISTA (RiderPosition, no el
    /// pivot del eje trasero) quede en riderPosition, mirando hacia yawDeg.
    /// Llamar solo con la pantalla en negro.
    /// </summary>
    public void TeleportRider(Vector3 riderPosition, float yawDeg)
    {
        headingDeg = Mathf.Repeat(yawDeg, 360f);
        Quaternion rotation = Quaternion.Euler(0f, headingDeg, 0f);

        // El modelo integra el eje trasero: retroceder desde el punto del ciclista
        Vector3 pivot = riderPosition - (rotation * Vector3.forward) * rearAxleToRiderM;

        // Ajustar altura al suelo real para no aparecer flotando o enterrado
        if (groundLayerMask != 0)
        {
            Vector3 rayOrigin = pivot + Vector3.up * groundRayOriginHeight;
            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit,
                                groundRayMaxDistance, groundLayerMask, QueryTriggerInteraction.Ignore))
                pivot.y = hit.point.y + groundOffset;
        }

        // Mover Rigidbody Y Transform a la vez: si solo se mueve el Rigidbody,
        // la interpolacion dibuja un frame "barrido" entre la posicion vieja y la nueva.
        rb.position = pivot;
        rb.rotation = rotation;
        transform.SetPositionAndRotation(pivot, rotation);

        ClearDynamicsState();
        GroundNormal = Vector3.up;
    }

    private void ClearDynamicsState()
    {
        smoothedSpeed = 0f;
        speedVelocityRef = 0f;
        steeringVelocityRef = 0f;
        groundYVelocityRef = 0f;
        CurrentSpeedMps = 0f;
        EffectiveSteeringDeg = 0f;
        YawRateDegPerSec = 0f;
        LeanAngleDeg = 0f;
    }

    /// <summary>
    /// La condicion para usar la bici real es "esta llegando data por el serial",
    /// nunca "velocidad > 0": gatear por velocidad congela el manubrio apenas el
    /// participante frena en un semaforo en rojo.
    /// </summary>
    private void SelectInputSource()
    {
        bool sensorOk = sensorProcessor != null && sensorProcessor.IsActive;

        if (sensorOk)
        {
            activeSource = sensorProcessor;
            ActiveSourceName = "sensors";
        }
        else if (allowKeyboardFallback && keyboardInput != null && keyboardInput.IsActive)
        {
            activeSource = keyboardInput;
            ActiveSourceName = "keyboard";
        }
        else
        {
            activeSource = null;
            ActiveSourceName = "none";
        }

        UsingRealBike = sensorOk;

        if (wasUsingRealBike && !sensorOk)
            Debug.LogError($"[BicycleController] PERDIDA DE SENAL DE LA BICI. " +
                           $"Fuente activa: {ActiveSourceName}. Marcar este trial en el log.");
        wasUsingRealBike = sensorOk;
    }

    private void UpdateDynamicsInputs()
    {
        float dt = Time.fixedDeltaTime;

        float sourceSpeed = activeSource?.SpeedMps ?? 0f;
        PhysicalSteeringDeg = activeSource?.SteeringDeg ?? 0f;

        smoothedSpeed = speedSmoothTime > 0f
            ? Mathf.SmoothDamp(smoothedSpeed, sourceSpeed, ref speedVelocityRef,
                               speedSmoothTime, Mathf.Infinity, dt)
            : sourceSpeed;

        CurrentSpeedMps = smoothedSpeed;

        float maxSteerNow = ComputeDynamicSteeringLimitDeg(smoothedSpeed);
        float clampedTarget = Mathf.Clamp(PhysicalSteeringDeg, -maxSteerNow, maxSteerNow);

        EffectiveSteeringDeg = steeringSmoothTime > 0f
            ? Mathf.SmoothDamp(EffectiveSteeringDeg, clampedTarget, ref steeringVelocityRef,
                               steeringSmoothTime, Mathf.Infinity, dt)
            : clampedTarget;
    }

    /// <summary>
    /// Limite de direccion dependiente de velocidad, derivado de fisica en vez
    /// de un numero elegido a mano. Una bici real no puede girar mas rapido de
    /// lo que su inclinacion permite: tan(phi) = v*omega/g. Invirtiendo para un
    /// phi maximo se obtiene omega_max = g*tan(phi_max)/v, y de ahi el angulo
    /// de manubrio correspondiente via el modelo cinematico.
    /// El techo de comfort VR se aplica encima como segundo limite.
    /// </summary>
    private float ComputeDynamicSteeringLimitDeg(float speed)
    {
        if (speed < lowSpeedThresholdMps) return maxSteeringAngleDeg;

        float g = Mathf.Abs(Physics.gravity.y);
        float omegaPhysical = g * Mathf.Tan(maxLeanAngleDeg * Mathf.Deg2Rad) / speed;   // rad/s
        float omegaComfort = comfortMaxYawRateDegPerSec * Mathf.Deg2Rad;                // rad/s
        float omegaMax = Mathf.Min(omegaPhysical, omegaComfort);

        float deltaMaxRad = Mathf.Atan(omegaMax * wheelbase / speed);
        return Mathf.Min(maxSteeringAngleDeg, deltaMaxRad * Mathf.Rad2Deg);
    }

    private void MoveBicycle(float dt)
    {
        // Modelo cinematico de bicicleta, referencia = eje trasero.
        // Sin clamp redundante sobre omega: ComputeDynamicSteeringLimitDeg ya
        // garantiza |omega| <= omega_max por construccion. Tener los dos limites
        // hacia imposible saber cual estaba actuando al ajustar el tuning.
        float steeringRad = EffectiveSteeringDeg * Mathf.Deg2Rad;
        float omegaRad = (smoothedSpeed * Mathf.Tan(steeringRad)) / Mathf.Max(wheelbase, 0.01f);

        YawRateDegPerSec = omegaRad * Mathf.Rad2Deg;

        // Inclinacion implicada por el giro. Solo para el modelo visual:
        // aplicarla a la camara agrega rotacion de roll sin contraparte
        // vestibular, que es de los estimulos mas nauseabundos en VR.
        LeanAngleDeg = Mathf.Atan2(smoothedSpeed * omegaRad, Mathf.Abs(Physics.gravity.y)) * Mathf.Rad2Deg;

        headingDeg = Mathf.Repeat(headingDeg + YawRateDegPerSec * dt, 360f);
        Quaternion targetRotation = Quaternion.Euler(0f, headingDeg, 0f);

        Vector3 targetPosition = rb.position + (targetRotation * Vector3.forward) * smoothedSpeed * dt;

        if (groundLayerMask != 0)
        {
            Vector3 rayOrigin = targetPosition + Vector3.up * groundRayOriginHeight;
            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit,
                                groundRayMaxDistance, groundLayerMask, QueryTriggerInteraction.Ignore))
            {
                GroundNormal = hit.normal;
                targetPosition.y = Mathf.SmoothDamp(rb.position.y, hit.point.y + groundOffset,
                                                    ref groundYVelocityRef, groundFollowSmoothTime,
                                                    Mathf.Infinity, dt);
            }
            else
            {
                // Sin suelo detectado: mantener la altura actual en vez de caer.
                // Si esto se dispara seguido, revisar que la geometria de la ciudad
                // este en la layer correcta -- un hueco produce un salto vertical.
                GroundNormal = Vector3.up;
                targetPosition.y = rb.position.y;
            }
        }
        else
        {
            targetPosition.y = rb.position.y;
        }

        rb.MoveRotation(targetRotation);
        rb.MovePosition(targetPosition);
    }

    private void OnDrawGizmosSelected()
    {
        // Visualiza eje trasero (pivot) vs punto de referencia del ciclista.
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, 0.08f);
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position + transform.forward * rearAxleToRiderM, 0.08f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * wheelbase);
    }
}
