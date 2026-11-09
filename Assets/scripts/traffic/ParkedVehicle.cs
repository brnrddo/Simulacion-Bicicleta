// ============================================================
// ParkedVehicle.cs
// Auto estacionado (escenografia): no circula ni lo ve el trafico, pero la bici
// choca con el (BoxCollider en StaticObstacle) y la red del ciclista lo esquiva.
// Lo coloca SimBici > Place Parked Cars. Pinta la carroceria al habilitarse,
// porque el MaterialPropertyBlock no se guarda en la escena.
// ============================================================

using UnityEngine;

[ExecuteAlways]
public class ParkedVehicle : MonoBehaviour
{
    [SerializeField] private string modelType = "auto";
    [SerializeField] private float lengthM = 4.3f;
    [SerializeField] private float widthM = 1.85f;
    [SerializeField] private bool paintBody = true;
    [SerializeField] private Color bodyColor = Color.white;
    [SerializeField] private Renderer body;

    private static readonly int BodyColorId = Shader.PropertyToID("_BodyColor");

    public string ModelType => modelType;
    public float LengthM => lengthM;
    public float WidthM => widthM;

    public void Setup(string type, float length, float width, Renderer bodyRenderer, bool paint, Color color)
    {
        modelType = type; lengthM = length; widthM = width; body = bodyRenderer; paintBody = paint; bodyColor = color;
        Apply();
    }

    private void OnEnable() => Apply();

    private void Apply()
    {
        if (body == null || !paintBody) return;
        var mpb = new MaterialPropertyBlock();
        body.GetPropertyBlock(mpb);
        mpb.SetColor(BodyColorId, bodyColor);
        body.SetPropertyBlock(mpb);
    }
}
