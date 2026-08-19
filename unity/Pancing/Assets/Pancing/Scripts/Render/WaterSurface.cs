using UnityEngine;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.Render
{
    /// <summary>
    /// The water plane: mesh, bathymetry bake, wave uniforms and the ripple ring
    /// buffer. Pairs with Resources/PancingWater.shader.
    ///
    /// The C# side owns two things the shader cannot: the baked depth texture
    /// (evaluated from the same DepthAt the catch table uses) and HeightAt(),
    /// which must reproduce the vertex shader's wave sum exactly so that floating
    /// objects sit on the surface they appear to be on.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class WaterSurface : MonoBehaviour
    {
        private const int MaxRipples = 12;
        /// <summary>Grid resolution across the plane. 128 is where the Gerstner
        /// displacement stops visibly stepping at the camera heights this game uses.</summary>
        private const int GridHigh = 128;
        private const int GridLow = 56;
        private const int DepthMapSize = 128;

        private Material _mat;
        private Texture2D _depthMap;
        private readonly Vector4[] _ripples = new Vector4[MaxRipples];
        private int _rippleCursor;

        private float _minX, _minZ, _spanX, _spanZ;
        private float _wind = 0.4f, _chop = 0.6f;
        private float _time;

        private static readonly int IdRipples = Shader.PropertyToID("_Ripples");
        private static readonly int IdBounds = Shader.PropertyToID("_Bounds");
        private static readonly int IdDepthMap = Shader.PropertyToID("_DepthMap");
        private static readonly int IdMaxDepth = Shader.PropertyToID("_MaxDepth");
        private static readonly int IdWind = Shader.PropertyToID("_Wind");
        private static readonly int IdChop = Shader.PropertyToID("_Chop");
        private static readonly int IdClarity = Shader.PropertyToID("_Clarity");
        private static readonly int IdLight = Shader.PropertyToID("_Light");
        private static readonly int IdShallow = Shader.PropertyToID("_ShallowColor");
        private static readonly int IdDeep = Shader.PropertyToID("_DeepColor");
        private static readonly int IdFoam = Shader.PropertyToID("_FoamColor");
        private static readonly int IdSky = Shader.PropertyToID("_SkyTint");

        public Material Material => _mat;

        public static WaterSurface Create(Transform parent, Spot spot, bool highQuality)
        {
            var go = new GameObject("Water");
            go.transform.SetParent(parent, false);
            var water = go.AddComponent<WaterSurface>();
            water.Build(spot, highQuality);
            return water;
        }

        private void Build(Spot spot, bool highQuality)
        {
            // The plane covers the fishable box plus a margin, so the far edge is
            // off-screen rather than a visible seam.
            _minX = -Game.HalfWidth - 8f;
            _minZ = -3f;
            _spanX = (Game.HalfWidth + 8f) * 2f;
            _spanZ = Game.MaxCast + 20f;

            int n = highQuality ? GridHigh : GridLow;
            GetComponent<MeshFilter>().sharedMesh = BuildGrid(n);

            var shader = Shader.Find("Pancing/Water");
            if (shader == null)
            {
                Debug.LogError("[Pancing] Pancing/Water shader missing — falling back to flat water.");
                shader = Shader.Find("Unlit/Color");
            }
            _mat = new Material(shader) { name = "WaterMaterial" };

            var mr = GetComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            BakeDepthMap(spot);
            ApplyPalette(spot);

            _mat.SetVector(IdBounds, new Vector4(_minX, _minZ, _spanX, _spanZ));
            _mat.SetFloat(IdMaxDepth, (float)spot.MaxDepth);
            _mat.SetFloat(IdClarity, (float)spot.WaterClarity);

            Game.SurfaceHeight = HeightAt;
        }

        private Mesh BuildGrid(int n)
        {
            var verts = new Vector3[(n + 1) * (n + 1)];
            var uvs = new Vector2[verts.Length];
            var tris = new int[n * n * 6];

            for (int j = 0; j <= n; j++)
            {
                for (int i = 0; i <= n; i++)
                {
                    float fx = i / (float)n, fz = j / (float)n;
                    int idx = j * (n + 1) + i;
                    verts[idx] = new Vector3(_minX + fx * _spanX, 0f, _minZ + fz * _spanZ);
                    uvs[idx] = new Vector2(fx, fz);
                }
            }

            int t = 0;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i;
                    int b = a + 1;
                    int c = a + (n + 1);
                    int d = c + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }

            var mesh = new Mesh { name = "WaterGrid" };
            // A 128×128 grid is 16 641 vertices — under the 16-bit limit, but only
            // just, and the low-quality tier is not. 32-bit indices cost nothing on
            // anything that can run this shader.
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            // The vertex shader moves everything, so a tight bound would get the
            // plane culled the moment a wave lifts it out of its rest box.
            mesh.bounds = new Bounds(
                new Vector3(_minX + _spanX * 0.5f, 0f, _minZ + _spanZ * 0.5f),
                new Vector3(_spanX, 12f, _spanZ));
            return mesh;
        }

        /// <summary>
        /// Bake the bathymetry into a texture the vertex shader can sample.
        ///
        /// One source of truth: this samples Game.DepthAtWorld, which is the same
        /// spot.DepthAt() the catch table scores against. The water therefore
        /// cannot be deep where the ground is high, and it ends exactly where the
        /// ground rises out of it — no separate shoreline mask to keep in step.
        /// </summary>
        private void BakeDepthMap(Spot spot)
        {
            _depthMap = new Texture2D(DepthMapSize, DepthMapSize, TextureFormat.RFloat, false, true)
            {
                name = "Bathymetry",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            float maxDepth = Mathf.Max(0.1f, (float)spot.MaxDepth);
            var pixels = new Color[DepthMapSize * DepthMapSize];
            for (int j = 0; j < DepthMapSize; j++)
            {
                float fz = j / (float)(DepthMapSize - 1);
                float z = _minZ + fz * _spanZ;
                for (int i = 0; i < DepthMapSize; i++)
                {
                    float fx = i / (float)(DepthMapSize - 1);
                    float x = _minX + fx * _spanX;
                    float d = Game.DepthAtWorld(x, z) / maxDepth;
                    pixels[j * DepthMapSize + i] = new Color(d, d, d, 1f);
                }
            }
            _depthMap.SetPixels(pixels);
            _depthMap.Apply(false, false);
            _mat.SetTexture(IdDepthMap, _depthMap);
        }

        private void ApplyPalette(Spot spot)
        {
            var p = spot.Palette;
            _mat.SetColor(IdShallow, ProcNoise.HexToColor(p.Shallow));
            _mat.SetColor(IdDeep, ProcNoise.HexToColor(p.Deep));
            _mat.SetColor(IdFoam, ProcNoise.HexToColor(p.Foam));
            _mat.SetColor(IdSky, ProcNoise.HexToColor(p.Sky != null && p.Sky.Length > 0 ? p.Sky[0] : "#8fb8d8"));
        }

        public void SetSpot(Spot spot)
        {
            BakeDepthMap(spot);
            ApplyPalette(spot);
            _mat.SetFloat(IdMaxDepth, (float)spot.MaxDepth);
            _mat.SetFloat(IdClarity, (float)spot.WaterClarity);
        }

        /// <summary>Wind and chop from the world clock; light from the sun arc.</summary>
        public void SetConditions(float wind, float chop, float light)
        {
            _wind = wind;
            _chop = chop;
            _mat.SetFloat(IdWind, wind);
            _mat.SetFloat(IdChop, chop);
            _mat.SetFloat(IdLight, light);
        }

        /// <summary>Drop a decaying radial wave packet at a world point.</summary>
        public void Ripple(Vector3 worldPos, float strength)
        {
            if (strength <= 0.001f) return;
            _ripples[_rippleCursor] = new Vector4(worldPos.x, worldPos.z, _time, Mathf.Clamp01(strength));
            _rippleCursor = (_rippleCursor + 1) % MaxRipples;
            _mat.SetVectorArray(IdRipples, _ripples);
        }

        private void Update()
        {
            // _Time.y drives the shader; mirroring it here keeps HeightAt in step
            // with what is actually on screen.
            _time = Time.time;
        }

        /// <summary>
        /// Surface height at a world point, matching the vertex shader's waves.
        ///
        /// MUST stay in lockstep with the four gerstner() calls in the shader. If
        /// it drifts, the float and the fish sit off the surface they appear to be
        /// on — which looks like a physics bug and is actually a copy-paste bug.
        /// </summary>
        public float HeightAt(float x, float z)
        {
            float depth = Game.DepthAtWorld(x, z);
            float shoal = ProcNoise.SmoothStep(0f, 0.9f, depth);
            float amp = (0.35f + _wind * 0.55f) * shoal;
            float steep = (0.055f + _chop * 0.055f) * shoal;
            if (amp <= 0.0001f) return 0f;

            float y = 0f;
            y += Wave(x, z, 1.00f, 0.22f, 1.00f, 9.40f * amp, 1.00f, steep);
            y += Wave(x, z, 0.62f, -0.78f, 0.62f, 5.10f * amp, 1.18f, steep);
            y += Wave(x, z, -0.35f, 0.94f, 0.44f, 2.70f * amp, 1.42f, steep);
            y += Wave(x, z, 0.88f, 0.47f, 0.28f, 1.35f * amp, 1.75f, steep);
            return y;
        }

        private float Wave(float x, float z, float dx, float dz, float s,
                           float wavelength, float speed, float steep)
        {
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 1e-5f) return 0f;
            float k = 2f * Mathf.PI / Mathf.Max(wavelength, 0.001f);
            float c = Mathf.Sqrt(9.81f / k);
            float f = k * ((dx / len) * x + (dz / len) * z - c * speed * _time);
            return (steep * s / k) * Mathf.Sin(f);
        }

        private void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
            if (_depthMap != null) Destroy(_depthMap);
        }
    }
}
