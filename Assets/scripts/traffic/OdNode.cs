// ============================================================
// OdNode.cs
// Nodo origen o destino del trafico (C1..C4 en el dibujo del experimento):
// el carril por el que los autos entran a la red o por el que salen.
// Es lo unico que une la matriz origen-destino con la red de carriles.
// Lo crea SimBici > Remove Streets; los revisa SimBici > Validate Network.
// ============================================================

using UnityEngine;

public class OdNode : MonoBehaviour
{
    public enum NodeKind { Entry, Exit }

    [Tooltip("Nombre del nodo en la matriz origen-destino (C1, C2, ...).")]
    [SerializeField] private string nodeId = "C1";
    [Tooltip("Entry: los autos aparecen al inicio del carril. Exit: salen de la red al final del carril.")]
    [SerializeField] private NodeKind kind = NodeKind.Entry;
    [SerializeField] private TrafficLane lane;

    public string NodeId => nodeId;
    public NodeKind Kind => kind;
    public TrafficLane Lane => lane;

    public void Setup(string id, NodeKind nodeKind, TrafficLane nodeLane)
    {
        nodeId = id;
        kind = nodeKind;
        lane = nodeLane;
    }

    private void OnDrawGizmos()
    {
        if (lane == null) return;
        Gizmos.color = kind == NodeKind.Entry ? Color.green : Color.red;
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 1f, 2f);
#if UNITY_EDITOR
        UnityEditor.Handles.Label(transform.position + Vector3.up * 4f, $"{nodeId} ({kind})");
#endif
    }
}
