// Pancing/VertexLit plus a swimming wave and a wet-skin glint, for the fish.
// Vertex-coloured Lambert with fog.
//
// Every surface in this game that is not water uses this: the lake bed, the
// banks, the trees, the reeds, the rod, the line and the fish. There are no
// textures anywhere in the project, so a shader that reads colour from the mesh
// and lights it is the entire material system — which is also why the whole
// scene draws in a handful of calls on a phone.
//
// Two-sided, because the plant billboards and the fish fins are single-quad
// membranes that must be visible from behind. The normal is flipped for back
// faces so they light correctly instead of going flat black.

Shader "Pancing/Fish"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        _Ambient ("Extra ambient", Range(0,1)) = 0.32
        _SwimAmp ("Swim amplitude (body lengths)", Range(0,0.3)) = 0.04
        _SwimFreq ("Tail beats, rad/s", Range(0,40)) = 7
        _Sheen ("Wet sheen", Range(0,1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100
        Cull Off

        Pass
        {
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_fwdbase

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            fixed4 _Color;
            float _Ambient, _SwimAmp, _SwimFreq, _Sheen;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                fixed4 color  : COLOR;
                float3 normal : TEXCOORD0;
                float3 world  : TEXCOORD1;
                UNITY_FOG_COORDS(2)
                LIGHTING_COORDS(3, 4)
            };

            v2f vert(appdata v)
            {
                v2f o;
                // Swimming: a travelling wave down the body, growing toward the
                // tail. The mesh runs snout z=0 -> tail root z=1, so z is "along".
                float along = saturate(v.vertex.z);
                float wave = sin(_Time.y * _SwimFreq - v.vertex.z * 5.5);
                v.vertex.x += wave * _SwimAmp * along * along;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;
                // Vertex colours are authored as sRGB hex values. In a Linear
                // colour-space project they must be decoded, or every surface
                // renders washed out (that was the whole game's look until now).
                #ifndef UNITY_COLORSPACE_GAMMA
                o.color.rgb = GammaToLinearSpace(o.color.rgb);
                #endif
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                UNITY_TRANSFER_FOG(o, o.pos);
                TRANSFER_VERTEX_TO_FRAGMENT(o);
                return o;
            }

            fixed4 frag(v2f i, fixed facing : VFACE) : SV_Target
            {
                // Flip the normal on back faces or the two-sided quads light as if
                // they were all pointing one way, which reads as a hole.
                float3 n = normalize(i.normal) * (facing > 0 ? 1 : -1);
                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);

                float ndl = saturate(dot(n, lightDir));
                // Half-Lambert: fully unlit surfaces on a foliage billboard read as
                // silhouettes, and a lake bank has too much of it to allow that.
                float wrapped = ndl * 0.7 + 0.3;

                float atten = LIGHT_ATTENUATION(i);
                fixed3 diffuse = _LightColor0.rgb * wrapped * atten;
                fixed3 ambient = ShadeSH9(float4(n, 1)) + _Ambient;

                fixed4 col = i.color;
                col.rgb *= diffuse + ambient;
                // Wet skin: a tight specular glint. Fish without it read as plastic.
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.world);
                float spec = pow(saturate(dot(n, normalize(lightDir + viewDir))), 48.0);
                col.rgb += _LightColor0.rgb * spec * _Sheen * atten;
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }

    Fallback "Legacy Shaders/Diffuse"
}
