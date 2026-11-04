// ============================================================
// WorldTile.shader (SimBici)
// Veredas de baldosa (A2): la textura se proyecta desde arriba en coordenadas
// de mundo (x, z), asi las baldosas miden lo mismo en todas las veredas aunque
// las UV del pack de la ciudad tengan escalas distintas en cada eje.
// _TileWorldM = metros que cubre una repeticion de la textura.
// Built-in RP, iluminacion Standard, compatible con single pass instanced (VR).
// ============================================================
Shader "SimBici/WorldTile"
{
    Properties
    {
        _MainTex ("Baldosa", 2D) = "white" {}
        _TileWorldM ("Metros por repeticion", Float) = 2.0
        _Tint ("Tinte", Color) = (1, 1, 1, 1)
        _Glossiness ("Smoothness", Range(0, 1)) = 0.12
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma multi_compile_instancing
        #pragma target 3.0

        sampler2D _MainTex;
        float _TileWorldM;
        fixed4 _Tint;
        half _Glossiness;

        struct Input
        {
            float3 worldPos;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.worldPos.xz / _TileWorldM) * _Tint;
            o.Albedo = c.rgb;
            o.Metallic = 0;
            o.Smoothness = _Glossiness;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
