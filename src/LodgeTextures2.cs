// Managed stand-ins (see TexMath.cs): these generators run on a worker thread.
using Mathf = OlliVR.TexMath;
using Color = OlliVR.TexColor;
using Color32 = OlliVR.TexColor32;
using Vector3 = OlliVR.TV3;
using Texture2D = OlliVR.PendingTexture;

namespace OlliVR
{
    /// <summary>More lodge textures: individual board grain, leather, sheepskin and a painted landscape.</summary>
    internal static partial class RoomTextures
    {
        public static Texture2D BoardGrain, Leather, Sheepskin, Painting, Halo;

        static void GenerateLodge2(System.Random rng)
        {
            BoardGrain = MakeBoardGrain(rng);
            Leather = MakeLeather(rng);
            Sheepskin = MakeSheepskin();
            Painting = MakePainting(rng);
            Halo = Make(64, 64, true, (x, y) =>
            {
                // Soft radial falloff, premultiplied (it is drawn additively).
                float dx = (x + 0.5f) / 32f - 1f, dy = (y + 0.5f) / 32f - 1f;
                float r = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                float v = r * r * (0.4f + 0.6f * r);
                return C(v, v, v, v);
            });
        }

        /// <summary>
        /// Four different pine/oak boards stacked in v (each 1/4 of the height), grain running along u (tileable).
        /// Deep relief: growth-ring ridges, open pores, sunken knots with swirling grain around them.
        /// </summary>
        static Texture2D MakeBoardGrain(System.Random rng)
        {
            const int W = 1024, H = 512, Strip = H / 4;
            var tint = new Color[4];
            for (int i = 0; i < 4; i++)
            {
                float t = 0.86f + 0.26f * (float)rng.NextDouble();
                float warm = (float)rng.NextDouble() * 0.1f;
                tint[i] = new Color(t * (1f + warm), t, t * (1f - warm * 1.6f));
            }
            var knots = new Vector3[10];
            for (int i = 0; i < knots.Length; i++) knots[i] = new Vector3(rng.Next(W), (i % 4) * Strip + 20 + rng.Next(Strip - 40), 6 + rng.Next(12));
            return MakeWithHeight(W, H, false, 3.6f, (x, y) =>
            {
                int s = y / Strip;
                int ly = y % Strip;
                float warp = Fbm(x / 256f, y / 32f, W / 256, H / 32, 80 + s, 3);
                float streak = Fbm(x / 128f + warp * 2.5f, y / 4f, W / 128, H / 4, 82 + s * 3, 4);
                float ringPhase = ly * 0.16f + warp * 26f + streak * 5f;
                // Knots bend the grain around them.
                float knotDark = 1f, knotDip = 0f;
                foreach (var k in knots)
                {
                    if ((int)k.y / Strip != s) continue;
                    float dx = Mathf.DeltaAngle(0, (x - k.x) * 360f / W) * W / 360f, dy = y - k.y;
                    if (Mathf.Abs(dy) > 50 || Mathf.Abs(dx) > 90) continue;
                    float e = Mathf.Sqrt(dx * dx / (k.z * k.z * 3.2f) + dy * dy / (k.z * k.z));
                    ringPhase += 4f / (1f + e * e);
                    if (e < 1f) { knotDark *= 0.35f + 0.4f * e; knotDip = 0.4f * (1f - e); }
                }
                float rings = 0.5f + 0.5f * Mathf.Sin(ringPhase);
                float late = Mathf.Pow(rings, 3f);              // narrow dark latewood bands
                float pores = Hash(x / 2, y, 84 + s) > 0.88f ? 0.8f : 1f;
                var t = tint[s];
                float v = (0.62f + 0.2f * (1f - late) + 0.12f * streak) * pores * knotDark;
                float h = 0.7f + 0.22f * (1f - late) + 0.08f * streak - knotDip - (pores < 1f ? 0.12f : 0f);
                return (C(0.66f * v * t.r, 0.44f * v * t.g, 0.25f * v * t.b), h);
            });
        }

        /// <summary>Brown leather: pebble grain, soft creases, slightly worn lighter patches.</summary>
        static Texture2D MakeLeather(System.Random rng)
        {
            const int S = 512;
            return MakeWithHeight(S, S, false, 2.0f, (x, y) =>
            {
                float pebble = Fbm(x / 6f, y / 6f, S / 6 + 1, S / 6 + 1, 90, 2);
                float crease = Mathf.Abs(Fbm(x / 64f, y / 64f, S / 64, S / 64, 91, 3) - 0.5f);
                float wear = Fbm(x / 128f, y / 128f, S / 128, S / 128, 92, 3);
                float v = 0.75f + 0.25f * pebble;

                var c = Color.Lerp(new Color(0.36f, 0.19f, 0.10f), new Color(0.52f, 0.31f, 0.17f), wear * 0.8f) * v;
                float h = 0.6f + 0.3f * pebble + 0.25f * crease;
                return (C(c.r, c.g, c.b), h);
            });
        }

        /// <summary>Cream sheepskin: curly wool tufts with deep shading between them.</summary>
        static Texture2D MakeSheepskin()
        {
            const int S = 512;
            return MakeWithHeight(S, S, false, 1.1f, (x, y) =>
            {
                float curl = Fbm(x / 10f, y / 10f, S / 10 + 1, S / 10 + 1, 95, 3);
                float tuft = 0.5f + 0.5f * Mathf.Sin(curl * 18f);
                float v = 0.86f + 0.14f * tuft;
                float h = tuft * 0.8f + 0.2f * curl;
                return (C(1.0f * v, 0.96f * v, 0.88f * v), h);
            });
        }

        /// <summary>A small oil painting: alpine lake at dusk with snowy peaks and pines, brushy texture.</summary>
        static Texture2D MakePainting(System.Random rng)
        {
            const int W = 512, H = 384;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = x / (float)W, v = y / (float)H;
                    float brush = Fbm(x / 3f, y / 9f, W / 3 + 1, H / 9 + 1, 100, 2) - 0.5f;
                    var sky = Color.Lerp(new Color(0.95f, 0.66f, 0.45f), new Color(0.32f, 0.42f, 0.66f), Mathf.SmoothStep(0.45f, 1f, v));
                    float ridge = 0.55f + 0.18f * Mathf.Max(0f, 1f - Mathf.Abs(u - 0.35f) * 3.2f) + 0.12f * Mathf.Max(0f, 1f - Mathf.Abs(u - 0.72f) * 4f)
                                  + 0.03f * Fbm(u * 12f, 0, 12, 1, 101, 3);
                    Color c = sky;
                    if (v < ridge)
                    {
                        float below = ridge - v;
                        c = below < 0.06f + 0.05f * Fbm(u * 20f, v * 20f, 20, 20, 102, 2) ? new Color(0.92f, 0.86f, 0.84f) : new Color(0.34f, 0.32f, 0.42f);
                    }
                    float tree = 0.36f + 0.05f * Mathf.Abs(Mathf.Sin(u * 90f)) + 0.03f * Fbm(u * 30f, 0, 30, 1, 103, 2);
                    if (v < tree) c = new Color(0.10f, 0.18f, 0.14f);
                    if (v < 0.24f)
                    {
                        // Lake reflecting the sky and peaks.
                        float rv = 0.48f - v;
                        var refl = Color.Lerp(new Color(0.95f, 0.66f, 0.45f), new Color(0.32f, 0.42f, 0.66f), Mathf.SmoothStep(0.45f, 1f, rv));
                        c = Color.Lerp(refl, new Color(0.15f, 0.22f, 0.30f), 0.45f) * (0.9f + 0.2f * Mathf.Sin(y * 1.3f + x * 0.05f));
                    }
                    c = c * (1f + brush * 0.25f);
                    px[y * W + x] = C(c.r, c.g, c.b);
                }
            return Upload(W, H, true, px);
        }
    }
}
