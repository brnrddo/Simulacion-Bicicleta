// ============================================================
// IntersectionLaneBuilder.cs
// Datos para generar los carriles de una interseccion (la generacion
// esta en Editor/IntersectionLaneBuilderEditor.cs: boton en el Inspector
// o menu SimBici > Generate Intersection Lanes).
//
// Genera, bajo el hijo "Generated" (se borra y se rehace en cada generacion;
// no toca nada fuera de el salvo los nextLanes de los carriles 'feedFrom'):
//   <prefix>_<A>_In        carril de llegada hasta la linea de detencion (+ StopLine)
//   <prefix>_<A>_Conn_...  conectores Straight/Right/Left hacia los otros brazos
//   <prefix>_<A>_Out       carril de salida desde el borde de la interseccion
// Calcula ademas el limite de velocidad de cada conector (v = sqrt(a_lat R_min)) y sus
// puntos de conflicto con los otros conectores (cruces y convergencias), con quien cede.
// Opcionalmente ubica los postes de semaforo de cada acceso con llegada y pinta
// las lineas de detencion. SimBici > Fit Intersections To Curbs mide las soleras
// reales y lleva las lineas de detencion fuera de la curva de las esquinas.
// Despues de regenerar, correr tambien SimBici > Generate Cyclist Network.
//
// Convenciones:
//   - El brazo North sale del centro hacia +Z del builder (East = +X).
//   - Acceso = brazo por el que llegan los autos (igual que IntersectionController).
//   - Si se asigna feedFrom / continueTo, la geometria se ajusta a esos carriles
//     existentes para que los extremos coincidan exactamente (posicion lateral incluida).
// ============================================================

using System;
using System.Collections.Generic;
using UnityEngine;

public class IntersectionLaneBuilder : MonoBehaviour
{
    public const string GeneratedRootName = "Generated";

    [Serializable]
    public class Arm
    {
        public Approach approach = Approach.North;
        [Tooltip("Signal = semaforo del controlador. Priority = via con prioridad (sin linea). " +
                 "Stop = PARE (detencion obligatoria y brecha). Yield = CEDA EL PASO (brecha sin detencion).")]
        public ApproachControl control = ApproachControl.Signal;
        [Min(0)] public int inLanes = 1;
        [Min(0)] public int outLanes = 1;

        [Tooltip("Carril existente que llega a este brazo. Su final pasa a conectar con el carril In (solo el carril 0).")]
        public TrafficLane feedFrom;
        [Tooltip("Carril existente que continua despues del carril Out (solo el carril 0).")]
        public TrafficLane continueTo;

        [Tooltip("Distancia del centro de la interseccion a la linea de detencion de este brazo (m).")]
        [Min(0f)] public float stopLineDistanceM = 9f;
        [Tooltip("Largo del carril In si no hay feedFrom (m).")]
        [Min(1f)] public float approachLengthM = 30f;
        [Tooltip("Largo del carril Out si no hay continueTo (m).")]
        [Min(1f)] public float departureLengthM = 30f;

        [Header("Pesos propios de este acceso (si no, se usan los de la interseccion)")]
        public bool customWeights;
        [Min(0f)] public float straightWeight = 70f;
        [Min(0f)] public float rightWeight = 20f;
        [Min(0f)] public float leftWeight = 10f;

        [Header("Soleras (las mide SimBici > Fit Intersections To Curbs)")]
        [Tooltip("Distancia del eje del brazo a la solera del lado derecho de quien llega (m). Ubica los postes y la pintura.")]
        [Min(0f)] public float curbDistanceM = 6f;
        [Tooltip("Distancia del eje del brazo a la solera del lado izquierdo de quien llega (m). 0 = no medida.")]
        [Min(0f)] public float leftCurbDistanceM;

        [Header("Paso peatonal (accesos con semaforo)")]
        [Tooltip("Ancho del paso peatonal (m).")]
        [Min(1f)] public float crosswalkWidthM = 3f;
        [Tooltip("Distancia entre la linea de detencion y el borde exterior del paso peatonal (m).")]
        [Min(0f)] public float crosswalkSetbackM = 1.2f;
    }

    [Tooltip("Prefijo de los nombres generados (unico por interseccion, p. ej. I_NO).")]
    public string prefix = "I1";
    [Tooltip("Vacio = interseccion sin semaforo (los brazos Signal se tratan como Priority).")]
    public IntersectionController controller;

    [Header("Carriles")]
    [Min(1f)] public float laneWidthM = 3.5f;
    [Min(1f)] public float speedLimitKmh = 50f;
    [Tooltip("Aceleracion lateral admitida en los conectores: limite = sqrt(a_lat * R_min) (T3). Punto de partida: 2.0 m/s2.")]
    [Min(0.1f)] public float lateralAccelMps2 = 2.0f;
    [Tooltip("Limite minimo en los conectores (km/h), para radios muy chicos.")]
    [Min(1f)] public float minConnectorSpeedKmh = 10f;

    [Header("Pesos de los conectores (recto / derecha / izquierda)")]
    [Min(0f)] public float straightWeight = 70f;
    [Min(0f)] public float rightWeight = 20f;
    [Min(0f)] public float leftWeight = 10f;

    [Header("Semaforos")]
    [Tooltip("Ubicar los postes de las luces del controlador en cada acceso con llegada.")]
    public bool placeSignals = true;

    [Header("Pintura")]
    [Tooltip("Pintar la linea de detencion en los accesos con semaforo, PARE o CEDA.")]
    public bool paintStopLines = true;
    [Tooltip("Ancho de la linea de detencion pintada (m). [VERIFICAR] con el Manual de Senalizacion de Transito.")]
    [Min(0.1f)] public float stopLinePaintWidthM = 0.4f;

    public List<Arm> arms = new List<Arm>();

    /// <summary>Control efectivo del brazo (sin controlador, Signal pasa a Priority).</summary>
    public ApproachControl EffectiveControl(Arm arm) =>
        arm.control == ApproachControl.Signal && controller == null ? ApproachControl.Priority : arm.control;

    /// <summary>Distancia del centro al eje del paso peatonal del brazo (queda entre la interseccion y la linea).</summary>
    public float CrosswalkCenterDistance(Arm a) => a.stopLineDistanceM - a.crosswalkSetbackM - a.crosswalkWidthM * 0.5f;

    /// <summary>Direccion del brazo (del centro hacia afuera) en el mundo.</summary>
    public Vector3 ArmDirection(Approach a)
    {
        Vector3 local = a == Approach.North ? Vector3.forward
                      : a == Approach.South ? Vector3.back
                      : a == Approach.East ? Vector3.right
                      : Vector3.left;
        Vector3 d = transform.rotation * local;
        d.y = 0f;
        return d.normalized;
    }

    private void OnDrawGizmosSelected()
    {
        foreach (var arm in arms)
        {
            Vector3 dir = ArmDirection(arm.approach);
            Vector3 c = transform.position + Vector3.up * 0.1f;
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(c, c + dir * (arm.stopLineDistanceM + 5f));
            Vector3 right = Vector3.Cross(Vector3.up, -dir);
            Vector3 s = c + dir * arm.stopLineDistanceM;
            Gizmos.DrawLine(s - right * arm.curbDistanceM, s + right * arm.curbDistanceM);
        }
    }
}
