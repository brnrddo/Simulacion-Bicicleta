// KeyboardBikeInput.cs
// Fuente de input alternativa para desarrollo y pilotos sin hardware.
// Se separo de BicycleController para que la bici real y el teclado
// pasen por EXACTAMENTE el mismo modelo cinematico -- antes el camino
// de teclado tenia su propio suavizado y por lo tanto su propio
// comportamiento, asi que probar sin bici no predecia la sesion real.

using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-100)]
public class KeyboardBikeInput : MonoBehaviour, IBikeInputSource
{
    [Header("Activacion")]
    [Tooltip("APAGAR durante sesiones con participantes. Si el USB se cae a mitad de " +
             "un trial y el fallback esta activo, el sistema empieza a aceptar teclado " +
             "sin avisar y los datos quedan corruptos de forma silenciosa.")]
    [SerializeField] private bool enableKeyboardInput = true;

    [Header("Parametros")]
    [SerializeField] private float maxSpeedMps = 6f;
    [SerializeField] private float accelMps2 = 3f;
    [SerializeField] private float brakeMps2 = 5f;
    [SerializeField] private float steeringRateDegPerSec = 90f;
    [SerializeField] private float steeringReturnDegPerSec = 120f;
    [SerializeField] private float maxSteeringDeg = 45f;

    public bool IsActive => enableKeyboardInput && Keyboard.current != null;
    public float SpeedMps { get; private set; }
    public float SteeringDeg { get; private set; }

    private void FixedUpdate()
    {
        if (!IsActive) return;

        var kb = Keyboard.current;
        float dt = Time.fixedDeltaTime;

        bool throttle = kb.wKey.isPressed || kb.upArrowKey.isPressed;
        bool brake = kb.sKey.isPressed || kb.downArrowKey.isPressed;

        if (brake) SpeedMps = Mathf.MoveTowards(SpeedMps, 0f, brakeMps2 * dt);
        else if (throttle) SpeedMps = Mathf.MoveTowards(SpeedMps, maxSpeedMps, accelMps2 * dt);
        else SpeedMps = Mathf.MoveTowards(SpeedMps, 0f, 0.5f * dt);  // rolling resistance

        float steerInput = 0f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steerInput -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steerInput += 1f;

        // Retorno al centro al soltar: un potenciometro real montado en el
        // manubrio se queda donde lo dejaste, pero sin retorno el teclado se
        // vuelve inmanejable para probar trayectorias.
        SteeringDeg = Mathf.Approximately(steerInput, 0f)
            ? Mathf.MoveTowards(SteeringDeg, 0f, steeringReturnDegPerSec * dt)
            : Mathf.MoveTowards(SteeringDeg, steerInput * maxSteeringDeg, steeringRateDegPerSec * dt);
    }
}
