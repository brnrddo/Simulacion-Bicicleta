// ============================================================
// ScreenFader.cs
// Fade a negro que funciona en VR. Un Canvas "Screen Space - Overlay"
// NO se ve en el visor, asi que se usa una esfera pequena alrededor
// de la camara, con un material transparente cuyo alpha se anima.
// Va en la Main Camera del XR Origin (el mismo objeto que tiene Camera).
//
// Material: URP/Unlit, color negro. El script fuerza por codigo
// Transparent + Render Face Both + ZTest Always, asi que aunque el
// material este mal configurado en el Inspector deberia funcionar.
// ============================================================

using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class ScreenFader : MonoBehaviour
{
    [SerializeField] private Material fadeMaterial;

    [Tooltip("Radio de la esfera en metros. Se agranda solo si es menor que el Near Clip " +
             "de la camara (si no, la camara la recorta y el fade no se ve).")]
    [SerializeField] private float sphereRadiusM = 0.15f;

    [SerializeField] private Color fadeColor = Color.black;

    [Tooltip("Imprime en consola el estado de la camara y la esfera en cada FadeOut.")]
    [SerializeField] private bool logDiagnostics = true;

    [Tooltip("Al llegar a negro completo, la camara deja de dibujar la escena (fondo negro). " +
             "Respaldo por si la esfera no se ve: asegura que el teletransporte nunca sea visible.")]
    [SerializeField] private bool hardBlackoutAtFullAlpha = true;

    public bool IsFading { get; private set; }
    public float CurrentAlpha { get; private set; }

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private MeshRenderer sphereRenderer;
    private Material materialInstance;
    private Coroutine running;
    private Camera targetCamera;
    private bool blackoutActive;
    private int savedCullingMask;
    private CameraClearFlags savedClearFlags;
    private Color savedBackground;

    private static int PickVisibleLayer(Camera cam)
    {
        int own = cam.gameObject.layer;
        if ((cam.cullingMask & (1 << own)) != 0) return own;
        for (int i = 0; i < 32; i++)
            if ((cam.cullingMask & (1 << i)) != 0) return i;
        return 0;
    }

    private void Awake()
    {
        if (fadeMaterial == null)
        {
            Debug.LogError("[ScreenFader] Falta asignar el material de fade.");
            enabled = false;
            return;
        }

        // --- Radio compatible con el Near Clip de la camara ---
        // Causa tipica de "el fade no se ve": Near Clip = 0.3 (valor por defecto de Unity)
        // y la esfera de 0.15 m queda completa DENTRO de la zona recortada.
        Camera cam = GetComponent<Camera>();
        if (cam == null)
        {
            Debug.LogError("[ScreenFader] Este script debe ir en el MISMO objeto que la Camera " +
                           "(XR Origin > Camera Offset > Main Camera).");
            enabled = false;
            return;
        }
        float minRadius = cam.nearClipPlane * 1.5f;
        if (sphereRadiusM < minRadius)
        {
            Debug.LogWarning($"[ScreenFader] Near Clip de la camara = {cam.nearClipPlane:F2} m. " +
                             $"Radio de la esfera ajustado de {sphereRadiusM:F2} a {minRadius:F2} m. " +
                             "Recomendado: Near Clip 0.05 en la Main Camera.");
            sphereRadiusM = minRadius;
        }

        // --- Esfera ---
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "FadeSphere";
        // Sin collider: una esfera con collider pegada a la cabeza dispararia triggers
        DestroyImmediate(sphere.GetComponent<Collider>());

        sphere.transform.SetParent(transform, false);
        sphere.transform.localPosition = Vector3.zero;
        sphere.transform.localRotation = Quaternion.identity;
        sphere.transform.localScale = Vector3.one * sphereRadiusM * 2f;

        // La esfera tiene que estar en una layer que la camara dibuje (Culling Mask).
        // Se usa la layer de la propia camara si esta en su mascara; si no, la primera que si.
        sphere.layer = PickVisibleLayer(cam);

        sphereRenderer = sphere.GetComponent<MeshRenderer>();
        sphereRenderer.shadowCastingMode = ShadowCastingMode.Off;
        sphereRenderer.receiveShadows = false;

        targetCamera = cam;

        // --- Material: copia del asset con configuracion forzada ---
        materialInstance = new Material(fadeMaterial);
        ForceTransparentOverlay(materialInstance);
        sphereRenderer.sharedMaterial = materialInstance;

        SetAlpha(0f);
    }

    /// <summary>
    /// Fuerza en el material: transparente con alpha, visible por dentro (sin culling),
    /// sin escribir profundidad y dibujado encima de todo (ZTest Always).
    /// Propiedades del shader URP/Unlit de Unity 6.
    /// </summary>
    private static void ForceTransparentOverlay(Material m)
    {
        m.SetFloat("_Surface", 1f);                                    // Transparent
        m.SetFloat("_Blend", 0f);                                      // Alpha
        m.SetFloat("_Cull", (float)CullMode.Off);                      // Render Face: Both
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetFloat("_ZTest", (float)CompareFunction.Always);           // encima de todo
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)RenderQueue.Overlay;                      // despues de todo lo demas
    }

    /// <summary>Anima hasta negro. Usar con yield return en una corrutina.</summary>
    public Coroutine FadeOut(float durationSec)
    {
        LogDiagnostics();
        return StartFade(1f, durationSec);
    }

    /// <summary>Imprime en consola todo lo que puede impedir que el fade se vea.</summary>
    private void LogDiagnostics()
    {
        if (!logDiagnostics) return;
        Transform sphereT = sphereRenderer.transform;
        bool layerVisible = (targetCamera.cullingMask & (1 << sphereT.gameObject.layer)) != 0;
        Debug.Log($"[ScreenFader] FadeOut | camara='{targetCamera.name}' activa={targetCamera.isActiveAndEnabled} " +
                  $"nearClip={targetCamera.nearClipPlane:F3} | esfera radio={sphereT.lossyScale.x * 0.5f:F3} " +
                  $"layer='{LayerMask.LayerToName(sphereT.gameObject.layer)}' visibleParaCamara={layerVisible} " +
                  $"padre='{sphereT.parent.name}' | shader='{materialInstance.shader.name}' " +
                  $"queue={materialInstance.renderQueue} | Camera.main='{(Camera.main != null ? Camera.main.name : "null")}'");
    }

    /// <summary>Anima hasta transparente.</summary>
    public Coroutine FadeIn(float durationSec) => StartFade(0f, durationSec);

    private Coroutine StartFade(float targetAlpha, float durationSec)
    {
        if (running != null) StopCoroutine(running);
        running = StartCoroutine(FadeRoutine(targetAlpha, durationSec));
        return running;
    }

    private IEnumerator FadeRoutine(float targetAlpha, float durationSec)
    {
        IsFading = true;
        float start = CurrentAlpha;
        float elapsed = 0f;

        while (elapsed < durationSec)
        {
            elapsed += Time.unscaledDeltaTime;
            SetAlpha(Mathf.Lerp(start, targetAlpha, Mathf.Clamp01(elapsed / durationSec)));
            yield return null;
        }

        SetAlpha(targetAlpha);

        // Diagnostico: un frame despues de llegar a negro, ?alguna camara dibujo la esfera?
        if (logDiagnostics && targetAlpha >= 1f)
        {
            yield return null;
            Debug.Log($"[ScreenFader] Negro completo | rendererActivo={sphereRenderer.enabled} " +
                      $"dibujadaPorAlgunaCamara={sphereRenderer.isVisible} " +
                      $"colorMaterial={materialInstance.GetColor(BaseColorId)} " +
                      $"blackoutCamara={blackoutActive}");
        }

        IsFading = false;
        running = null;
    }

    private void SetAlpha(float a)
    {
        CurrentAlpha = a;
        Color c = fadeColor;
        c.a = a;
        materialInstance.SetColor(BaseColorId, c);
        sphereRenderer.enabled = a > 0.001f;

        // Respaldo: con negro completo la camara deja de dibujar la escena.
        // Garantiza que el participante NO vea el teletransporte aunque la esfera
        // no se renderice por algun motivo del pipeline.
        if (hardBlackoutAtFullAlpha) SetCameraBlackout(a >= 0.999f);
    }

    private void SetCameraBlackout(bool on)
    {
        if (on == blackoutActive || targetCamera == null) return;

        if (on)
        {
            savedCullingMask = targetCamera.cullingMask;
            savedClearFlags = targetCamera.clearFlags;
            savedBackground = targetCamera.backgroundColor;

            targetCamera.cullingMask = 0;                            // no dibujar ningun objeto
            targetCamera.clearFlags = CameraClearFlags.SolidColor;   // sin skybox
            targetCamera.backgroundColor = fadeColor;
        }
        else
        {
            targetCamera.cullingMask = savedCullingMask;
            targetCamera.clearFlags = savedClearFlags;
            targetCamera.backgroundColor = savedBackground;
        }
        blackoutActive = on;
    }

    private void OnDisable()
    {
        // Nunca dejar la camara apagada si el componente se desactiva a mitad de un fade
        if (blackoutActive) SetCameraBlackout(false);
    }

    // Prueba rapida: en Play, clic derecho sobre el componente -> "Probar fade"
    [ContextMenu("Probar fade")]
    private void TestFade()
    {
        if (!Application.isPlaying) { Debug.Log("[ScreenFader] Solo funciona en Play."); return; }
        StartCoroutine(TestRoutine());
    }

    private IEnumerator TestRoutine()
    {
        yield return FadeOut(0.5f);
        yield return new WaitForSecondsRealtime(1f);
        yield return FadeIn(0.5f);
    }
}
