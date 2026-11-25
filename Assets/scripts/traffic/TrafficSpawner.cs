// ============================================================
// TrafficSpawner.cs
// Genera autos al inicio del carril de un nodo de entrada (C2, C4...).
// Su flujo es la suma de su fila en la matriz origen-destino sorteada, y
// cada llegada lleva un destino sorteado con probabilidad q(i,j) / suma
// (lo configura OdDemand al iniciar).
// Los intervalos entre llegadas siguen una exponencial desplazada (llegadas
// aleatorias tipo Poisson, pero con un intervalo minimo realista), con la
// semilla del experimento: mismo flujo y misma semilla = misma secuencia.
// Si no hay espacio para aparecer (cola, ciclista), el auto queda
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

    [Header("Llegadas (el flujo lo fija la matriz origen-destino)")]
    [Tooltip("Intervalo minimo entre llegadas (s). Evita llegadas pegadas irreales.")]
    [SerializeField, Min(0.5f)] private float minHeadwayS = 1.2f;
    [Tooltip("Cola virtual fuera de la red: autos que llegaron y esperan espacio para entrar (p. ej. mientras la cola " +
             "de un rojo llega hasta el punto de aparicion). Mas alla se descartan y se registra. Con 5, en C4 " +
             "(semaforo a 60 m) se perdia el 15 % de la demanda.")]
    [SerializeField] private int maxPending = 30;

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
    public int PendingCount => pending;

    /// <summary>Nodo de origen (null si el spawner no esta en un nodo de entrada).</summary>
    public string OriginId { get; private set; }
    /// <summary>Flujo de llegada (veh/h): suma de la fila de su nodo en la matriz sorteada.</summary>
    public float FlowVehPerHour { get; private set; }

    private OdNode[] destinations = new OdNode[0];
    private float[] destinationFlows = new float[0];

    private System.Random rng;
    private float timeToNextArrival;
    private int pending;
    // Destino, vehiculo y conductor de la proxima llegada: se sortean una vez y esperan hasta que haya espacio.
    // Si se resorteaban en cada intento, con la entrada congestionada ganaban los mas cortos (sesgo de la mezcla).
    private OdNode nextDestination;
    private TrafficVehicle nextPrefab;
    private DriverProfile nextProfile;
    private IdmParams nextParams;
    private float nextSpeedFactor;
    private bool warnedNoRoute;

    public bool IsValid(out string error)
    {
        error = null;
        if (lane == null) error = "sin carril asignado";
        else if (driverProfile == null) error = "sin DriverProfile";
        else if (vehicles.Count == 0 || vehicles.TrueForAll(v => v.prefab == null)) error = "sin prefabs de autos";
        return error == null;
    }

    /// <summary>Fila de la matriz para este spawner (la asigna OdDemand antes de Init).</summary>
    public void SetDemand(string originId, IList<OdNode> dests, IList<float> flows)
    {
        OriginId = originId;
        destinations = new OdNode[dests.Count];
        destinationFlows = new float[flows.Count];
        dests.CopyTo(destinations, 0);
        flows.CopyTo(destinationFlows, 0);
        float total = 0f;
        foreach (float f in destinationFlows) total += Mathf.Max(0f, f);
        FlowVehPerHour = total;
    }

    public void Init(TrafficManager manager)
    {
        rng = manager.CreateRng(seedOffset);
        timeToNextArrival = FlowVehPerHour > 0f ? SampleHeadway(rng, FlowVehPerHour, minHeadwayS) : float.PositiveInfinity;
    }

    public void ResetCounters()
    {
        SpawnedCount = 0;
        DroppedCount = 0;
    }

    /// <summary>Lo llama TrafficManager una vez por tick.</summary>
    public void Tick(float dt, TrafficManager manager)
    {
        if (FlowVehPerHour <= 0f) return;

        timeToNextArrival -= dt;
        while (timeToNextArrival <= 0f)
        {
            if (pending < maxPending) pending++;
            else DroppedCount++;
            timeToNextArrival += SampleHeadway(rng, FlowVehPerHour, minHeadwayS);
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
        // Si el vehiculo sorteado ya llego a su tope (p. ej. 2 buses), se sortea otro para el mismo destino
        if (nextPrefab != null && manager.AtTypeLimit(nextPrefab.ModelType)) nextPrefab = null;
        if (nextPrefab == null)
        {
            if (nextDestination == null)
            {
                int d = OdMatrix.PickIndex(rng, destinationFlows);
                if (d < 0) return false;
                nextDestination = destinations[d];
            }
            nextPrefab = PickPrefab(manager, nextDestination);
            if (nextPrefab == null)
            {
                // Ningun vehiculo de la mezcla tiene ruta a ese destino: la llegada se descarta
                if (!warnedNoRoute) Debug.LogError($"[TrafficSpawner] {name}: no hay ruta de {lane.name} a {nextDestination.NodeId}.");
                warnedNoRoute = true;
                nextDestination = null;
                DroppedCount++;
                pending = Mathf.Max(0, pending - 1);
                return false;
            }
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

        var v = manager.SpawnVehicle(prefab, lane, spawnS, speed, p, speedFactor, OriginId, nextDestination);
        v.CriticalGapS = profile.SampleCriticalGap(rng);
        v.MinorStreetExtraGapS = profile.minorStreetExtraGapS;
        v.FollowBeforeOvertakeS = profile.SampleFollowBeforeOvertake(rng);
        nextPrefab = null;
        nextDestination = null;
        SpawnedCount++;
        return true;
    }

    /// <summary>Sorteo ponderado (un solo numero al azar) entre los vehiculos que no llegaron a su tope y tienen ruta al destino.</summary>
    private TrafficVehicle PickPrefab(TrafficManager manager, OdNode destination)
    {
        bool Allowed(VehicleOption v) => v.prefab != null && !manager.AtTypeLimit(v.prefab.ModelType) &&
                                         manager.HasRoute(lane, destination.Lane, v.prefab);
        float total = 0f;
        foreach (var v in vehicles) if (Allowed(v)) total += v.weight;
        if (total <= 0f) return null;
        double r = rng.NextDouble() * total;
        TrafficVehicle last = null;
        foreach (var v in vehicles)
        {
            if (!Allowed(v)) continue;
            last = v.prefab;
            r -= v.weight;
            if (r <= 0) return v.prefab;
        }
        return last;
    }

    private void OnDrawGizmos()
    {
        if (lane == null) return;
        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(lane.transform.position + Vector3.up * 0.75f, new Vector3(2f, 1.5f, 2f));
    }
}
