using System;
using UnityEngine;

namespace OlliVR
{
    /// <summary>
    /// Procedurally generated textures for the cozy room (the game ships no suitable art for it).
    /// Generated once at start-up.
    /// </summary>
    internal static class RoomTextures
    {
        public static Texture2D White, Wood, Wallpaper, Rug, Brick, Sky, Snow, Flames;
        public const int FlameFrames = 8;

        public static void Generate()
        {
            var rng = new System.Random(1234);
            White = Make(4, 4, false, (x, y) => new Color32(255, 255, 255, 255));
            Wood = MakeWood(rng);
            Wallpaper = MakeWallpaper();
            Rug = MakeRug();
            Brick = MakeBrick(rng);
            Sky = MakeSky(rng);
            Snow = MakeSnow(rng);
            Flames = MakeFlames();
        }

        static Texture2D Make(int w, int h, bool clamp, Func<int, int, Color32> f)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = f(x, y);
            return Upload(w, h, clamp, px);
        }

        static Texture2D Upload(int w, int h, bool clamp, Color32[] px)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            t.SetPixels32(px);
            t.Apply(true, false);
            UnityEngine.Object.DontDestroyOnLoad(t);
            return t;
        }

        // ---- noise ----------------------------------------------------------------------------------

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 982451653;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xffffff) / (float)0xffffff;
            }
        }

        /// <summary>Tileable value noise with the given period (in cells).</summary>
        static float Noise(float x, float y, int px, int py, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            int xa = Mod(x0, px), xb = Mod(x0 + 1, px), ya = Mod(y0, py), yb = Mod(y0 + 1, py);
            float a = Hash(xa, ya, seed), b = Hash(xb, ya, seed), c = Hash(xa, yb, seed), d = Hash(xb, yb, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        static float Fbm(float x, float y, int px, int py, int seed, int octaves = 4)
        {
            float sum = 0, amp = 0.5f, norm = 0;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Noise(x, y, px, py, seed + o * 17);
                norm += amp; amp *= 0.5f; x *= 2; y *= 2; px *= 2; py *= 2;
            }
            return sum / norm;
        }

        static int Mod(int a, int m) => ((a % m) + m) % m;
        static byte B(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        static Color32 C(float r, float g, float b, float a = 1f) => new(B(r), B(g), B(b), B(a));

        // ---- textures -------------------------------------------------------------------------------

        /// <summary>Floor planks running along u, 8 planks across, staggered joints, grain.</summary>
        static Texture2D MakeWood(System.Random rng)
        {
            const int S = 512, Planks = 8;
            int ph = S / Planks;
            var joint = new int[Planks];
            var tint = new float[Planks * 2];
            for (int i = 0; i < Planks; i++) joint[i] = rng.Next(S);
            for (int i = 0; i < tint.Length; i++) tint[i] = 0.85f + 0.3f * (float)rng.NextDouble();
            return Make(S, S, false, (x, y) =>
            {
                int plank = y / ph;
                int seg = ((x - joint[plank] + S) % S) < S / 2 ? 0 : 1;
                float t = tint[plank * 2 + seg];
                float grain = Fbm(x / 64f, y / 4f, S / 64, S / 4, 7 + plank, 3);
                float rings = 0.5f + 0.5f * Mathf.Sin((y % ph) * 0.35f + grain * 9f);
                float v = (0.78f + 0.22f * rings) * t;
                bool edge = y % ph == 0 || ((x - joint[plank] + S) % (S / 2)) == 0;
                if (edge) v *= 0.55f;
                return C(0.60f * v, 0.38f * v, 0.22f * v);
            });
        }

        /// <summary>Light, near-neutral wallpaper (tinted per wall) with soft stripes and small dots.</summary>
        static Texture2D MakeWallpaper()
        {
            const int S = 256;
            return Make(S, S, false, (x, y) =>
            {
                float stripe = 0.94f + 0.06f * Mathf.Cos(x / (float)S * Mathf.PI * 2f * 8f);
                int cx = (x + 16) % 32 - 16, cy = (y + ((x / 32) % 2) * 16 + 16) % 32 - 16;
                float dot = Mathf.Clamp01(1.5f - Mathf.Sqrt(cx * cx + cy * cy) / 2.2f);
                float paper = 0.97f + 0.03f * Fbm(x / 16f, y / 16f, S / 16, S / 16, 3, 3);
                float v = stripe * paper * (1f - 0.12f * dot);
                return C(v, v, v);
            });
        }

        /// <summary>Warm patterned rug with a border and a central medallion.</summary>
        static Texture2D MakeRug()
        {
            const int W = 512, H = 344;
            var red = new Color(0.55f, 0.14f, 0.10f);
            var cream = new Color(0.90f, 0.78f, 0.58f);
            var navy = new Color(0.14f, 0.16f, 0.28f);
            var ochre = new Color(0.80f, 0.52f, 0.18f);
            return Make(W, H, true, (x, y) =>
            {
                float u = x / (float)(W - 1), v = y / (float)(H - 1);
                float ex = Mathf.Min(u, 1 - u) * W, ey = Mathf.Min(v, 1 - v) * H, e = Mathf.Min(ex, ey);
                Color c;
                if (e < 6) c = cream;
                else if (e < 30) c = ((x / 12 + y / 12) % 2 == 0) ? navy : ochre;
                else if (e < 36) c = cream;
                else
                {
                    float dx = (u - 0.5f) * W, dy = (v - 0.5f) * H;
                    float diamond = Mathf.Abs(dx) / 150f + Mathf.Abs(dy) / 100f;
                    if (diamond < 0.35f) c = cream;
                    else if (diamond < 0.5f) c = navy;
                    else if (diamond < 0.58f) c = ochre;
                    else c = red * (0.9f + 0.1f * Mathf.Cos((x + y) * 0.25f));
                }
                float fuzz = 0.9f + 0.1f * Hash(x, y, 5);
                return C(c.r * fuzz, c.g * fuzz, c.b * fuzz);
            });
        }

        static Texture2D MakeBrick(System.Random rng)
        {
            const int S = 256, BH = 32, BW = 64;
            var tint = new float[64];
            for (int i = 0; i < tint.Length; i++) tint[i] = 0.8f + 0.35f * (float)rng.NextDouble();
            return Make(S, S, false, (x, y) =>
            {
                int row = y / BH;
                int xo = (x + (row % 2) * (BW / 2)) % S;
                int col = xo / BW;
                bool mortar = y % BH < 3 || xo % BW < 3;
                if (mortar) { float m = 0.55f + 0.05f * Hash(x, y, 9); return C(m, m * 0.95f, m * 0.9f); }
                float t = tint[(row * 7 + col) % tint.Length] * (0.92f + 0.08f * Fbm(x / 8f, y / 8f, S / 8, S / 8, 11, 2));
                return C(0.58f * t, 0.27f * t, 0.18f * t);
            });
        }

        /// <summary>Night sky: gradient, stars, a moon, snowy hills with a few lit cottage windows.</summary>
        static Texture2D MakeSky(System.Random rng)
        {
            const int W = 1024, H = 512;
            var px = new Color32[W * H];
            var top = new Color(0.02f, 0.03f, 0.09f);
            var horizon = new Color(0.16f, 0.13f, 0.28f);
            float moonX = 0.62f * W, moonY = 0.78f * H, moonR = 26f;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = y / (float)H;
                    var c = Color.Lerp(horizon, top, Mathf.SmoothStep(0.25f, 1f, v));
                    float md = Mathf.Sqrt((x - moonX) * (x - moonX) + (y - moonY) * (y - moonY));
                    c += new Color(0.35f, 0.35f, 0.45f) * Mathf.Exp(-md / 90f) * 0.5f;
                    if (md < moonR)
                    {
                        float crater = 0.9f + 0.1f * Fbm(x / 6f, y / 6f, 1000, 1000, 21, 3);
                        c = new Color(0.95f, 0.93f, 0.82f) * crater;
                    }
                    px[y * W + x] = C(c.r, c.g, c.b);
                }
            for (int i = 0; i < 700; i++)
            {
                int x = rng.Next(W), y = H / 3 + rng.Next(H * 2 / 3);
                float b = 0.4f + 0.6f * (float)rng.NextDouble();
                px[y * W + x] = C(b, b, b * 0.95f);
            }
            // Two ranges of snowy hills.
            for (int layer = 0; layer < 2; layer++)
            {
                float baseH = layer == 0 ? 0.30f : 0.22f;
                var body = layer == 0 ? new Color(0.06f, 0.07f, 0.13f) : new Color(0.10f, 0.11f, 0.19f);
                var snow = layer == 0 ? new Color(0.45f, 0.48f, 0.62f) : new Color(0.62f, 0.65f, 0.78f);
                for (int x = 0; x < W; x++)
                {
                    float hgt = (baseH + 0.10f * Fbm(x / 128f, layer * 10, W / 128, 1, 31 + layer, 3)) * H;
                    for (int y = 0; y < (int)hgt; y++)
                    {
                        float depthBelow = hgt - y;
                        var c = depthBelow < 5 ? snow : Color.Lerp(snow, body, Mathf.Clamp01((depthBelow - 5) / 30f));
                        px[y * W + x] = C(c.r, c.g, c.b);
                    }
                }
            }
            // A few cottages on the near hills with warm windows.
            for (int i = 0; i < 7; i++)
            {
                int cx = 60 + i * 140 + rng.Next(40), w = 26 + rng.Next(14), h = 16 + rng.Next(8);
                int ground = (int)((0.22f + 0.10f * Fbm(cx / 128f, 10, W / 128, 1, 32, 3)) * H) - 4;
                for (int y = ground; y < ground + h + w / 2; y++)
                    for (int x = cx - w / 2; x <= cx + w / 2; x++)
                    {
                        if (x < 0 || x >= W || y >= H) continue;
                        bool roof = y >= ground + h;
                        if (roof && Mathf.Abs(x - cx) > (ground + h + w / 2 - y)) continue;
                        Color c = roof ? new Color(0.70f, 0.72f, 0.84f) : new Color(0.07f, 0.06f, 0.10f);
                        bool win = !roof && y > ground + 4 && y < ground + 11 && (Mathf.Abs(x - cx + w / 4) < 3 || Mathf.Abs(x - cx - w / 4) < 3);
                        if (win) c = new Color(1f, 0.75f, 0.35f);
                        px[y * W + x] = C(c.r, c.g, c.b);
                    }
            }
            return Upload(W, H, true, px);
        }

        /// <summary>Tileable white snowflakes on transparent.</summary>
        static Texture2D MakeSnow(System.Random rng)
        {
            const int S = 256;
            var px = new Color32[S * S];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 0);
            for (int i = 0; i < 90; i++)
            {
                float cx = rng.Next(S), cy = rng.Next(S), r = 1.2f + 2.2f * (float)rng.NextDouble();
                for (int dy = -5; dy <= 5; dy++)
                    for (int dx = -5; dx <= 5; dx++)
                    {
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(1.2f - d / r);
                        if (a <= 0) continue;
                        int x = Mod((int)cx + dx, S), y = Mod((int)cy + dy, S);
                        int idx = y * S + x;
                        px[idx].a = (byte)Mathf.Max(px[idx].a, B(a * 0.9f));
                    }
            }
            return Upload(S, S, false, px);
        }

        /// <summary>Flame animation atlas: FlameFrames frames side by side, noise scrolling upward.</summary>
        static Texture2D MakeFlames()
        {
            const int FW = 64, FH = 128;
            int W = FW * FlameFrames;
            var px = new Color32[W * FH];
            for (int f = 0; f < FlameFrames; f++)
                for (int y = 0; y < FH; y++)
                    for (int x = 0; x < FW; x++)
                    {
                        float u = (x + 0.5f) / FW - 0.5f, v = (y + 0.5f) / FH;
                        // Scroll the noise upward by exactly one period over the loop so it tiles in time.
                        float n = Fbm(u * 4f + 8f, (v - f / (float)FlameFrames) * 4f, 16, 4, 41, 4);
                        float width = 0.42f * (1f - v) * (1f - v) + 0.04f;
                        float shape = 1f - Mathf.Abs(u + (n - 0.5f) * 0.25f * v) / width;
                        float heat = Mathf.Clamp01(shape * 1.6f) * Mathf.Clamp01((1f - v) * 1.4f - (n - 0.5f) * 0.9f);
                        heat = Mathf.Clamp01(heat * (0.75f + 0.5f * n));
                        if (v < 0.06f) heat *= v / 0.06f;
                        var c = heat > 0.7f ? Color.Lerp(new Color(1f, 0.75f, 0.25f), new Color(1f, 0.95f, 0.7f), (heat - 0.7f) / 0.3f)
                              : Color.Lerp(new Color(0.85f, 0.18f, 0.03f), new Color(1f, 0.75f, 0.25f), heat / 0.7f);
                        px[y * W + f * FW + x] = C(c.r, c.g, c.b, Mathf.Clamp01(heat * 1.8f));
                    }
            return Upload(W, FH, true, px);
        }
    }
}
