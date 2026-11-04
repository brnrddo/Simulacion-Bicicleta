// ============================================================
// TrafficSpawner.cs
// Genera autos al inicio de un carril con un flujo dado (veh/h).
// Los intervalos entre llegadas siguen una exponencial desplazada
// (llegadas aleatorias tipo Poisson, pero con un intervalo minimo
// realista), sorteados con la semilla del experimento: mismo flujo
// y misma semilla = misma secuencia de llegadas.
// Si no hay espacio para aparecer (cola, ciclista) o la red ya tiene la
// cantidad objetivo de autos (TrafficManager.TargetVehicles), el auto queda
// "esperando" y aparece apenas se puede.
// ============================================================

using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class VehicleOption
{
    public TrafficVehicle prefab;
    [Min(0f)] public float weight = 1f;
}

public class TrafficSpawner : MonoBehaviour
{
    [Header("Donde")]
    [SerializeField] private TrafficLane lane;

    [Header("Cuanto")]
    [Tooltip("Flujo de llegada en vehiculos por hora.")]
    [SerializeField, Min(0f)] private float flowVehPerHour = 400f;
    [Tooltip("Intervalo minimo entre llegadas (s). Evita llegadas pegadas irreales.")]
    [SerializeField, Min(0.5f)] private float minHeadwayS = 1.2f;
    [Tooltip("Maximo de autos esperando para entrar. Mas alla se descartan (y se registra).")]
    [SerializeField] private int maxPending = 5;

    [Header("Que")]
    [SerializeField] private List<VehicleOption> vehicles = new List<VehicleOption>();
    [SerializeField] private DriverProfile driverProfile;

    [Header("Reproducibilidad")]
    [Tooltip("Cada spawner debe tener un valor distinto. Se combina con la semilla maestra.")]
    [SerializeField] private int seedOffset = 1;

    public TrafficLane Lane => lane;
    public IEnumerable<TrafficVehicle> Prefabs { get { foreach (var v in vehicles) if (v.prefab != null) yield return v.prefab; } }
    public int SpawnedCount { get; private set; }
    public int DroppedCount { get; private set; }

    private System.Random rng;
    private float timeToNextArrival;
    private int pending;
    // Vehiculo y conductor de la proxima llegada: se sortean una vez y esperan hasta que haya espacio. Si se
    // resorteaban en cada intento, con la entrada congestionada ganaban los mas cortos (sesgo de la mezcla).
    private TrafficVehicle nextPrefab;
    private DriverProfile nextProfile;
    private IdmParams nextParams;
    private float nextSpeedFactor;

    public float FlowVehPerHour
    {
        get => flowVehPerHour;
        set => flowVehPerHour = Mathf.Max(0f, value);
    }

    public bool IsValid(out string error)
    {
        error = null;
        if (lane == null) error = "sin carril asignado";
        else if (driverProfile == null) error = "sin DriverProfile";
        else if (vehicles.Count == 0 || vehicles.TrueForAll(v => v.prefab == null)) error = "sin prefabs de autos";
        return error == null;
    }

    public void Init(TrafficManager manager)
    {
        rng = manager.CreateRng(seedOffset);
        timeToNextArrival = SampleHeadway(rng, EffectiveFlow(manager), minHeadwayS);
    }

    /// <summary>Flujo real: el nominal del spawner por el multiplicador global (y el refuerzo del precalentamiento).</summary>
    public float EffectiveFlow(TrafficManager manager) => flowVehPerHour * manager.FlowMultiplier * manager.SpawnFlowBoost;

    /// <summary>Lo llama TrafficManager una vez por tick.</summary>
    public void Tick(float dt, TrafficManager manager)
    {
        float flow = EffectiveFlow(manager);
        if (flow <= 0f) return;

        timeToNextArrival -= dt;
        while (timeToNextArrival <= 0f)
        {
            if (pending < maxPending) pending++;
            else DroppedCount++;
            timeToNextArrival += SampleHeadway(rng, flow, minHeadwayS);
        }

        if (pending > 0 && TrySpawn(manager)) pending--;
    }

    /// <summary>
    /// Intervalo entre llegadas: exponencial desplazada con media 3600/flujo y minimo minHeadwayS.
    /// Estatico y puro para poder testear el determinismo.
    /// </summary>
    public static float SampleHeadway(System.Random rng, float flowVehPerHour, float minHeadwayS)
    {
        float meanS = 3600f / Mathf.Max(flowVehPerHour, 1f);
        float extraMean = Mathf.Max(meanS - minHeadwayS, 0.01f);
        double u = 1.0 - rng.NextDouble();
        return minHeadwayS + (float)(-Math.Log(u) * extraMean);
    }

    private bool TrySpawn(TrafficManager manager)
    {
        // Cantidad estable: no generar por encima del objetivo del nivel de trafico (la llegada queda pendiente)
        if (manager.ActiveCount >= manager.TargetVehicles) return false;

        if (nextPrefab == null)
        {
            nextPrefab = PickPrefab();
            nextProfile = nextPrefab.ProfileOverride != null ? nextPrefab.ProfileOverride : driverProfile;  // p. ej. bus
            nextParams = nextProfile.Sample(rng, out nextSpeedFactor);
        }
        TrafficVehicle prefab = nextPrefab;
        DriverProfile profile = nextProfile;
        IdmParams p = nextParams;
        float speedFactor = nextSpeedFactor;
        float desired = lane.SpeedLimitMps * speedFactor;
        float halfLen = prefab.LengthM * 0.5f;
        float spawnS = halfLen;  // la cola del auto justo en el inicio del carril

        // Obstaculo mas cercano adelante del punto de aparicion: ultimo auto del carril o ciclista
        float obstacleRearS = float.PositiveInfinity;
        float obstacleSpeed = desired;

        if (lane.Vehicles.Count > 0)
        {
            var last = lane.Vehicles[0];
            obstacleRearS = last.S - last.LengthM * 0.5f;
            obstacleSpeed = last.Speed;
        }
        if (manager.TryGetBikeOnLane(lane, out float bikeS, out float bikeSpeedAlong) &&
            bikeS - manager.BikeHalfLengthM < obstacleRearS)
        {
            obstacleRearS = bikeS - manager.BikeHalfLengthM;
            obstacleSpeed = bikeSpeedAlong;
        }

        float speed = desired;
        if (!float.IsPositiveInfinity(obstacleRearS))
        {
            float gap = obstacleRearS - (spawnS + halfLen);
            speed = Mathf.Clamp(obstacleSpeed, 0f, desired);
            // Espacio minimo segun el propio IDM: s0 + v*T
            if (gap < p.minGapM + speed * p.timeHeadwayS) return false;
        }

        // Autos que vienen llegando desde un carril anterior (p. ej. spawner dentro del circuito):
        // no aparecer justo delante de ellos.
        foreach (var other in manager.Vehicles)
        {
            if (other.NextLane != lane) continue;
            float gapBehind = other.Lane.LengthM - (other.S + other.LengthM * 0.5f);
            if (gapBehind < other.Params.minGapM + other.Speed * other.Params.timeHeadwayS) return false;
        }

        var v = manager.SpawnVehicle(prefab, lane, spawnS, speed, p, speedFactor);
        v.CriticalGapS = profile.SampleCriticalGap(rng);
        v.MinorStreetExtraGapS = profile.minorStreetExtraGapS;
        v.FollowBeforeOvertakeS = profile.SampleFollowBeforeOvertake(rng);
        nextPrefab = null;
        SpawnedCount++;
        return true;
    }

    private TrafficVehicle PickPrefab()
    {
        float total = 0f;
        foreach (var v in vehicles) if (v.prefab != null) total += v.weight;
        double r = rng.NextDouble() * total;
        foreach (var v in vehicles)
        {
            if (v.prefab == null) continue;
            r -= v.weight;
            if (r <= 0) return v.prefab;
        }
        return vehicles.Find(v => v.prefab != null).prefab;
    }

    private void OnDrawGizmos()
    {
        if (lane == null) return;
        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(lane.transform.position + Vector3.up * 0.75f, new Vector3(2f, 1.5f, 2f));
    }
}
