// ============================================================
// IntersectionController.cs
// Controlador de una interseccion semaforizada de 3 o 4 brazos.
//
// - El estado se calcula con SignalCycle a partir del tiempo de simulacion
//   (determinista): verde eje 1, amarillo, todo-rojo, verde eje 2, amarillo, todo-rojo.
// - Avanza en FixedUpdate. Si hay un TrafficManager en la escena, es el
//   TrafficManager quien lo avanza dentro de su paso (asi el semaforo y los autos
//   quedan sincronizados, tambien durante el precalentamiento).
// - Cada cambio de fase de un acceso activo publica "signal_phase" en el EventBus.
// - Las ResearchTrafficLight son solo visuales: muestran la fase que este controlador asigna.
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

[DefaultExecutionOrder(10)]  // antes de TrafficManager (20)
public class IntersectionController : MonoBehaviour
{
    [Header("Luces por acceso (acceso = brazo por el que llegan los autos)")]
    [SerializeField] private ResearchTrafficLight[] lightsNorth;
    [SerializeField] private ResearchTrafficLight[] lightsSouth;
    [SerializeField] private ResearchTrafficLight[] lightsEast;
    [SerializeField] private ResearchTrafficLight[] lightsWest;

    [Header("Accesos con trafico (3 o 4 brazos). Solo estos se registran en el log.")]
    [SerializeField] private List<Approach> activeApproaches = new List<Approach>
        { Approach.North, Approach.South, Approach.East, Approach.West };

    [Header("Tiempos (s)")]
    [SerializeField] private SignalTimingConfig timing = SignalTimingConfig.Default;

    /// <summary>Aviso de cambio de fase vehicular de un acceso (se emite para los 4 accesos).</summary>
    public event Action<Approach, SignalPhase> OnPhaseChanged;

    public double ElapsedS { get; private set; }
    public float CycleS => timing.CycleS;
    public SignalTimingConfig Timing => timing;
    public IReadOnlyList<Approach> ActiveApproaches => activeApproaches;

    /// <summary>Mientras es true no se publican eventos (lo usa el precalentamiento del trafico).</summary>
    public bool SuppressEvents { get; set; }

    private static readonly Approach[] AllApproaches = { Approach.North, Approach.South, Approach.East, Approach.West };
    private readonly SignalPhase[] lastPhase = new SignalPhase[4];
    private bool drivenExternally;
    private bool initialized;

    private void Awake()
    {
        BindLights();
        Refresh(force: true);
    }

    private void Start()
    {
        if (!drivenExternally) PublishCurrentState("start");
    }

    private void FixedUpdate()
    {
        if (!drivenExternally) Tick(Time.fixedDeltaTime);
    }

    /// <summary>Lo llama TrafficManager en Awake: desde ahi el avance lo hace su paso sincronizado.</summary>
    public void SetDrivenExternally(bool value) => drivenExternally = value;

    public void Tick(float dt)
    {
        ElapsedS += dt;
        Refresh(force: false);
    }

    // ------------------------------------------------------------
    // API publica
    // ------------------------------------------------------------
    public SignalState GetState(Approach a) => SignalCycle.Evaluate(timing, ElapsedS, a);

    public PedestrianState GetPedestrianState(Approach arm) => SignalCycle.EvaluatePedestrian(timing, ElapsedS, arm);

    public bool IsActive(Approach a) => activeApproaches.Contains(a);

    /// <summary>Cambia los tiempos (p. ej. desde ScenarioConfig). Llamar antes de Start.</summary>
    public void SetTimings(SignalTimingConfig newTiming)
    {
        timing = newTiming;
        Refresh(force: true);
    }

    /// <summary>Publica el estado de todos los accesos activos (al inicio de la sesion).</summary>
    public void PublishCurrentState(string reason)
    {
        foreach (var a in activeApproaches) Publish(a, GetState(a), reason);
    }

    // ------------------------------------------------------------
    // Internos
    // ------------------------------------------------------------
    private void Refresh(bool force)
    {
        for (int i = 0; i < AllApproaches.Length; i++)
        {
            Approach a = AllApproaches[i];
            SignalState s = GetState(a);
            if (!force && initialized && s.Phase == lastPhase[i]) continue;

            bool changed = initialized && s.Phase != lastPhase[i];
            lastPhase[i] = s.Phase;
            SetGroup(LightsOf(a), s.Phase);

            if (changed)
            {
                OnPhaseChanged?.Invoke(a, s.Phase);
                if (IsActive(a)) Publish(a, s, "change");
            }
        }
        initialized = true;
    }

    private void Publish(Approach a, SignalState s, string reason)
    {
        if (SuppressEvents || !Application.isPlaying) return;
        EventBus.Publish("signal_phase", name, string.Format(CultureInfo.InvariantCulture,
            "approach={0};phase={1};time_to_change_s={2:F2};cycle_s={3:F1};t_signal_s={4:F3};reason={5}",
            ApproachCode(a), s.Phase, s.TimeToChangeS, timing.CycleS, ElapsedS, reason));
    }

    public static string ApproachCode(Approach a) =>
        a == Approach.North ? "N" : a == Approach.South ? "S" : a == Approach.East ? "E" : "W";

    private ResearchTrafficLight[] LightsOf(Approach a)
    {
        switch (a)
        {
            case Approach.North: return lightsNorth;
            case Approach.South: return lightsSouth;
            case Approach.East: return lightsEast;
            default: return lightsWest;
        }
    }

    private void BindLights()
    {
        foreach (var a in AllApproaches)
        {
            var group = LightsOf(a);
            if (group == null) continue;
            foreach (var light in group) if (light != null) light.Bind(this, a);
        }
    }

    private static void SetGroup(ResearchTrafficLight[] group, SignalPhase phase)
    {
        if (group == null) return;
        foreach (var light in group) if (light != null) light.Show(phase);
    }
}
