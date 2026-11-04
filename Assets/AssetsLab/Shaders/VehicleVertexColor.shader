// ============================================================
// VehicleVertexColor.shader (SimBici)
// Vehiculos del trafico: una sola malla por vehiculo con el color de cada
// pieza en los vertices (RGB). El canal alfa marca la carroceria (1) cuyo
// color se elige al aparecer (_BodyColor, propiedad por instancia: los autos
// con distinto color se siguen dibujando con GPU instancing).
// Built-in RP, iluminacion Standard, compatible con single pass instanced (VR).
// ============================================================
Shader "SimBici/VehicleVertexColor"
{
    Properties
    {
        _BodyColor ("Color de carroceria", Color) = (1, 1, 1, 1)
        _Glossiness ("Smoothness", Range(0, 1)) = 0.45
        _Metallic ("Metallic", Range(0, 1)) = 0.05
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows addshadow
        #pragma multi_compile_instancing
        #pragma target 3.0

        struct Input
        {
            float4 color : COLOR;
        };

        half _Glossiness;
        half _Metallic;

        UNITY_INSTANCING_BUFFER_START(Props)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _BodyColor)
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 body = UNITY_ACCESS_INSTANCED_PROP(Props, _BodyColor);
            o.Albedo = lerp(IN.color.rgb, body.rgb, IN.color.a);
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
