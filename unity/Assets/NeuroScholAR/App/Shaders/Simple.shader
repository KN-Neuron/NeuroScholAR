// Flat-shaded colour + emission for the palace objects. Replaces Unity's Standard shader, which has tens of
// thousands of variants (slow builds, runtime compile stalls on the Beam Pro) for lighting features that
// primitive shapes on see-through glasses don't need. Fixed light direction: no dependency on scene lights.
// Single-pass instanced stereo safe (XREAL renders both eyes in one pass). Opaque by default; set _SrcBlend /
// _DstBlend / _ZWrite (see Interactor.CreateHandMesh) for a see-through material.
Shader "NeuroScholAR/Simple"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _EmissionColor ("Emission", Color) = (0, 0, 0, 1)
        [HideInInspector] _SrcBlend ("Src Blend", Float) = 1
        [HideInInspector] _DstBlend ("Dst Blend", Float) = 0
        [HideInInspector] _ZWrite ("Z Write", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Color;
            fixed4 _EmissionColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed3 color : COLOR0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 n = UnityObjectToWorldNormal(v.normal);
                float light = saturate(dot(n, normalize(float3(0.3, 1.0, -0.4))));
                o.color = _Color.rgb * (0.45 + 0.55 * light) + _EmissionColor.rgb;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(i.color, _Color.a);
            }
            ENDCG
        }
    }
}
