// ============================================================
// PedestrianAgent.cs
// Estado y visual de un peaton. NO se mueve solo: PedestrianManager lo avanza
// en FixedUpdate a lo largo de un camino calculado sobre el NavMesh (veredas y
// pasos peatonales), con velocidad propia sorteada con semilla.
// Va en la raiz del prefab: CapsuleCollider + Rigidbody kinematico, layer
// Pedestrian (CollisionDetector del ciclista ya lo detecta). La animacion
// (Animator con parametro "Speed") se sincroniza con la velocidad real.
// ============================================================

using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class PedestrianAgent : MonoBehaviour
{
    [Tooltip("Velocidad propia del clip de caminar (m/s): a esa velocidad la animacion va a speed = 1. La fija Build Pedestrian Setup.")]
    [SerializeField] private float animNominalSpeedMps = 1.3f;
    [SerializeField] private float radiusM = 0.25f;

    public int Id { get; set; }
    public string Label { get; set; } = "";
    public PedestrianAgent Prefab { get; set; }
    public float RadiusM => radiusM;

    // Estado (lo escribe PedestrianManager o un PedestrianEvent)
    public float DesiredSpeedMps { get; set; } = 1.35f;
    public float SpeedMps { get; set; }
    public Vector3[] Corners { get; set; }
    public int CornerIndex { get; set; }
    public double WaitUntilS { get; set; }
    public Crosswalk Crossing { get; set; }         // cruzando la calzada por este paso
    public Crosswalk WaitingAt { get; set; }        // esperando en la cuneta de este paso
    public bool Scripted { get; set; }              // controlado por un PedestrianEvent (no esquiva)
    public bool OnRoad { get; set; }                // en la calzada fuera de un paso (cruce scriptado): los autos lo ven
    public bool DespawnWhenUnseen { get; set; }     // termino un evento fuera de las veredas
    public string State { get; set; } = "walking";  // walking / idle / waiting_signal / waiting_zebra / crossing / yielding_bike / scripted
    /// <summary>Posicion de simulacion (la del transform puede estar interpolada).</summary>
    public Vector3 Position { get; private set; }
    /// <summary>Velocidad en planta (x, z) del modelo de fuerza social.</summary>
    public Vector2 Velocity { get; set; }
    /// <summary>Destino del camino actual (para recalcularlo si el peaton queda trabado).</summary>
    public Vector3 Destination { get; set; }
    /// <summary>Tiempo que lleva sin avanzar queriendo avanzar (s).</summary>
    public float StuckS { get; set; }

    private Rigidbody rb;
    private Animator anim;
    private static readonly int SpeedHash = Animator.StringToHash("Speed");

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        anim = GetComponentInChildren<Animator>();
    }

    /// <summary>Mueve el peaton (en FixedUpdate) y orienta hacia donde camina.</summary>
    public void MoveTo(Vector3 position, Vector3 heading, float dt)
    {
        Position = position;
        rb.MovePosition(position);
        heading.y = 0f;
        if (heading.sqrMagnitude > 1e-4f)
        {
            Quaternion target = Quaternion.LookRotation(heading.normalized, Vector3.up);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, target, 360f * dt));
        }
    }

    public void Snap(Vector3 position, Vector3 heading)
    {
        heading.y = 0f;
        Quaternion r = heading.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(heading.normalized) : transform.rotation;
        Position = position;
        Velocity = Vector2.zero;
        StuckS = 0f;
        rb.position = position;
        rb.rotation = r;
        transform.SetPositionAndRotation(position, r);
    }

    /// <summary>Visual: animacion de caminar a la velocidad real (puede ir en Update).</summary>
    private void Update()
    {
        if (anim == null) return;
        anim.SetFloat(SpeedHash, SpeedMps);
        anim.speed = SpeedMps > 0.1f ? SpeedMps / animNominalSpeedMps : 1f;
    }
}
