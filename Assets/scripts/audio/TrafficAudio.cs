// ============================================================
// TrafficAudio.cs
// Audio de la simulacion (la bici no suena: el participante oye la real).
//   - Ambiente de ciudad: bucle 2D de fondo.
//   - Vehiculos cercanos: un grupo fijo de voces 3D (spatialize = true: las procesa el
//     espacializador del proyecto, Meta XR Audio con HRTF) que se
//     asignan a los vehiculos mas cercanos al oyente. Cada voz sigue a su vehiculo
//     y su tono y volumen dependen de la velocidad. El Doppler y la atenuacion por
//     distancia los calcula Unity.
//       autos: motor en ralenti (grabado) + neumatico (procedural);
//       bus electrico: neumatico mas grave y fuerte + zumbido del motor + murmullo
//       de ventilacion (procedurales).
// Solo presentacion: no cambia la simulacion. Las fases aleatorias de los bucles usan
// TrafficManager.CreateRng(semilla, 6000). Volumenes por categoria para manipularlos
// por condicion experimental.
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

[DefaultExecutionOrder(200)]   // despues del trafico (las posiciones del frame ya estan)
public class TrafficAudio : MonoBehaviour
{
    [Header("Grabaciones")]
    [SerializeField] private AudioClip ambientClip;
    [SerializeField] private AudioClip engineClip;

    [Header("Volumen por categoria (0-1)")]
    [SerializeField, Range(0f, 1f)] private float ambientVolume = 0.35f;
    [SerializeField, Range(0f, 1f)] private float carVolume = 1f;
    [SerializeField, Range(0f, 1f)] private float busVolume = 1f;

    [Header("Voces 3D")]
    [Tooltip("Vehiculos que suenan a la vez (los mas cercanos al oyente).")]
    [SerializeField, Min(1)] private int voiceCount = 10;
    [Tooltip("Mas alla de esta distancia un vehiculo no recibe voz (m).")]
    [SerializeField] private float assignDistanceM = 45f;
    [SerializeField] private float minDistanceM = 2f;
    [SerializeField] private float maxDistanceM = 60f;
    [SerializeField] private float fadeS = 0.35f;
    [SerializeField, Range(0f, 2f)] private float dopplerLevel = 1f;

    [Header("Autos [VERIFICAR] en el visor")]
    [SerializeField] private float engineIdlePitch = 0.9f;
    [SerializeField] private float enginePitchPerMps = 0.035f;
    [SerializeField] private float engineIdleVolume = 0.35f;
    [SerializeField] private float engineVolumePerMps = 0.012f;
    [Tooltip("Velocidad (m/s) a la que el neumatico llega a su volumen maximo (50 km/h).")]
    [SerializeField] private float tireRefSpeedMps = 13.9f;
    [SerializeField] private float tireMaxVolume = 0.8f;
    [SerializeField] private Vector2 tirePitch = new Vector2(0.7f, 1.25f);

    [Header("Bus electrico [VERIFICAR] en el visor")]
    [SerializeField] private float busTirePitchScale = 0.75f;
    [SerializeField] private float busTireGain = 1.4f;
    [SerializeField] private float whineBasePitch = 0.4f;
    [SerializeField] private float whinePitchPerMps = 0.09f;
    [SerializeField] private float whineVolume = 0.12f;
    [SerializeField] private float humVolume = 0.10f;

    private class Voice
    {
        public GameObject go;
        public AudioSource motor, tire, hum;
        public TrafficVehicle vehicle;
        public float fade;          // 0..1
        public bool releasing;
        public float dopplerHoldS;  // tras reasignar: sin Doppler un momento (el salto de posicion no es velocidad)
    }

    private readonly List<Voice> voices = new List<Voice>();
    private readonly List<TrafficVehicle> nearest = new List<TrafficVehicle>();
    private AudioSource ambient;
    private AudioClip tireClip, whineClip, humClip;
    private Transform listener;
    private System.Random rng;
    private float reassignTimer;

    public float AmbientVolume { get => ambientVolume; set => ambientVolume = Mathf.Clamp01(value); }
    public float CarVolume { get => carVolume; set => carVolume = Mathf.Clamp01(value); }
    public float BusVolume { get => busVolume; set => busVolume = Mathf.Clamp01(value); }

    private void Start()
    {
        int seed = TrafficManager.Instance != null ? TrafficManager.Instance.MasterSeed : 12345;
        rng = TrafficManager.CreateRng(seed, 6000);
        var al = FindFirstObjectByType<AudioListener>();
        listener = al != null ? al.transform : (Camera.main != null ? Camera.main.transform : transform);

        int sr = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
        tireClip = ProceduralAudio.TireNoise(sr, 6001);
        whineClip = ProceduralAudio.ElectricWhine(sr, 6002);
        humClip = ProceduralAudio.BusHum(sr, 6003);

        if (ambientClip != null)
        {
            ambient = gameObject.AddComponent<AudioSource>();
            ambient.clip = ambientClip; ambient.loop = true; ambient.spatialBlend = 0f; ambient.playOnAwake = false;
            ambient.volume = ambientVolume;
            ambient.time = (float)(rng.NextDouble() * ambientClip.length * 0.9);
            ambient.Play();
        }
        for (int i = 0; i < voiceCount; i++) voices.Add(CreateVoice(i));

        EventBus.Publish("audio_start", "audio", string.Format(CultureInfo.InvariantCulture,
            "ambient={0:F2};cars={1:F2};bus={2:F2};voices={3};spatializer={4}",
            ambientVolume, carVolume, busVolume, voiceCount, AudioSettings.GetSpatializerPluginName()));
    }

    private Voice CreateVoice(int i)
    {
        var go = new GameObject($"Voz_{i:D2}");
        go.transform.SetParent(transform, false);
        var v = new Voice { go = go };
        v.motor = Source(go); v.tire = Source(go); v.hum = Source(go);
        go.SetActive(false);
        return v;
    }

    private AudioSource Source(GameObject go)
    {
        var s = go.AddComponent<AudioSource>();
        s.loop = true; s.playOnAwake = false; s.spatialBlend = 1f; s.spatialize = true;
        s.rolloffMode = AudioRolloffMode.Logarithmic; s.minDistance = minDistanceM; s.maxDistance = maxDistanceM;
        s.dopplerLevel = dopplerLevel; s.volume = 0f;
        return s;
    }

    private void Update()
    {
        if (ambient != null) ambient.volume = ambientVolume;
        reassignTimer -= Time.deltaTime;
        if (reassignTimer <= 0f) { reassignTimer = 0.1f; Reassign(); }
    }

    /// <summary>Las voces van a los vehiculos mas cercanos (orden estable por distancia y Id).</summary>
    private void Reassign()
    {
        var tm = TrafficManager.Instance;
        nearest.Clear();
        if (tm != null)
        {
            Vector3 l = listener.position;
            foreach (var v in tm.Vehicles)
                if (v != null && (v.transform.position - l).sqrMagnitude < assignDistanceM * assignDistanceM) nearest.Add(v);
            nearest.Sort((a, b) =>
            {
                int c = (a.transform.position - l).sqrMagnitude.CompareTo((b.transform.position - l).sqrMagnitude);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });
            if (nearest.Count > voiceCount) nearest.RemoveRange(voiceCount, nearest.Count - voiceCount);
        }
        // Liberar las voces de vehiculos que ya no estan entre los cercanos
        foreach (var vc in voices)
            if (vc.vehicle != null && !vc.releasing && !nearest.Contains(vc.vehicle)) vc.releasing = true;
        // Asignar voces libres a los cercanos sin voz
        foreach (var veh in nearest)
        {
            bool has = false;
            foreach (var vc in voices) if (vc.vehicle == veh && !vc.releasing) { has = true; break; }
            if (has) continue;
            Voice free = null;
            foreach (var vc in voices) if (vc.vehicle == null) { free = vc; break; }
            if (free == null) break;
            Assign(free, veh);
        }
    }

    private void Assign(Voice vc, TrafficVehicle veh)
    {
        vc.vehicle = veh; vc.fade = 0f; vc.releasing = false; vc.dopplerHoldS = 0.25f;
        vc.go.transform.position = veh.transform.position;
        vc.go.SetActive(true);
        bool bus = veh.ModelType == "bus";
        PlayLoop(vc.motor, bus ? whineClip : engineClip);
        PlayLoop(vc.tire, tireClip);
        if (bus) PlayLoop(vc.hum, humClip); else vc.hum.Stop();
    }

    private void PlayLoop(AudioSource s, AudioClip clip)
    {
        if (clip == null) { s.Stop(); return; }
        s.clip = clip;
        s.volume = 0f;
        s.dopplerLevel = 0f;
        s.time = (float)(rng.NextDouble() * clip.length * 0.95);
        s.Play();
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        foreach (var vc in voices)
        {
            // El vehiculo desaparecio o volvio a la reserva (se reutiliza en otro lugar): la voz se libera
            if (vc.vehicle == null || !vc.vehicle.gameObject.activeInHierarchy)
            {
                if (vc.go.activeSelf) { vc.fade = 0f; Release(vc); }
                continue;
            }
            vc.fade = Mathf.MoveTowards(vc.fade, vc.releasing ? 0f : 1f, dt / Mathf.Max(fadeS, 0.01f));
            if (vc.releasing && vc.fade <= 0f) { Release(vc); continue; }

            var veh = vc.vehicle;
            bool bus = veh.ModelType == "bus";
            if (!vc.releasing) vc.go.transform.position = veh.transform.position + Vector3.up * (bus ? 1.0f : 0.5f);
            vc.dopplerHoldS -= dt;
            float doppler = vc.dopplerHoldS > 0f ? 0f : dopplerLevel;
            vc.motor.dopplerLevel = vc.tire.dopplerLevel = vc.hum.dopplerLevel = doppler;

            float v = Mathf.Max(0f, veh.Speed);
            float tireLevel = Mathf.Pow(Mathf.Clamp01(v / tireRefSpeedMps), 1.5f);
            float tirePitchNow = Mathf.Lerp(tirePitch.x, tirePitch.y, Mathf.Clamp01(v / (tireRefSpeedMps * 1.2f)));
            if (bus)
            {
                float g = busVolume * vc.fade;
                vc.tire.pitch = tirePitchNow * busTirePitchScale;
                vc.tire.volume = g * Mathf.Min(1f, tireMaxVolume * busTireGain * tireLevel);
                vc.motor.pitch = whineBasePitch + whinePitchPerMps * v;
                vc.motor.volume = g * whineVolume * Mathf.Clamp01(0.3f + v / 8f);
                vc.hum.volume = g * humVolume;
            }
            else
            {
                float g = carVolume * vc.fade;
                vc.tire.pitch = tirePitchNow;
                vc.tire.volume = g * tireMaxVolume * tireLevel;
                vc.motor.pitch = engineIdlePitch + enginePitchPerMps * v;
                vc.motor.volume = g * Mathf.Min(1f, engineIdleVolume + engineVolumePerMps * v);
            }
        }
    }

    private void Release(Voice vc)
    {
        vc.motor.Stop(); vc.tire.Stop(); vc.hum.Stop();
        vc.vehicle = null; vc.releasing = false;
        vc.go.SetActive(false);
    }
}
