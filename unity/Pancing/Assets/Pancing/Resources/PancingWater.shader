// Pancing water.
//
// Gerstner waves displace real vertices, with normals derived analytically from
// the wave derivatives rather than sampled from a height texture — that is what
// keeps the lighting correct on a surface that is actually moving, and it costs
// three extra multiply-adds per wave instead of a texture fetch.
//
// Fresnel is Schlick: at grazing angles water is a mirror, looking straight down
// it is a window. Getting that term right does more for realism than anything
// else in here.
//
// The depth term does not come from a depth buffer. It is baked off the same
// DepthAt() the catch table scores against (see Game.DepthAtWorld), so the water
// cannot be deep where the ground is high, it ends exactly where the ground
// rises out of it, and the shallows you can see are the shallows you are fishing.
//
// Built-in render pipeline on purpose: no SRP asset to configure, one pass, and
// it compiles down to something a five-year-old Android phone will run.

Shader "Pancing/Water"
{
    Properties
    {
        _ShallowColor ("Shallow", Color) = (0.50, 0.68, 0.58, 1)
        _DeepColor    ("Deep",    Color) = (0.07, 0.19, 0.17, 1)
        _FoamColor    ("Foam",    Color) = (0.91, 0.95, 0.92, 1)
        _SkyTint      ("Sky tint", Color) = (0.55, 0.72, 0.85, 1)

        _DepthMap ("Bathymetry (R = depth)", 2D) = "black" {}
        _ReflectionTex ("Planar reflection", 2D) = "black" {}

        _MaxDepth ("Max depth (m)", Float) = 4.0
        _Wind ("Wind", Range(0,3)) = 0.4
        _Chop ("Chop", Range(0,3)) = 0.6
        _Clarity ("Clarity", Range(0,1)) = 0.5
        _Light ("Light level", Range(0,2)) = 1.0
        _ReflectionAmount ("Reflection amount", Range(0,1)) = 0.65
        _Glossiness ("Sun glint", Range(1,512)) = 180
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-100" "IgnoreProjector"="True" }
        LOD 200
        Cull Back
        ZWrite On
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ PANCING_REFLECTION
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            #define MAX_RIPPLES 12

            sampler2D _DepthMap;
            sampler2D _ReflectionTex;

            fixed4 _ShallowColor, _DeepColor, _FoamColor, _SkyTint;
            float _MaxDepth, _Wind, _Chop, _Clarity, _Light, _ReflectionAmount, _Glossiness;

            // xy = world xz, z = birth time, w = strength
            float4 _Ripples[MAX_RIPPLES];
            // minX, minZ, spanX, spanZ of the water plane in world space
            float4 _Bounds;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos     : SV_POSITION;
                float3 world   : TEXCOORD0;
                float3 normal  : TEXCOORD1;
                float  depth   : TEXCOORD2;
                float  crest   : TEXCOORD3;
                float4 screen  : TEXCOORD4;
                UNITY_FOG_COORDS(5)
            };

            // One Gerstner wave. Returns the displacement and accumulates the
            // surface tangent derivatives, so the normal stays exact under
            // displacement instead of being an approximation of a flat plane.
            float3 gerstner(float2 pos, float2 dir, float steep, float wavelength,
                            float speed, float t, inout float3 tangent, inout float3 binormal)
            {
                float k = 6.28318530718 / max(wavelength, 0.001);
                float c = sqrt(9.81 / k);
                float2 d = normalize(dir);
                float f = k * (dot(d, pos) - c * speed * t);
                float a = steep / k;

                tangent += float3(
                    -d.x * d.x * steep * sin(f),
                     d.x * steep * cos(f),
                    -d.x * d.y * steep * sin(f));
                binormal += float3(
                    -d.x * d.y * steep * sin(f),
                     d.y * steep * cos(f),
                    -d.y * d.y * steep * sin(f));

                return float3(d.x * a * cos(f), a * sin(f), d.y * a * cos(f));
            }

            v2f vert(appdata v)
            {
                v2f o;
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                float t = _Time.y;

                // Depth under this vertex, from the bathymetry baked off the same
                // function the simulation uses.
                float2 duv = float2((world.x - _Bounds.x) / _Bounds.z,
                                    (world.z - _Bounds.y) / _Bounds.w);
                duv = saturate(duv);
                float depthN = tex2Dlod(_DepthMap, float4(duv, 0, 0)).r;
                o.depth = depthN * _MaxDepth;

                // Waves die out in the shallows: a swell cannot be taller than the
                // water it is standing in. This is what makes the margins settle
                // down naturally instead of needing a hand-painted shore mask.
                float shoal = smoothstep(0.0, 0.9, o.depth);
                // Wave height is steepness * wavelength / 2pi, so these two together
                // give roughly 8 cm of chop in a light breeze and 40 cm in a storm —
                // lake water, not open ocean.
                float amp   = (0.35 + _Wind * 0.55) * shoal;
                float steep = (0.055 + _Chop * 0.055) * shoal;

                float3 tangent  = float3(1, 0, 0);
                float3 binormal = float3(0, 0, 1);
                float3 disp = 0;

                disp += gerstner(world.xz, float2( 1.00,  0.22), steep * 1.00, 9.40 * amp, 1.00, t, tangent, binormal);
                disp += gerstner(world.xz, float2( 0.62, -0.78), steep * 0.62, 5.10 * amp, 1.18, t, tangent, binormal);
                disp += gerstner(world.xz, float2(-0.35,  0.94), steep * 0.44, 2.70 * amp, 1.42, t, tangent, binormal);
                disp += gerstner(world.xz, float2( 0.88,  0.47), steep * 0.28, 1.35 * amp, 1.75, t, tangent, binormal);

                // --- dynamic ripples ------------------------------------------
                // Each source is a decaying radial wave packet. Cheap, and it is
                // what makes the lure landing feel like it hit something real.
                [unroll]
                for (int i = 0; i < MAX_RIPPLES; i++)
                {
                    float4 r = _Ripples[i];
                    if (r.w <= 0.001) continue;
                    float age = t - r.z;
                    if (age < 0.0 || age > 3.2) continue;

                    float dist  = distance(world.xz, r.xy);
                    float front = age * 2.6;                        // expanding ring
                    float band  = exp(-pow((dist - front) * 1.7, 2.0));
                    float decay = exp(-age * 1.15) * exp(-dist * 0.10);
                    float h = sin((dist - front) * 7.0) * band * decay * r.w * 0.30;

                    disp.y += h;
                    // Perturb the tangents too, or the ripple is a bump that does
                    // not catch the light and reads as a texture glitch.
                    float2 rd = normalize(world.xz - r.xy + 1e-5);
                    float slope = cos((dist - front) * 7.0) * band * decay * r.w * 2.1;
                    tangent.y  += rd.x * slope;
                    binormal.y += rd.y * slope;
                }

                world += disp;
                o.world = world;
                o.pos = UnityWorldToClipPos(float4(world, 1.0));
                o.screen = ComputeScreenPos(o.pos);

                float3 n = normalize(cross(binormal, tangent));
                o.normal = n.y < 0 ? -n : n;

                // How close this point is to a breaking crest — drives foam.
                o.crest = saturate(disp.y / max(amp * 0.45, 0.001));

                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.world);

                // --- body colour ---------------------------------------------
                // Turbid water goes opaque fast; clear water lets you see the bed.
                // Clarity scales how many metres it takes to reach the deep colour.
                float clarityRange = lerp(0.8, 4.5, _Clarity);
                float depthMix = saturate(i.depth / clarityRange);
                fixed4 body = lerp(_ShallowColor, _DeepColor, depthMix);

                // --- Fresnel --------------------------------------------------
                // Schlick, with water's real F0 of about 0.02.
                float cosTheta = saturate(dot(n, viewDir));
                float fresnel = 0.02 + 0.98 * pow(1.0 - cosTheta, 5.0);

                // --- reflection ------------------------------------------------
                fixed3 reflected = _SkyTint.rgb;
                #ifdef PANCING_REFLECTION
                    float2 ruv = i.screen.xy / max(i.screen.w, 1e-4);
                    // Distort the reflection by the wave normal, or it reads as a
                    // mirror lying flat under a moving surface.
                    ruv += n.xz * 0.035;
                    fixed3 planar = tex2D(_ReflectionTex, saturate(ruv)).rgb;
                    reflected = lerp(_SkyTint.rgb, planar, _ReflectionAmount);
                #endif

                fixed3 col = lerp(body.rgb, reflected, fresnel);

                // --- sun glint -------------------------------------------------
                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                float3 halfDir = normalize(lightDir + viewDir);
                float spec = pow(saturate(dot(n, halfDir)), _Glossiness);
                col += _LightColor0.rgb * spec * 0.9 * _Light;

                // --- foam --------------------------------------------------------
                // A narrow line where the bed comes up, plus the crests.
                //
                // The first version faded foam in across the whole first 35 cm of
                // depth, which on a gently shelving pond is several metres of water
                // and read as a white smear over the entire near margin. Foam wants
                // to be a LINE, so it is a band with an edge on both sides.
                float shoreFoam = smoothstep(0.03, 0.10, i.depth) * (1.0 - smoothstep(0.10, 0.24, i.depth));
                float crestFoam = smoothstep(0.80, 1.0, i.crest) * smoothstep(0.2, 1.2, _Chop);
                float foam = saturate(shoreFoam * 0.7 + crestFoam * 0.45);
                col = lerp(col, _FoamColor.rgb, foam * 0.65);

                col *= lerp(0.35, 1.0, saturate(_Light));

                // The water fades out entirely where there is no water, which is how
                // the shoreline gets its shape from the bathymetry rather than from
                // the mesh edge.
                float alpha = smoothstep(0.0, 0.10, i.depth);

                fixed4 outCol = fixed4(col, alpha);
                UNITY_APPLY_FOG(i.fogCoord, outCol);
                return outCol;
            }
            ENDCG
        }
    }

    // Anything that cannot compile the above still gets water, just flat.
    Fallback "Transparent/Diffuse"
}
