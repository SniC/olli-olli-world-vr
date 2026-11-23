// Managed stand-ins (see TexMath.cs): these generators run on a worker thread.
using Mathf = OlliVR.TexMath;
using Color = OlliVR.TexColor;
using Color32 = OlliVR.TexColor32;
using Texture2D = OlliVR.PendingTexture;

namespace OlliVR
{
    /// <summary>Textures for the lodge's soft furnishings and wall decor.</summary>
    internal static partial class RoomTextures
    {
        public static Texture2D Wool, Cowhide, TrailMap, Frost, Wicker, ClockFace;

        static void GenerateLodge3(System.Random rng)
        {
            Wool = MakeWool();
            Cowhide = MakeCowhide();
            TrailMap = MakeTrailMap(rng);
            Frost = MakeFrost();
            Wicker = MakeWicker();
            ClockFace = MakeClockFace();
        }

        /// <summary>Heavy woven wool (tinted per use): fine twill weave and slubs.</summary>
        static Texture2D MakeWool()
        {
            const int S = 256;
            return MakeWithHeight(S, S, false, 1.4f, (x, y) =>
            {
                float twill = 0.5f + 0.5f * Mathf.Sin((x + y) * 0.9f);
                float slub = Fbm(x / 6f, y / 24f, S / 6 + 1, S / 24 + 1, 110, 2);
                float v = 0.82f + 0.1f * twill + 0.08f * slub;
                return (C(v, v, v), 0.5f + 0.35f * twill + 0.15f * slub);
            });
        }

        /// <summary>Cowhide: cream with irregular dark brown patches and short hair texture.</summary>
        static Texture2D MakeCowhide()
        {
            const int S = 512;
            return MakeWithHeight(S, S, true, 0.8f, (x, y) =>
            {
                float n = Fbm(x / 70f, y / 70f, 8, 8, 111, 4);
                float hair = 0.92f + 0.08f * Hash(x, y / 2, 112);
                var c = n > 0.56f ? new Color(0.22f, 0.13f, 0.08f) : new Color(0.92f, 0.88f, 0.80f);
                if (n > 0.53f && n <= 0.56f) c = Color.Lerp(new Color(0.92f, 0.88f, 0.80f), new Color(0.22f, 0.13f, 0.08f), (n - 0.53f) / 0.03f);
                c = c * hair;
                return (C(c.r, c.g, c.b), hair);
            });
        }

        /// <summary>An old trail map: parchment, contour lines, a lake, dashed trails, a compass rose.</summary>
        static Texture2D MakeTrailMap(System.Random rng)
        {
            const int W = 512, H = 384;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = x / (float)W, v = y / (float)H;
                    float stain = Fbm(x / 40f, y / 40f, 13, 10, 120, 4);
                    var paper = Color.Lerp(new Color(0.88f, 0.80f, 0.62f), new Color(0.70f, 0.58f, 0.40f), stain * 0.7f);
                    float edge = Mathf.Min(Mathf.Min(u, 1 - u), Mathf.Min(v, 1 - v));
                    paper = paper * (0.75f + 0.25f * Mathf.Clamp01(edge * 12f));
                    float hgt = Fbm(x / 90f, y / 90f, 6, 5, 121, 4);
                    float contour = Mathf.Abs((hgt * 14f) % 1f - 0.5f);
                    var c = paper;
                    if (hgt < 0.36f) c = Color.Lerp(paper, new Color(0.45f, 0.60f, 0.70f), 0.6f);          // lake
                    else if (contour < 0.04f) c = paper * 0.62f;                                              // contour lines
                    // A couple of dashed red trails.
                    float t1 = Mathf.Abs(v - (0.3f + 0.25f * Mathf.Sin(u * 7f + 1f) + 0.1f * u));
                    float t2 = Mathf.Abs(u - (0.65f + 0.12f * Mathf.Sin(v * 9f)));
                    bool dash = ((x + y) / 7) % 2 == 0;
                    if ((t1 < 0.006f || t2 < 0.005f) && dash && hgt >= 0.36f) c = new Color(0.62f, 0.12f, 0.10f);
                    // Compass rose bottom right.
                    float cx = x - W * 0.86f, cy = y - H * 0.18f, r = Mathf.Sqrt(cx * cx + cy * cy);
                    if (r < 26f && (Mathf.Abs(cx) < 2.5f * (1f - r / 26f) * 4f || Mathf.Abs(cy) < 2.5f * (1f - r / 26f) * 4f)) c = new Color(0.25f, 0.18f, 0.12f);
                    if (Mathf.Abs(r - 20f) < 1f) c = new Color(0.25f, 0.18f, 0.12f);
                    px[y * W + x] = C(c.r, c.g, c.b);
                }
            return Upload(W, H, true, px);
        }

        /// <summary>Frost creeping in from the corners of a window pane (premultiplied, drawn blended).</summary>
        static Texture2D MakeFrost()
        {
            const int S = 256;
            return Make(S, S, true, (x, y) =>
            {
                float u = x / (float)(S - 1), v = y / (float)(S - 1);
                float corner = Mathf.Min(Mathf.Min(Mathf.Sqrt(u * u + v * v), Mathf.Sqrt((1 - u) * (1 - u) + v * v)),
                                         Mathf.Min(Mathf.Sqrt(u * u + (1 - v) * (1 - v)), Mathf.Sqrt((1 - u) * (1 - u) + (1 - v) * (1 - v))));
                float edge = Mathf.Min(Mathf.Min(u, 1 - u), Mathf.Min(v, 1 - v));
                float crystals = Fbm(x / 9f, y / 9f, S / 9 + 1, S / 9 + 1, 130, 4);
                float a = Mathf.Clamp01((0.2f - corner) * 6f + (0.035f - edge) * 9f + (crystals - 0.5f) * 0.7f);
                a = a * a * 0.45f;
                return C(0.85f, 0.90f, 1.0f, a);
            });
        }

        /// <summary>Woven wicker for baskets.</summary>
        static Texture2D MakeWicker()
        {
            const int S = 256;
            return MakeWithHeight(S, S, false, 2.4f, (x, y) =>
            {
                int cell = 16;
                bool over = ((x / cell) + (y / cell)) % 2 == 0;
                float along = over ? Mathf.Sin((y % cell) / (float)cell * Mathf.PI) : Mathf.Sin((x % cell) / (float)cell * Mathf.PI);
                float v = 0.6f + 0.4f * along;
                return (C(0.66f * v, 0.48f * v, 0.28f * v), along);
            });
        }

        /// <summary>A plain clock face: cream dial, hour marks, a thin rim (hands are geometry).</summary>
        static Texture2D MakeClockFace()
        {
            const int S = 256;
            return Make(S, S, true, (x, y) =>
            {
                float cx = x - S / 2f + 0.5f, cy = y - S / 2f + 0.5f, r = Mathf.Sqrt(cx * cx + cy * cy) / (S / 2f);
                var c = new Color(0.92f, 0.88f, 0.78f);
                if (r > 0.94f) c = new Color(0.12f, 0.10f, 0.09f);
                float ang = Mathf.DeltaAngle(0, Mathf.Atan2Deg(cy, cx));
                float toMark = Mathf.Abs(((ang + 360f) % 30f) - 15f);
                if (r > 0.74f && r < 0.88f && toMark > 13.6f) c = new Color(0.15f, 0.12f, 0.10f);
                return C(c.r, c.g, c.b);
            });
        }
    }
}
