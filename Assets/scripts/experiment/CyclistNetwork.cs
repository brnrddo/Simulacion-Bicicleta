// ============================================================
// CyclistNetwork.cs
// Red de caminos posibles del ciclista: todos los CyclistPath hijos.
// Se genera desde la red de trafico con SimBici > Generate Cyclist Network
// (o el boton del Inspector): cada carril de trafico da un tramo de bici
//   - por la ciclovia, si la calle tiene marcas de bicicleta a la derecha
//     del carril (el desplazamiento se interpola entre marcas, asi calza en curvas);
//   - si no, pegado a la solera derecha (curbClearanceM de la solera, medida con rayos),
//     que es como se anda en bici por la calle;
// y cada conector de interseccion (rectos y giros, aunque para los autos tengan
// peso 0) da un tramo de giro. Regenerar despues de cambiar los carriles de trafico.
// ============================================================

using System.Collections.Generic;
using UnityEngine;

public class CyclistNetwork : MonoBehaviour
{
    public const string GeneratedRootName = "Generated";

    [Header("Generacion")]
    [Tooltip("Calles sin ciclovia: distancia de la solera derecha al centro del ciclista (m). " +
             "Se mide la solera con rayos sobre la calzada.")]
    [Min(0.3f)] public float curbClearanceM = 0.75f;
    [Tooltip("Junto a autos estacionados (ParkedVehicle): distancia del centro del ciclista al costado del auto (m). " +
             "El camino se abre hacia la calzada con transiciones de 8 m.")]
    [Min(0.3f)] public float parkedCarClearanceM = 1.0f;
    [Tooltip("Respaldo si no se encuentra la solera: distancia del eje del carril hacia la derecha (m).")]
    [Min(0f)] public float rightOfLaneOffsetM = 1.0f;
    [Tooltip("Prefijo de los objetos de marca de ciclovia pintados en las calles.")]
    public string bikeMarkingPrefix = "Bicycle Road Marking";
    [Tooltip("Centro de la ciclovia medido desde la solera derecha (m). Ciclovia pintada del circuito: 2.4 m de ancho.")]
    [Min(0.3f)] public float bikeLaneCenterFromCurbM = 1.2f;
    [Tooltip("Una marca cuenta como ciclovia del carril si esta a su derecha, entre estos desplazamientos (m).")]
    public Vector2 markingLateralRangeM = new Vector2(0.8f, 6f);
    [Tooltip("Separacion de los nudos del spline de cada tramo (m).")]
    [Min(0.5f)] public float sampleSpacingM = 2f;

    [Header("Inicio y reaparicion")]
    [Tooltip("Carril de trafico cuyo tramo de bici es el inicio (se re-resuelve al regenerar).")]
    public string startLaneName = "Lane_North_01";
    [Tooltip("Metros desde el inicio de ese tramo.")]
    [Min(0f)] public float startDistanceM = 46f;
    public CyclistPath startPath;

    private readonly List<CyclistPath> paths = new List<CyclistPath>();

    /// <summary>Tramos de la red ordenados por nombre (orden estable).</summary>
    public IReadOnlyList<CyclistPath> Paths
    {
        get
        {
            if (paths.Count == 0) Collect();
            return paths;
        }
    }

    public float TotalLengthM
    {
        get
        {
            float sum = 0f;
            foreach (var p in Paths) sum += p.LengthM;
            return sum;
        }
    }

    private void Awake() => Collect();

    private void Collect()
    {
        paths.Clear();
        paths.AddRange(GetComponentsInChildren<CyclistPath>());
        paths.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        foreach (var p in paths) p.EnsureInit();
    }

    /// <summary>Tramos cuyo 'next' incluye a 'path'.</summary>
    public List<CyclistPath> PredecessorsOf(CyclistPath path)
    {
        var result = new List<CyclistPath>();
        foreach (var p in Paths)
            foreach (var n in p.Next)
                if (n == path) { result.Add(p); break; }
        return result;
    }

    /// <summary>Pose de inicio / reaparicion (opcionalmente adelantada 'extraM' metros sobre el tramo de inicio).</summary>
    public Vector3 GetStartPose(float extraM, out Vector3 forwardWorld)
    {
        if (startPath == null)
        {
            forwardWorld = transform.forward;
            return transform.position;
        }
        return startPath.GetPoint(startDistanceM + extraM, out forwardWorld);
    }
}
