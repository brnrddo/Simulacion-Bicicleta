// ============================================================
// BicycleAnimator.cs
// Solo presentacion visual. Lee de DOS fuentes distintas a proposito:
//
//   Manubrio -> angulo FISICO (sin limitar por velocidad).
//     El participante tiene el manubrio real en las manos. Si gira 40
//     grados fisicos y el modelo virtual muestra 12 (el valor que la
//     dinamica realmente usa a esa velocidad), hay conflicto entre
//     propiocepcion y vision, que es una de las causas mas confiables
//     de malestar en VR. La propiocepcion gana.
//
//   Ruedas y lean -> estado del controller.
//     La rotacion de las ruedas tiene que coincidir con el
//     desplazamiento real sobre el suelo o se ve patinar.
//
// REGLA CLAVE DE ESTE SCRIPT: nunca asignar localRotation directo.
// Los modelos importados traen rotaciones horneadas; sobrescribirlas
// hace que los ejes locales dejen de coincidir con los del vehiculo y
// una rotacion en Y se convierte en una mezcla de yaw y pitch.
// Siempre se compone contra la rotacion base capturada en Start().
// ============================================================

using UnityEngine;

[DefaultExecutionOrder(100)]  // despues del controller
public class BicycleAnimator : MonoBehaviour
{
    public enum SpinAxis { X, Y, Z }

    [Header("Fuente de datos")]
    [SerializeField] private BicycleController bikeController;

    [Header("Partes del modelo BMXBikeE")]
    [Tooltip("Horquilla/manubrio. La rueda delantera deberia ser hija de este " +
             "transform para heredar el giro de direccion.")]
    [SerializeField] private Transform handlebarTransform;
    [SerializeField] private Transform frontWheelTransform;
    [SerializeField] private Transform rearWheelTransform;

    [Tooltip("Raiz visual de la bici (el modelo BMXBikeE), NO el XR Origin ni playerBike. " +
             "Aca se aplica inclinacion de giro y de pendiente. Dejar vacio desactiva ambas.")]
    [SerializeField] private Transform visualRoot;

    [Header("Direccion visual")]
    [Tooltip("Invertir si el manubrio virtual gira al reves que el fisico.")]
    [SerializeField] private bool invertHandlebar = false;

    [Tooltip("Suavizado minimo contra el escalonado a 90 Hz cuando el render corre mas " +
             "rapido que physics. Mantener muy bajo: cualquier valor grande reintroduce " +
             "desfase entre las manos y lo que se ve.")]
    [SerializeField, Range(0f, 0.06f)] private float handlebarSmoothTime = 0.015f;

    [Header("Ruedas")]
    [Tooltip("Radio en metros, coherente con wheelCircumferenceM del processor: " +
             "r = circunferencia / (2*pi). Con 2.1 m -> 0.334 m.")]
    [SerializeField] private float wheelRadiusM = 0.334f;

    [Tooltip("Eje del EJE DE LA RUEDA en el espacio local del transform de la rueda. " +
             "Si la rueda gira en yaw en vez de rodar, cambiar de eje. " +
             "Activar showAxisGizmos para verlo dibujado en la escena.")]
    [SerializeField] private SpinAxis wheelSpinAxis = SpinAxis.X;
    [SerializeField] private bool invertWheelSpin = false;

    [Tooltip("Dibuja el eje de giro elegido en la Scene view (linea roja). " +
             "Tiene que quedar horizontal y perpendicular a la direccion de marcha.")]
    [SerializeField] private bool showAxisGizmos = true;

    [Header("Inclinacion (solo modelo, nunca camara)")]
    [SerializeField] private bool applyLean = true;

    [Tooltip("Fraccion de la inclinacion fisica que se muestra. Reportar este valor: " +
             "cambia el flujo optico y puede afectar la percepcion de velocidad.")]
    [SerializeField, Range(0f, 1f)] private float leanVisualGain = 1f;
    [SerializeField] private bool invertLean = false;
    [SerializeField] private float leanSmoothTime = 0.15f;

    [Header("Pendiente")]
    [Tooltip("Alinea el modelo con la normal del suelo. Solo visual: el XR Origin " +
             "mantiene el horizonte estable, que es lo que evita el mareo.")]
    [SerializeField] private bool alignToGroundSlope = true;
    [SerializeField] private float slopeSmoothTime = 0.2f;

    // Rotaciones base capturadas del modelo importado
    private Quaternion handlebarBaseRot = Quaternion.identity;
    private Quaternion frontWheelBaseRot = Quaternion.identity;
    private Quaternion rearWheelBaseRot = Quaternion.identity;
    private Quaternion visualRootBaseRot = Quaternion.identity;

    private float displayedHandlebarDeg, handlebarVelRef;
    private float displayedLeanDeg, leanVelRef;
    private float wheelSpinDeg;
    private Vector3 smoothedGroundNormal = Vector3.up;

    private void Start()
    {
        if (bikeController == null)
        {
            Debug.LogError("[BicycleAnimator] Falta asignar BicycleController en el Inspector.");
            enabled = false;
            return;
        }

        if (handlebarTransform != null) handlebarBaseRot = handlebarTransform.localRotation;
        if (frontWheelTransform != null) frontWheelBaseRot = frontWheelTransform.localRotation;
        if (rearWheelTransform != null) rearWheelBaseRot = rearWheelTransform.localRotation;
        if (visualRoot != null) visualRootBaseRot = visualRoot.localRotation;
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        AnimateHandlebar(dt);
        AnimateWheels(dt);
        AnimateLeanAndSlope(dt);
    }

    private void AnimateHandlebar(float dt)
    {
        if (handlebarTransform == null) return;

        // PhysicalSteeringDeg, no EffectiveSteeringDeg: ver comentario de cabecera.
        float target = bikeController.PhysicalSteeringDeg * (invertHandlebar ? -1f : 1f);

        displayedHandlebarDeg = handlebarSmoothTime > 0f
            ? Mathf.SmoothDamp(displayedHandlebarDeg, target, ref handlebarVelRef,
                               handlebarSmoothTime, Mathf.Infinity, dt)
            : target;

        // PRE-multiplicacion: el eje de giro se interpreta en el espacio del PADRE
        // (el cuadro de la bici), no en el espacio local de la horquilla. Por eso
        // Vector3.up es el vertical real de la bici sin importar como este orientado
        // el mesh de la horquilla, y el manubrio ya no sube ni baja al girar.
        handlebarTransform.localRotation =
            Quaternion.AngleAxis(displayedHandlebarDeg, Vector3.up) * handlebarBaseRot;
    }

    private void AnimateWheels(float dt)
    {
        if (wheelRadiusM <= 0.001f) return;
        if (frontWheelTransform == null && rearWheelTransform == null) return;

        // omega = v / r. Se acumula el angulo en vez de derivarlo de la distancia
        // total para que no se rompa al reposicionar la bici entre trials.
        float degPerSec = (bikeController.CurrentSpeedMps / wheelRadiusM) * Mathf.Rad2Deg;
        if (invertWheelSpin) degPerSec = -degPerSec;

        wheelSpinDeg = Mathf.Repeat(wheelSpinDeg + degPerSec * dt, 360f);

        Vector3 axis = AxisVector(wheelSpinAxis);
        Quaternion spin = Quaternion.AngleAxis(wheelSpinDeg, axis);

        // POST-multiplicacion, al reves que el manubrio: el eje de la rueda es una
        // propiedad del mesh, asi que se interpreta en su propio espacio local.
        if (frontWheelTransform != null) frontWheelTransform.localRotation = frontWheelBaseRot * spin;
        if (rearWheelTransform != null) rearWheelTransform.localRotation = rearWheelBaseRot * spin;
    }

    private void AnimateLeanAndSlope(float dt)
    {
        if (visualRoot == null) return;

        float targetLean = applyLean
            ? bikeController.LeanAngleDeg * leanVisualGain * (invertLean ? 1f : -1f)
            : 0f;

        displayedLeanDeg = Mathf.SmoothDamp(displayedLeanDeg, targetLean, ref leanVelRef,
                                            leanSmoothTime, Mathf.Infinity, dt);

        // Lean tambien en espacio del padre: el eje de inclinacion es la direccion
        // de marcha del vehiculo, no un eje del mesh importado.
        Quaternion leanRot = Quaternion.AngleAxis(displayedLeanDeg, Vector3.forward);

        if (alignToGroundSlope)
        {
            smoothedGroundNormal = Vector3.Slerp(
                smoothedGroundNormal, bikeController.GroundNormal,
                1f - Mathf.Exp(-dt / Mathf.Max(slopeSmoothTime, 0.001f)));

            Vector3 localNormal = visualRoot.parent != null
                ? visualRoot.parent.InverseTransformDirection(smoothedGroundNormal)
                : smoothedGroundNormal;

            Quaternion slopeRot = Quaternion.FromToRotation(Vector3.up, localNormal);
            visualRoot.localRotation = slopeRot * leanRot * visualRootBaseRot;
        }
        else
        {
            visualRoot.localRotation = leanRot * visualRootBaseRot;
        }
    }

    private static Vector3 AxisVector(SpinAxis a) => a switch
    {
        SpinAxis.X => Vector3.right,
        SpinAxis.Y => Vector3.up,
        _ => Vector3.forward
    };

    private void OnDrawGizmosSelected()
    {
        if (!showAxisGizmos) return;

        Gizmos.color = Color.red;
        DrawAxis(frontWheelTransform);
        DrawAxis(rearWheelTransform);

        // Eje de direccion: vertical del cuadro, en verde.
        if (handlebarTransform != null && handlebarTransform.parent != null)
        {
            Gizmos.color = Color.green;
            Vector3 up = handlebarTransform.parent.TransformDirection(Vector3.up) * 0.4f;
            Gizmos.DrawLine(handlebarTransform.position - up, handlebarTransform.position + up);
        }
    }

    private void DrawAxis(Transform t)
    {
        if (t == null) return;
        Vector3 dir = t.TransformDirection(AxisVector(wheelSpinAxis)) * 0.35f;
        Gizmos.DrawLine(t.position - dir, t.position + dir);
    }
}