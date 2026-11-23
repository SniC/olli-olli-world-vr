// Managed stand-ins (see TexMath.cs): these generators run on a worker thread.
using Mathf = OlliVR.TexMath;
using Color = OlliVR.TexColor;
using Color32 = OlliVR.TexColor32;
using Vector2 = OlliVR.TV2;
using Vector3 = OlliVR.TV3;
using Vector4 = OlliVR.TV4;
using Texture2D = OlliVR.PendingTexture;

namespace OlliVR
{
    /// <summary>Procedural textures for the wooden lodge and the mountain view outside it.</summary>
    internal static partial class RoomTextures
    {
        public static Texture2D Log, Stone, Plaid, Deck, NightSky, Mountains, Forest, NearTrees;

        static void GenerateLodge(System.Random rng)
        {
            Log = MakeLog(rng);
            Stone = MakeStone(rng);
            Plaid = MakePlaid();
            Deck = MakeDeck();
            NightSky = MakeNightSky(rng);
            Mountains = MakeMountains();
            Forest = MakeForest(rng);
            NearTrees = MakeNearTrees(rng);
        }

        /// <summary>Golden pine log surface: u along the log, v around it. Grain grooves, knots, drying cracks.</summary>
        static Texture2D MakeLog(System.Random rng)
        {
            const int W = 1024, H = 512;
            var knots = new Vector3[10];
            for (int i = 0; i < knots.Length; i++) knots[i] = new Vector3(rng.Next(W), 60 + rng.Next(H - 120), 10 + rng.Next(16));
            var cracks = new Vector4[7];
            for (int i = 0; i < cracks.Length; i++) cracks[i] = new Vector4(rng.Next(W), 80 + rng.Next(H - 160), 160 + rng.Next(400), (float)rng.NextDouble() * 6f);
            return MakeWithHeight(W, H, false, 4.0f, (x, y) =>
            {
                float grain = Fbm(x / 128f, y / 8f, W / 128, H / 8, 51, 4);
                float fibre = Fbm(x / 64f, y / 2f, W / 64, H / 2, 54, 2);
                float lines = 0.5f + 0.5f * Mathf.Sin(y * 0.3f + grain * 14f);
                float v = (0.80f + 0.16f * lines) * (0.9f + 0.12f * fibre);
                float h = 0.75f + 0.2f * lines + 0.05f * Hash(x, y, 53);
                foreach (var k in knots)
                {
                    float dx = Mathf.DeltaAngle(0, (x - k.x) * 360f / W) * W / 360f, dy = y - k.y;
                    float e = Mathf.Sqrt(dx * dx / (k.z * k.z * 2.2f) + dy * dy / (k.z * k.z));
                    if (e < 1f) { v *= 0.45f + 0.4f * e; h -= 0.25f * (1f - e); }
                    else if (e < 2.2f) v *= 0.9f + 0.1f * Mathf.Sin(e * 9f);
                }
                foreach (var cr in cracks)
                {
                    float along = Mod(x - (int)cr.x, W);
                    if (along > cr.z) continue;
                    float taper = Mathf.Sin(along / cr.z * Mathf.PI);
                    float cy = cr.y + 3f * Mathf.Sin(along * 0.03f + cr.w);
                    float d = Mathf.Abs(y - cy);
                    if (d < 2.2f * taper) { h = 0f; v *= 0.5f; }
                }
                v *= 0.92f + 0.08f * Hash(x, y, 52);
                return (C(0.80f * v, 0.56f * v, 0.33f * v), h);
            });
        }

        /// <summary>Height of the river stones (0 = mortar, 1 = top of a stone), tileable, for displacement.</summary>
        public static float[] StoneHeight;
        public const int StoneSize = 512;

        /// <summary>Bilinear sample of the stone height at texture coordinates (wrapping).</summary>
        public static float SampleStone(float u, float v)
        {
            if (StoneHeight == null) return 0f;
            float x = u * StoneSize - 0.5f, y = v * StoneSize - 0.5f;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            float S(int a, int b) => StoneHeight[Mod(b, StoneSize) * StoneSize + Mod(a, StoneSize)];
            return Mathf.Lerp(Mathf.Lerp(S(x0, y0), S(x0 + 1, y0), fx), Mathf.Lerp(S(x0, y0 + 1), S(x0 + 1, y0 + 1), fx), fy);
        }

        /// <summary>River-rock wall: rounded stones of varied tone in grey-brown mortar (tileable Voronoi).</summary>
        static Texture2D MakeStone(System.Random rng)
        {
            const int S = StoneSize, N = 7;
            float cell = S / (float)N;
            var pts = new Vector2[N * N];
            var tone = new Color[N * N];
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    pts[j * N + i] = new Vector2((i + 0.15f + 0.7f * (float)rng.NextDouble()) * cell, (j + 0.15f + 0.7f * (float)rng.NextDouble()) * cell);
                    // River stones: blue-grey granite, warm sandstone, rusty iron-stained, dark basalt.
                    var palette = new[] { new Color(0.55f, 0.57f, 0.60f), new Color(0.70f, 0.62f, 0.50f), new Color(0.60f, 0.45f, 0.34f), new Color(0.38f, 0.38f, 0.40f), new Color(0.64f, 0.63f, 0.60f) };
                    var baseTone = palette[rng.Next(palette.Length)];
                    float g = 0.85f + 0.3f * (float)rng.NextDouble();
                    tone[j * N + i] = baseTone * g;
                }
            var px = new Color32[S * S];
            StoneHeight = new float[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d1 = float.MaxValue, d2 = float.MaxValue; int best = 0;
                    int ci = (int)(x / cell), cj = (int)(y / cell);
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int ii = Mod(ci + di, N), jj = Mod(cj + dj, N);
                            var p = pts[jj * N + ii] + new Vector2((ci + di - ii) * cell, (cj + dj - jj) * cell);
                            float dx = (x - p.x) * 0.85f, dy = (y - p.y) * 1.15f;
                            float d = Mathf.Sqrt(dx * dx + dy * dy);
                            if (d < d1) { d2 = d1; d1 = d; best = jj * N + ii; } else if (d < d2) d2 = d;
                        }
                    float edge = d2 - d1;
                    float noise = Fbm(x / 10f, y / 10f, S / 10, S / 10, 62, 3);
                    if (edge < 5f)
                    {
                        float m = 0.30f + 0.04f * Hash(x, y, 61);
                        px[y * S + x] = C(m, m * 0.96f, m * 0.9f);
                        StoneHeight[y * S + x] = 0.05f * Hash(x, y, 63);
                        continue;
                    }
                    // Rounded (pillow) profile from the mortar line up.
                    float bevel = Mathf.Clamp01((edge - 5f) / 26f);
                    StoneHeight[y * S + x] = Mathf.Sqrt(bevel) * (0.85f + 0.15f * noise);
                    // Granite speckle, mottling and pale lichen on the upper, exposed parts of some stones.
                    float speck = Hash(x, y, 65);
                    float mottle = Fbm(x / 32f, y / 32f, S / 32, S / 32, 66, 3);
                    var t = tone[best] * (0.82f + 0.25f * mottle);
                    if (speck > 0.94f) t *= 1.25f; else if (speck < 0.06f) t *= 0.6f;
                    float lichen = Fbm(x / 64f, y / 64f, S / 64, S / 64, 67 + best, 3);
                    if (lichen > 0.66f && StoneHeight[y * S + x] > 0.5f) t = Color.Lerp(t, new Color(0.72f, 0.74f, 0.62f), Mathf.Clamp01((lichen - 0.66f) * 6f) * 0.6f);
                    px[y * S + x] = C(t.r, t.g, t.b);
                }
            BakeRelief(px, StoneHeight, S, S, false, 9f);
            return Upload(S, S, false, px);
        }

        /// <summary>Red and black buffalo check with a soft woven relief.</summary>
        static Texture2D MakePlaid()
        {
            const int S = 256;
            return MakeWithHeight(S, S, false, 1.0f, (x, y) =>
            {
                bool a = (x / 32) % 2 == 0, b = (y / 32) % 2 == 0;
                Color c = a && b ? new Color(0.08f, 0.05f, 0.05f) : (a || b ? new Color(0.36f, 0.06f, 0.05f) : new Color(0.68f, 0.12f, 0.08f));
                float weave = 0.5f + 0.5f * Mathf.Sin(x * 1.6f) * Mathf.Sin(y * 1.6f);
                float fuzz = 0.92f + 0.08f * Hash(x, y, 64);
                return (C(c.r * fuzz, c.g * fuzz, c.b * fuzz), weave);
            });
        }

        /// <summary>Skateboard deck underside: bright bands and a big circle (u along the board).</summary>
        static Texture2D MakeDeck()
        {
            const int W = 512, H = 128;
            var bands = new[] { new Color(0.10f, 0.62f, 0.66f), new Color(0.98f, 0.55f, 0.18f), new Color(0.98f, 0.85f, 0.25f), new Color(0.88f, 0.28f, 0.40f) };
            return Make(W, H, true, (x, y) =>
            {
                float u = x / (float)W, v = y / (float)H;
                int band = Mathf.Clamp((int)((u + 0.15f * Mathf.Sin(v * 6.28f)) * 4f), 0, 3);
                Color c = bands[band];
                float cx = (u - 0.5f) * W, cy = (v - 0.5f) * H;
                float r = Mathf.Sqrt(cx * cx + cy * cy);
                if (r < 44f) c = r < 32f ? new Color(0.98f, 0.96f, 0.90f) : new Color(0.12f, 0.12f, 0.16f);
                if (r < 14f) c = new Color(0.12f, 0.12f, 0.16f);
                return C(c.r, c.g, c.b);
            });
        }

        /// <summary>Clear night sky: deep gradient, stars, a bright moon with halo, a faint green aurora band.</summary>
        static Texture2D MakeNightSky(System.Random rng)
        {
            const int W = 1024, H = 1024;
            var px = new Color32[W * H];
            var top = new Color(0.01f, 0.015f, 0.05f);
            var horizon = new Color(0.10f, 0.12f, 0.24f);
            // Matches the sky dome mapping (CozyOutside.BuildSkyDome): moon at 18 deg azimuth, 14 deg elevation.
            // The dome stretches u about 2.2x more than v, so the moon is drawn as an ellipse to look round.
            float moonX = 0.58f * W, moonY = 0.257f * H, moonR = 18f;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = y / (float)H;
                    var c = Color.Lerp(horizon, top, Mathf.SmoothStep(0.15f, 0.75f, v));
                    float aur = Mathf.Exp(-Mathf.Pow((v - 0.34f - 0.04f * Mathf.Sin(x * 0.012f)) / 0.05f, 2f)) *
                                (0.5f + 0.5f * Fbm(x / 64f, 0, W / 64, 1, 71, 3));
                    c += new Color(0.05f, 0.22f, 0.12f) * aur * 0.55f;
                    float mdx = (x - moonX) * 2.2f, mdy = y - moonY;
                    float md = Mathf.Sqrt(mdx * mdx + mdy * mdy);
                    c += new Color(0.30f, 0.32f, 0.40f) * Mathf.Exp(-md / 40f) * 0.6f;
                    if (md < moonR) c = new Color(0.97f, 0.95f, 0.86f) * (0.92f + 0.08f * Fbm(x / 3f, y / 3f, 1000, 1000, 72, 2));
                    px[y * W + x] = C(c.r, c.g, c.b);
                }
            for (int i = 0; i < 1400; i++)
            {
                int x = rng.Next(W), y = (int)(H * (0.14f + 0.86f * (float)rng.NextDouble()));
                float b = 0.35f + 0.65f * (float)rng.NextDouble() * (float)rng.NextDouble();
                px[y * W + x] = C(b, b, b * 0.95f);
            }
            return Upload(W, H, true, px);
        }

        /// <summary>Distant jagged range, moonlit snow on the peaks and upper slopes (alpha above the ridge).</summary>
        static Texture2D MakeMountains()
        {
            const int W = 1024, H = 512;
            var px = new Color32[W * H];
            var ridge = new float[W];
            for (int x = 0; x < W; x++)
            {
                float u = x / (float)W;
                // A few big peaks plus jagged detail.
                float h = 0.35f;
                h = Mathf.Max(h, 0.88f - Mathf.Abs(u - 0.30f) * 2.1f);
                h = Mathf.Max(h, 0.74f - Mathf.Abs(u - 0.58f) * 1.8f);
                h = Mathf.Max(h, 0.66f - Mathf.Abs(u - 0.80f) * 2.4f);
                h = Mathf.Max(h, 0.55f - Mathf.Abs(u - 0.10f) * 1.6f);
                h += 0.05f * (Fbm(u * 24f, 0, 24, 1, 81, 4) - 0.5f);
                ridge[x] = h * H;
            }
            for (int x = 0; x < W; x++)
            {
                float slope = (ridge[Mathf.Min(W - 1, x + 1)] - ridge[Mathf.Max(0, x - 1)]) * 0.5f;
                for (int y = 0; y < H; y++)
                {
                    if (y > ridge[x]) { px[y * W + x] = new Color32(0, 0, 0, 0); continue; }
                    float below = (ridge[x] - y) / H;
                    float n = Fbm(x / 20f, y / 20f, W / 20, H / 20, 82, 4);
                    // Snow down from the ridge, broken up by rock on steep bits.
                    bool snow = below < 0.10f + 0.25f * n - Mathf.Abs(slope) * 0.04f;
                    float lit = slope > 0 ? 1.0f : 0.72f; // moon from the left
                    Color c = snow ? new Color(0.70f, 0.76f, 0.92f) * lit : new Color(0.16f, 0.19f, 0.30f) * (0.8f + 0.4f * n) * lit;
                    // Haze towards the base.
                    c = Color.Lerp(c, new Color(0.16f, 0.19f, 0.32f), Mathf.Clamp01(below * 1.6f));
                    px[y * W + x] = C(c.r, c.g, c.b);
                }
            }
            return Upload(W, H, true, px);
        }

        static void Pine(Color32[] px, int W, int H, float cx, float baseY, float height, float width, Color body, Color snow)
        {
            int tiers = 4;
            for (int t = 0; t < tiers; t++)
            {
                float ty0 = baseY + height * (0.12f + 0.2f * t), ty1 = ty0 + height * (0.42f - 0.04f * t);
                float tw = width * (1f - 0.2f * t);
                for (int y = Mathf.Max(0, (int)ty0); y < Mathf.Min(H, (int)ty1); y++)
                {
                    float f = 1f - (y - ty0) / (ty1 - ty0);
                    float half = tw * 0.5f * f;
                    for (int x = Mathf.Max(0, (int)(cx - half)); x <= Mathf.Min(W - 1, (int)(cx + half)); x++)
                    {
                        bool edge = (cx + half - x) < 2.5f || (x - (cx - half)) < 2.5f || y > ty1 - 3;
                        var c = edge || ((x * 7 + y * 3) % 11 == 0 && f < 0.7f) ? snow : body;
                        px[y * W + x] = C(c.r, c.g, c.b);
                    }
                }
            }
            for (int y = (int)baseY; y < (int)(baseY + height * 0.14f) && y < H; y++)
                for (int x = (int)cx - 2; x <= (int)cx + 2; x++)
                    if (x >= 0 && x < W && y >= 0) px[y * W + x] = C(0.10f, 0.07f, 0.06f);
        }

        /// <summary>Rolling snowy hills covered in pine forest (mid distance), alpha above.</summary>
        static Texture2D MakeForest(System.Random rng)
        {
            const int W = 1024, H = 256;
            var px = new Color32[W * H];
            var ground = new float[W];
            for (int x = 0; x < W; x++) ground[x] = (0.30f + 0.18f * Fbm(x / 160f, 3, W / 160, 1, 91, 3)) * H;
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                    px[y * W + x] = y <= ground[x] ? C(0.55f, 0.60f, 0.76f) : new Color32(0, 0, 0, 0);
            for (int i = 0; i < 260; i++)
            {
                float cx = rng.Next(W);
                float h = 40 + rng.Next(60);
                Pine(px, W, H, cx, ground[(int)cx] - 6, h, h * 0.45f, new Color(0.05f, 0.09f, 0.10f), new Color(0.55f, 0.60f, 0.75f));
            }
            return Upload(W, H, true, px);
        }

        /// <summary>Near snowy slope with a few big pines (closest layer outside the window).</summary>
        static Texture2D MakeNearTrees(System.Random rng)
        {
            const int W = 1024, H = 512;
            var px = new Color32[W * H];
            var ground = new float[W];
            for (int x = 0; x < W; x++) ground[x] = (0.12f + 0.06f * Fbm(x / 200f, 7, W / 200, 1, 95, 3)) * H;
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                {
                    if (y > ground[x]) { px[y * W + x] = new Color32(0, 0, 0, 0); continue; }
                    float n = Fbm(x / 30f, y / 12f, W / 30, H / 12, 96, 3);
                    px[y * W + x] = C(0.62f + 0.1f * n, 0.67f + 0.1f * n, 0.84f + 0.08f * n);
                }
            float[] xs = { 0.08f, 0.21f, 0.74f, 0.90f };
            foreach (var u in xs)
            {
                float cx = u * W + rng.Next(30);
                float h = 300 + rng.Next(150);
                Pine(px, W, H, cx, ground[(int)Mathf.Clamp(cx, 0, W - 1)] - 10, h, h * 0.42f, new Color(0.04f, 0.08f, 0.08f), new Color(0.75f, 0.80f, 0.92f));
            }
            return Upload(W, H, true, px);
        }
    }
}
