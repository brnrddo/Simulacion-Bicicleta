// ============================================================
// CrashResetHandler.cs
// Al chocar:
//   1. congela la bici y apaga la deteccion
//   2. fade a negro
//   3. teletransporta al inicio de la ruta
//   4. espera en negro
//   5. fade in con la bici TODAVIA congelada
//   6. libera la bici solo cuando la rueda se detuvo
// El paso 6 existe porque la rueda del rodillo sigue girando por
// inercia (y con teclado la velocidad se conserva): si se libera de
// inmediato, la bici "avanza sola" apenas vuelve la imagen.
// Va en el objeto _Experiment.
// ============================================================

using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;

public class CrashResetHandler : MonoBehaviour
{
    [Header("Referencias")]
    [SerializeField] private CollisionDetector collisionDetector;
    [SerializeField] private BicycleController bike;
    [SerializeField] private RiderRoute route;
    [SerializeField] private ScreenFader fader;

    [Header("Punto de reaparicion")]
    [Tooltip("Metros adelante del punto de inicio de la red del ciclista (CyclistNetwork). 0 = el inicio.")]
    [SerializeField] private float respawnDistanceM = 0f;

    [Header("Tiempos (s)")]
    [SerializeField] private float fadeOutSec = 0.4f;
    [Tooltip("Tiempo en negro despues de reposicionar.")]
    [SerializeField] private float holdBlackSec = 1.5f;
    [SerializeField] private float fadeInSec = 0.6f;

    [Header("Liberacion de la bici")]
    [Tooltip("La bici se libera cuando la velocidad de entrada (rueda o teclado) " +
             "queda bajo este valor.")]
    [SerializeField] private float releaseSpeedThresholdMps = 0.3f;
    [Tooltip("Cuanto tiempo seguido tiene que estar bajo el umbral.")]
    [SerializeField] private float releaseStableSec = 0.3f;
    [Tooltip("Si la rueda no se detiene en este tiempo, se libera igual y se registra timeout.")]
    [SerializeField] private float releaseTimeoutSec = 15f;

    [Header("Reinicio manual (experimentador)")]
    [SerializeField] private bool allowManualReset = true;
    [SerializeField] private Key manualResetKey = Key.R;

    public bool IsResetting { get; private set; }
    public int ResetCount { get; private set; }

    private void Awake()
    {
        if (bike == null || route == null)
        {
            Debug.LogError("[CrashResetHandler] Faltan referencias (bike o route).");
            enabled = false;
            return;
        }
        if (fader == null)
            Debug.LogWarning("[CrashResetHandler] Sin ScreenFader: el reinicio sera sin fade.");
    }

    private void OnEnable()
    {
        if (collisionDetector != null) collisionDetector.Collided += HandleCollision;
    }

    private void OnDisable()
    {
        if (collisionDetector != null) collisionDetector.Collided -= HandleCollision;
    }

    private void Update()
    {
        if (!allowManualReset || IsResetting || Keyboard.current == null) return;
        if (Keyboard.current[manualResetKey].wasPressedThisFrame)
            StartCoroutine(ResetRoutine("manual"));
    }

    private void HandleCollision(Collider other)
    {
        if (IsResetting) return;
        StartCoroutine(ResetRoutine("collision"));
    }

    private bool FaderUsable => fader != null && fader.isActiveAndEnabled;

    private IEnumerator ResetRoutine(string cause)
    {
        IsResetting = true;
        ResetCount++;

        // 1. Congelar YA: si la bici sigue avanzando durante el fade, atraviesa el objeto
        bike.SetFrozen(true);
        if (collisionDetector != null) collisionDetector.DetectionEnabled = false;

        EventBus.Publish("reset_start", "rider", string.Format(CultureInfo.InvariantCulture,
            "cause={0};count={1};route_dist_m={2:F2}", cause, ResetCount, route.DistanceAlongM));

        // 2. Negro
        if (FaderUsable) yield return fader.FadeOut(fadeOutSec);
        else Debug.LogWarning(fader == null
            ? "[CrashResetHandler] Campo Fader vacio: arrastra la Main Camera (con ScreenFader) al campo Fader."
            : "[CrashResetHandler] ScreenFader esta desactivado (revisa errores de ScreenFader en consola).");

        // 3. Reposicionar con la pantalla en negro
        Vector3 point = route.GetRespawnPose(respawnDistanceM, out Vector3 forward);
        float yaw = Quaternion.LookRotation(forward, Vector3.up).eulerAngles.y;
        bike.TeleportRider(point, yaw);
        route.ForceResync();

        // 4. Espera en negro
        yield return new WaitForSecondsRealtime(holdBlackSec);

        // 5. Volver a ver, todavia congelado
        if (FaderUsable) yield return fader.FadeIn(fadeInSec);

        // 6. Esperar a que la rueda se detenga
        float waitStart = Time.unscaledTime;
        float stableSince = -1f;
        bool timedOut = false;

        while (true)
        {
            float now = Time.unscaledTime;
            if (bike.InputSpeedMps <= releaseSpeedThresholdMps)
            {
                if (stableSince < 0f) stableSince = now;
                if (now - stableSince >= releaseStableSec) break;
            }
            else
            {
                stableSince = -1f;
            }

            if (now - waitStart >= releaseTimeoutSec)
            {
                timedOut = true;
                break;
            }
            yield return null;
        }

        float waitedSec = Time.unscaledTime - waitStart;

        if (collisionDetector != null) collisionDetector.DetectionEnabled = true;
        bike.SetFrozen(false);

        EventBus.Publish("reset_end", "rider", string.Format(CultureInfo.InvariantCulture,
            "cause={0};count={1};wait_stop_s={2:F2};timeout={3}",
            cause, ResetCount, waitedSec, timedOut ? 1 : 0));

        IsResetting = false;
    }
}
