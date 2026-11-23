using System.Collections.Generic;
using UnityEngine;

namespace OlliVR
{
    /// <summary>
    /// The view through the window, as real 3D: snowy rolling terrain, individual pine trees, a distant mountain
    /// range and a sky dome around all of it (so no direction is left uncovered). Lit by moon and sky light at
    /// build time into vertex colours, with atmospheric haze over distance.
    /// </summary>
    internal partial class CozyRoom
    {
        /// <summary>Opaque scenery outside (terrain, trees, mountains, sky), drawn with depth like the room.</summary>
        public readonly List<Part> OutsideOpaque = new();

        // Moon low over the scene, a little to the left of straight out of the window.
        static readonly Vector3 MoonDir = new Vector3(Mathf.Cos(14f * Mathf.Deg2Rad) * Mathf.Cos(18f * Mathf.Deg2Rad), Mathf.Sin(14f * Mathf.Deg2Rad),
                                                      Mathf.Cos(14f * Mathf.Deg2Rad) * Mathf.Sin(18f * Mathf.Deg2Rad)).normalized;
        static readonly Color MoonLight = new(0.42f, 0.47f, 0.60f);
        static readonly Color SkyLight = new(0.10f, 0.13f, 0.24f);
        static readonly Color Horizon = new(0.11f, 0.12f, 0.22f);
        const float SkyR = 2400f;

        float outFloor;

        void BuildOutside()
        {
            foreach (var p in OutsideOpaque) Object.Destroy(p.Mesh);
            OutsideOpaque.Clear();
            outFloor = Floor - 0.6f;   // the lodge stands a little above the snow
            var wc = WindowCenter;

            var land = new MeshBuilder(this, RoomTextures.White);
            BuildTerrain(land, wc);
            var trees = new MeshBuilder(this, RoomTextures.White);
            BuildTrees(trees, wc);
            var mountains = new MeshBuilder(this, RoomTextures.White);
            BuildMountains(mountains);
            var sky = new MeshBuilder(this, RoomTextures.NightSky);
            BuildSkyDome(sky);

            foreach (var b in new[] { land, trees, mountains, sky })
                if (b.Count > 0) OutsideOpaque.Add(new Part { Mesh = b.ToMeshUnlit(), Tex = b.Tex });
        }

        // ---- lighting and noise -------------------------------------------------------------------------

        static Color Lit(Color albedo, Vector3 n, float dist, float occlusion = 1f)
        {
            float ndl = Mathf.Max(0f, Vector3.Dot(n, MoonDir));
            float sky = 0.55f + 0.45f * n.y;
            var c = new Color(albedo.r * (SkyLight.r * sky * occlusion + MoonLight.r * ndl),
                              albedo.g * (SkyLight.g * sky * occlusion + MoonLight.g * ndl),
                              albedo.b * (SkyLight.b * sky * occlusion + MoonLight.b * ndl));
            float haze = 1f - Mathf.Exp(-dist / 900f);
            return Color.Lerp(c, Horizon, haze * 0.85f);
        }

        static float Hash2(int x, int z)
        {
            unchecked
            {
                int h = x * 374761393 + z * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xffffff) / (float)0xffffff;
            }
        }

        static float VNoise(float x, float z)
        {
            int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
            float fx = x - x0, fz = z - z0;
            fx = fx * fx * (3 - 2 * fx); fz = fz * fz * (3 - 2 * fz);
            float a = Hash2(x0, z0), b = Hash2(x0 + 1, z0), c = Hash2(x0, z0 + 1), d = Hash2(x0 + 1, z0 + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
        }

        static float Fbm2(float x, float z, int oct)
        {
            float s = 0, amp = 0.5f, norm = 0;
            for (int i = 0; i < oct; i++) { s += amp * VNoise(x, z); norm += amp; amp *= 0.5f; x *= 2.03f; z *= 2.03f; }
            return s / norm;
        }

        /// <summary>Snow surface height outside: flat near the lodge, rolling hills further out.</summary>
        float GroundY(float x, float z)
        {
            float t = Mathf.Max(0f, x - RoomHalfW);
            float hills = (Fbm2(x / 60f, z / 60f, 4) - 0.45f) * 22f;
            float near = Mathf.SmoothStep(0f, 1f, t / 40f);
            float swell = (Fbm2(x / 9f + 7f, z / 9f, 3) - 0.5f) * 0.8f * Mathf.SmoothStep(0f, 1f, t / 6f);
            return outFloor + hills * near + swell;
        }

        // ---- terrain ------------------------------------------------------------------------------------

        void BuildTerrain(MeshBuilder b, Vector3 wc)
        {
            // Rows outwards from the wall with growing spacing; columns across, denser in front of the window.
            var xs = new List<float>();
            for (float s = 0.4f, x = RoomHalfW + 0.2f; x < RoomHalfW + 700f; x += s, s *= 1.07f) xs.Add(x);
            var zs = new List<float>();
            for (float s = 0.4f, z = 0f; z < 900f; z += s, s *= 1.07f) { zs.Add(wc.z + z); if (z > 0) zs.Insert(0, wc.z - z); }
            int nx = xs.Count, nz = zs.Count;
            var P = new Vector3[nx, nz];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                    P[i, j] = new Vector3(xs[i], GroundY(xs[i], zs[j]), zs[j]);
            int baseIdx = b.Count;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                {
                    var du = P[Mathf.Min(nx - 1, i + 1), j] - P[Mathf.Max(0, i - 1), j];
                    var dv = P[i, Mathf.Min(nz - 1, j + 1)] - P[i, Mathf.Max(0, j - 1)];
                    var n = Vector3.Cross(dv, du).normalized;
                    if (n.y < 0) n = -n;
                    float sparkle = 0.94f + 0.06f * Hash2(i * 7, j * 13);
                    var snow = new Color(0.86f, 0.90f, 0.98f) * sparkle;
                    float dist = new Vector2(P[i, j].x, P[i, j].z - wc.z).magnitude;
                    b.Vert(P[i, j], n, Lit(snow, n, dist));
                }
            for (int i = 0; i < nx - 1; i++)
                for (int j = 0; j < nz - 1; j++)
                {
                    int a = baseIdx + i * nz + j;
                    b.Tri(a, a + nz, a + nz + 1);
                    b.Tri(a, a + nz + 1, a + 1);
                }
        }

        // ---- pine trees ---------------------------------------------------------------------------------

        void BuildTrees(MeshBuilder b, Vector3 wc)
        {
            var rng = new System.Random(77);
            int placed = 0;
            for (int attempt = 0; attempt < 4000 && placed < 260; attempt++)
            {
                // Distance biased towards near, inside the window's view cone (with margin for head movement).
                float dist = 7f + Mathf.Pow((float)rng.NextDouble(), 1.6f) * 320f;
                float az = ((float)rng.NextDouble() * 2f - 1f) * 70f * Mathf.Deg2Rad;
                float x = RoomHalfW + Mathf.Cos(az) * dist, z = wc.z + Mathf.Sin(az) * dist;
                // Keep a clearing right in front of the window.
                if (dist < 22f && Mathf.Abs(az) < 25f * Mathf.Deg2Rad) continue;
                // Clumps: thin out where a low-frequency noise says "meadow".
                if (Fbm2(x / 35f + 3f, z / 35f, 3) < 0.42f) continue;
                float h = 6f + (float)rng.NextDouble() * 9f;
                PineTree(b, new Vector3(x, GroundY(x, z) - 0.2f, z), h, h * (0.26f + 0.06f * (float)rng.NextDouble()), rng, dist);
                placed++;
            }
        }

        void PineTree(MeshBuilder b, Vector3 root, float h, float radius, System.Random rng, float dist)
        {
            int seg = dist < 40f ? 14 : dist < 120f ? 9 : 6;
            // Trunk.
            var bark = Lit(new Color(0.16f, 0.10f, 0.07f), Vector3.right, dist);
            b.ConeColoured(root, 0.05f * h / 8f + 0.08f, 0.04f, h * 0.35f, seg, (p, n) => bark);
            // Tiers of drooping branches, each a jagged cone with snow on its upper side.
            int tiers = 7 + (int)(h / 2.5f);
            float twist = (float)rng.NextDouble() * 6.28f;
            for (int t = 0; t < tiers; t++)
            {
                float f = t / (float)tiers;
                float y0 = h * (0.18f + 0.74f * f);
                float tierH = h * (0.22f - 0.1f * f);
                float r = radius * (1f - 0.85f * f) * (0.9f + 0.2f * (float)rng.NextDouble());
                float snowAmt = 0.35f + 0.45f * (float)rng.NextDouble();
                b.ConeColoured(root + Vector3.up * y0, r, 0f, tierH, seg, (p, n) =>
                {
                    // Snow sits on the upward-facing, outer parts of the branches.
                    // Only the flattest, outermost tops hold snow; the drooping tips and undersides stay dark.
                    float rim = Mathf.Clamp01((p.y - (root.y + y0)) / Mathf.Max(0.01f, tierH) * 1.4f);
                    float up = Mathf.Clamp01((n.y - 0.55f) * 3f) * snowAmt * (0.35f + 0.65f * rim);
                    var needle = new Color(0.035f, 0.075f, 0.055f) * (0.8f + 0.4f * Hash2((int)(p.x * 9f), (int)(p.z * 9f)));
                    var snow = new Color(0.82f, 0.86f, 0.95f);
                    var alb = Color.Lerp(needle, snow, up);
                    return Lit(alb, n, dist, 0.85f);
                }, jag: 0.22f, twist: twist + t * 0.7f);
            }
        }

        // ---- distant mountain range ---------------------------------------------------------------------

        void BuildMountains(MeshBuilder b)
        {
            const int NX = 70, NZ = 140;
            float x0 = RoomHalfW + 520f, x1 = RoomHalfW + 1500f, z0 = -1500f, z1 = 1500f;
            var P = new Vector3[NX + 1, NZ + 1];
            for (int i = 0; i <= NX; i++)
                for (int j = 0; j <= NZ; j++)
                {
                    float x = Mathf.Lerp(x0, x1, i / (float)NX), z = Mathf.Lerp(z0, z1, j / (float)NZ);
                    // Ridged noise: sharp crests, rising with distance into a high range.
                    float ridge = 0f, amp = 1f, fx = x / 420f, fz = z / 420f, norm = 0f;
                    for (int o = 0; o < 5; o++)
                    {
                        float nval = 1f - Mathf.Abs(VNoise(fx, fz) * 2f - 1f);
                        ridge += nval * nval * amp; norm += amp;
                        amp *= 0.5f; fx *= 2.1f; fz *= 2.1f;
                    }
                    ridge /= norm;
                    float rise = Mathf.SmoothStep(0f, 1f, (x - x0) / (x1 - x0) * 1.6f);
                    float y = outFloor - 20f + ridge * 520f * rise + 40f;
                    P[i, j] = new Vector3(x, y, z);
                }
            int baseIdx = b.Count;
            for (int i = 0; i <= NX; i++)
                for (int j = 0; j <= NZ; j++)
                {
                    var du = P[Mathf.Min(NX, i + 1), j] - P[Mathf.Max(0, i - 1), j];
                    var dv = P[i, Mathf.Min(NZ, j + 1)] - P[i, Mathf.Max(0, j - 1)];
                    var n = Vector3.Cross(dv, du).normalized;
                    if (n.y < 0) n = -n;
                    float alt = P[i, j].y - outFloor;
                    bool snow = n.y > 0.62f - Mathf.Clamp01((alt - 150f) / 300f) * 0.25f + (Hash2(i * 3, j * 5) - 0.5f) * 0.12f;
                    var alb = snow ? new Color(0.84f, 0.88f, 0.97f) : new Color(0.20f, 0.21f, 0.25f);
                    float dist = new Vector2(P[i, j].x, P[i, j].z).magnitude;
                    b.Vert(P[i, j], n, Lit(alb, n, dist));
                }
            for (int i = 0; i < NX; i++)
                for (int j = 0; j < NZ; j++)
                {
                    int a = baseIdx + i * (NZ + 1) + j;
                    b.Tri(a, a + NZ + 1, a + NZ + 2);
                    b.Tri(a, a + NZ + 2, a + 1);
                }
        }

        // ---- sky ----------------------------------------------------------------------------------------

        /// <summary>
        /// A dome over the outside (the half facing the window, from below the horizon to the zenith), mapped so
        /// the sky texture's horizon sits at the horizon and its moon in MoonDir.
        /// </summary>
        void BuildSkyDome(MeshBuilder b)
        {
            const int NA = 48, NE = 32;
            float az0 = -100f, az1 = 100f, e0 = -25f, e1 = 90f;
            int baseIdx = b.Count;
            for (int j = 0; j <= NE; j++)
                for (int i = 0; i <= NA; i++)
                {
                    float az = Mathf.Lerp(az0, az1, i / (float)NA) * Mathf.Deg2Rad;
                    float el = Mathf.Lerp(e0, e1, j / (float)NE) * Mathf.Deg2Rad;
                    var dir = new Vector3(Mathf.Cos(el) * Mathf.Cos(az), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Sin(az));
                    // Texture: u across azimuth, v from the horizon (v=0.12) up; the moon is drawn at (0.56, 0.22).
                    float u = 0.5f + az / (Mathf.PI * 1.25f);
                    float v = 0.12f + Mathf.Max(-0.12f, el / (Mathf.PI / 2f)) * 0.88f;
                    b.Vert(dir * SkyR, -dir, Color.white, new Vector2(u, v));
                }
            for (int j = 0; j < NE; j++)
                for (int i = 0; i < NA; i++)
                {
                    int a = baseIdx + j * (NA + 1) + i;
                    b.Tri(a, a + 1, a + NA + 2);
                    b.Tri(a, a + NA + 2, a + NA + 1);
                }
        }
    }
}
