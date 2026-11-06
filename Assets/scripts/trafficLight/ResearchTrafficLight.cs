// ============================================================
// ResearchTrafficLight.cs
// Semaforo vehicular SOLO VISUAL: muestra la fase que le asigna su
// IntersectionController. No tiene ciclo propio ni emite eventos
// (los eventos los publica el controlador, una vez por acceso).
//
// Visibilidad en VR: el proyecto usa el Built-in Render Pipeline sin
// post-procesado (no hay bloom), asi que la emision del material no basta a
// 50-80 m. Cada lente encendida muestra ademas un "halo": un quad aditivo con
// degradado radial, delante de la lente y orientado como ella.
// ============================================================

using UnityEngine;

public class ResearchTrafficLight : MonoBehaviour
{
    [Header("Renderers de las lentes")]
    [SerializeField] private Renderer redRenderer;
    [SerializeField] private Renderer yellowRenderer;
    [SerializeField] private Renderer greenRenderer;

    [Header("Materiales de las lentes")]
    [SerializeField] private Material matOn;   // encendida (si no hay material por color)
    [SerializeField] private Material matOff;  // apagada (si no hay material por color)

    [Header("Materiales por color (opcional; si estan, reemplazan a matOn/matOff)")]
    [SerializeField] private Material redOn;
    [SerializeField] private Material redOff;
    [SerializeField] private Material yellowOn;
    [SerializeField] private Material yellowOff;
    [SerializeField] private Material greenOn;
    [SerializeField] private Material greenOff;

    [Header("Halo (opcional)")]
    [SerializeField] private Material haloRed;
    [SerializeField] private Material haloYellow;
    [SerializeField] private Material haloGreen;
    [Tooltip("Diametro del halo en metros (mundo).")]
    [SerializeField, Min(0f)] private float haloSizeM = 0.8f;

    public SignalPhase CurrentPhase { get; private set; } = SignalPhase.Red;
    public IntersectionController Controller { get; private set; }
    public Approach Approach { get; private set; }

    /// <summary>Tiempo en la fase actual, siempre leido del controlador (correcto aunque la luz no tenga ciclo propio).</summary>
    public float TimeInPhase => Controller != null ? Controller.GetState(Approach).TimeInPhaseS : 0f;

    private Renderer haloR, haloY, haloG;
    private bool shown;
    private static Mesh quadMesh;

    public void Bind(IntersectionController controller, Approach approach)
    {
        Controller = controller;
        Approach = approach;
    }

    private void Awake()
    {
        haloR = CreateHalo(redRenderer, haloRed);
        haloY = CreateHalo(yellowRenderer, haloYellow);
        haloG = CreateHalo(greenRenderer, haloGreen);

        // Si el controlador ya asigno una fase antes de este Awake, aplicarla tambien a los halos
        if (shown) { shown = false; Show(CurrentPhase); }
    }

    /// <summary>Muestra una fase. Usa sharedMaterial: no instancia materiales.</summary>
    public void Show(SignalPhase phase)
    {
        if (shown && phase == CurrentPhase) return;
        CurrentPhase = phase;
        shown = true;

        SetLens(redRenderer, haloR, phase == SignalPhase.Red, redOn, redOff);
        SetLens(yellowRenderer, haloY, phase == SignalPhase.Yellow, yellowOn, yellowOff);
        SetLens(greenRenderer, haloG, phase == SignalPhase.Green, greenOn, greenOff);
    }

    private void SetLens(Renderer lens, Renderer halo, bool on, Material colorOn, Material colorOff)
    {
        Material m = on ? (colorOn != null ? colorOn : matOn) : (colorOff != null ? colorOff : matOff);
        if (lens != null && m != null) lens.sharedMaterial = m;
        if (halo != null) halo.enabled = on;
    }

    private Renderer CreateHalo(Renderer lens, Material mat)
    {
        if (lens == null || mat == null || haloSizeM <= 0f) return null;

        var go = new GameObject(lens.name + "_halo");
        go.layer = lens.gameObject.layer;
        go.transform.SetParent(lens.transform, false);

        // Delante de la lente (la lente mira hacia +Z local de la luz) y del tamano pedido en el mundo
        Vector3 fwd = transform.forward;
        go.transform.position = lens.bounds.center + fwd * 0.03f;
        go.transform.rotation = Quaternion.LookRotation(-fwd, transform.up);
        Vector3 parentScale = lens.transform.lossyScale;
        go.transform.localScale = new Vector3(haloSizeM / Mathf.Max(parentScale.x, 1e-4f),
                                              haloSizeM / Mathf.Max(parentScale.y, 1e-4f), 1f);

        go.AddComponent<MeshFilter>().sharedMesh = GetQuad();
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.enabled = false;
        return r;
    }

    private static Mesh GetQuad()
    {
        if (quadMesh != null) return quadMesh;
        quadMesh = new Mesh { name = "TL_HaloQuad" };
        quadMesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
        };
        quadMesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        quadMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        quadMesh.RecalculateNormals();
        quadMesh.RecalculateBounds();
        return quadMesh;
    }
}
