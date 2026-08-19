// Sky dome gradient.
//
// Unlit, depth-write off, drawn first: it is a backdrop, not geometry. The
// gradient runs on the dome's V coordinate, which BuildDome biases toward the
// horizon because that is where all the interesting colour is at dawn and dusk.
//
// A dome rather than a skybox material because the two colours change every
// frame with the clock, and setting two shader properties beats swapping or
// re-generating a skybox cubemap.

Shader "Pancing/SkyGradient"
{
    Properties
    {
        _TopColor ("Top", Color) = (0.56, 0.72, 0.85, 1)
        _HorizonColor ("Horizon", Color) = (0.87, 0.91, 0.93, 1)
        _Power ("Falloff", Range(0.2, 4)) = 1.1
    }

    SubShader
    {
        Tags { "RenderType"="Background" "Queue"="Background" }
        Cull Front       // we are inside the dome
        ZWrite Off
        ZTest LEqual
        Fog { Mode Off } // a backdrop must not be fogged into itself

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _TopColor, _HorizonColor;
            float _Power;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float height : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.height = saturate(v.uv.y);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float t = pow(i.height, _Power);
                return fixed4(lerp(_HorizonColor.rgb, _TopColor.rgb, t), 1.0);
            }
            ENDCG
        }
    }
}
