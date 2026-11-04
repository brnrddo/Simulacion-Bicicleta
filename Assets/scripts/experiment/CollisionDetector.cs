// ============================================================
// CollisionDetector.cs
// Va en un HIJO de playerBike que tenga un BoxCollider con
// "Is Trigger" activo (el volumen de bici + ciclista).
// playerBike es kinematico: no hay respuesta fisica al chocar,
// solo se detecta el contacto, se publica en EventBus y se avisa
// a quien este suscrito (CrashResetHandler).
// ============================================================

using System;
using System.Globalization;
using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
public class CollisionDetector : MonoBehaviour
{
    [Header("Referencias")]
    [SerializeField] private BicycleController bike;
    [Tooltip("Opcional: agrega distancia en ruta y offset al detalle del evento.")]
    [SerializeField] private RiderRoute route;

    [Header("Que cuenta como choque")]
    [Tooltip("Marcar SOLO Vehicle, Pedestrian y StaticObstacle. Nunca Ground ni player.")]
    [SerializeField] private LayerMask collisionLayers = 0;

    [Tooltip("Tiempo minimo entre dos choques registrados. Evita que un mismo contacto " +
             "(varios colliders del mismo auto) genere varios eventos.")]
    [SerializeField] private float cooldownSec = 1.0f;

    /// <summary>Se dispara una vez por choque valido. Argumento: el collider golpeado.</summary>
    public event Action<Collider> Collided;

    /// <summary>Mientras sea false, los contactos se ignoran (lo usa el reinicio).</summary>
    public bool DetectionEnabled { get; set; } = true;

    public int CollisionCount { get; private set; }

    private float lastCollisionTime = -999f;

    private void Awake()
    {
        var box = GetComponent<BoxCollider>();
        if (!box.isTrigger)
        {
            Debug.LogWarning("[CollisionDetector] El BoxCollider no era trigger; se activo Is Trigger.");
            box.isTrigger = true;
        }
        if (bike == null) bike = GetComponentInParent<BicycleController>();
        if (collisionLayers == 0)
            Debug.LogError("[CollisionDetector] collisionLayers vacio: no se detectara ningun choque.");
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!DetectionEnabled) return;
        if ((collisionLayers.value & (1 << other.gameObject.layer)) == 0) return;
        if (Time.time - lastCollisionTime < cooldownSec) return;

        lastCollisionTime = Time.time;
        CollisionCount++;

        // Si el objeto tiene Rigidbody (autos, peatones), identificarlo por su raiz
        // y no por el collider hijo (rueda, parachoques...).
        GameObject hitObject = other.attachedRigidbody != null ? other.attachedRigidbody.gameObject : other.gameObject;
        string layerName = LayerMask.LayerToName(other.gameObject.layer);
        float speed = bike != null ? bike.CurrentSpeedMps : 0f;

        string detail = string.Format(CultureInfo.InvariantCulture,
            "layer={0};object={1};speed_mps={2:F2};count={3}",
            layerName, hitObject.name.Replace(';', '_'), speed, CollisionCount);

        EventBus.Publish("collision", hitObject.name, detail);
        Collided?.Invoke(other);
    }

    private void OnDrawGizmos()
    {
        // Caja roja semitransparente = volumen de choque del ciclista
        var box = GetComponent<BoxCollider>();
        if (box == null) return;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0f, 0f, 0.25f);
        Gizmos.DrawCube(box.center, box.size);
    }
}
