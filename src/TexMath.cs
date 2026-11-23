using System;

namespace OlliVR
{
    // Plain managed stand-ins for the few Unity types the texture generators use.
    //
    // The generators run on a worker thread: Unity's Mathf/Color/Vector types are IL2CPP proxies that must stay on
    // Unity's thread and are slow per call (17 s for the full set of textures). These are drop-in replacements
    // (same member names), aliased in RoomTextures.cs and LodgeTextures.cs.

    internal static class TexMath
    {
        public const float PI = MathF.PI;
        public const float Deg2Rad = MathF.PI / 180f;
        public static float Abs(float v) => MathF.Abs(v);
        public static int Abs(int v) => Math.Abs(v);
        public static float Sin(float v) => MathF.Sin(v);
        public static float Cos(float v) => MathF.Cos(v);
        public static float Sqrt(float v) => MathF.Sqrt(v);
        public static float Exp(float v) => MathF.Exp(v);
        public static float Pow(float a, float b) => MathF.Pow(a, b);
        public static float Min(float a, float b) => MathF.Min(a, b);
        public static float Max(float a, float b) => MathF.Max(a, b);
        public static float Max(float a, float b, float c) => MathF.Max(a, MathF.Max(b, c));
        public static int Min(int a, int b) => Math.Min(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        public static float Atan2Deg(float y, float x) => MathF.Atan2(y, x) * 180f / MathF.PI;
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float SmoothStep(float a, float b, float t)
        {
            t = Clamp01(t);
            t = -2f * t * t * t + 3f * t * t;
            return b * t + a * (1f - t);
        }
        public static int FloorToInt(float v) => (int)MathF.Floor(v);
        public static int RoundToInt(float v) => (int)MathF.Round(v);
        public static float Repeat(float t, float length) => Clamp(t - MathF.Floor(t / length) * length, 0f, length);
        public static float DeltaAngle(float current, float target)
        {
            float d = Repeat(target - current, 360f);
            if (d > 180f) d -= 360f;
            return d;
        }
    }

    internal struct TexColor
    {
        public float r, g, b, a;
        public TexColor(float r, float g, float b, float a = 1f) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static TexColor operator *(TexColor c, float k) => new(c.r * k, c.g * k, c.b * k, c.a * k);
        public static TexColor operator *(float k, TexColor c) => c * k;
        public static TexColor operator +(TexColor x, TexColor y) => new(x.r + y.r, x.g + y.g, x.b + y.b, x.a + y.a);
        public static TexColor Lerp(TexColor x, TexColor y, float t)
        {
            t = TexMath.Clamp01(t);
            return new(x.r + (y.r - x.r) * t, x.g + (y.g - x.g) * t, x.b + (y.b - x.b) * t, x.a + (y.a - x.a) * t);
        }
    }

    internal struct TexColor32
    {
        public byte r, g, b, a;
        public TexColor32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    internal struct TV2
    {
        public float x, y;
        public TV2(float x, float y) { this.x = x; this.y = y; }
        public static TV2 operator +(TV2 p, TV2 q) => new(p.x + q.x, p.y + q.y);
    }

    internal struct TV3
    {
        public float x, y, z;
        public TV3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public TV3 normalized { get { float m = MathF.Sqrt(x * x + y * y + z * z); return m > 1e-8f ? new(x / m, y / m, z / m) : new(0, 0, 0); } }
        public static float Dot(TV3 p, TV3 q) => p.x * q.x + p.y * q.y + p.z * q.z;
    }

    internal struct TV4
    {
        public float x, y, z, w;
        public TV4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }

    /// <summary>
    /// Pixels generated on a worker thread, turned into a Unity texture on the main thread (Create). Converts
    /// implicitly to a Unity Texture so callers can use RoomTextures.Wood etc. directly once uploaded.
    /// </summary>
    internal sealed class PendingTexture
    {
        public readonly int W, H;
        public readonly bool Clamp, Linear;
        TexColor32[] px;
        public UnityEngine.Texture2D Tex { get; private set; }

        public PendingTexture(int w, int h, bool clamp, TexColor32[] pixels, bool linear = false) { W = w; H = h; Clamp = clamp; px = pixels; Linear = linear; }

        public void Create()
        {
            if (Tex != null) return;
            // Full mip chain (box filtered) packed level after level, as LoadRawTextureData expects.
            var levels = new System.Collections.Generic.List<byte[]>();
            var level = new byte[W * H * 4];
            for (int i = 0, j = 0; i < px.Length; i++, j += 4)
            {
                level[j] = px[i].r; level[j + 1] = px[i].g; level[j + 2] = px[i].b; level[j + 3] = px[i].a;
            }
            levels.Add(level);
            int lw = W, lh = H;
            while (lw > 1 || lh > 1)
            {
                int nw = Math.Max(1, lw / 2), nh = Math.Max(1, lh / 2);
                var next = new byte[nw * nh * 4];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                        for (int ch = 0; ch < 4; ch++)
                        {
                            int x0 = Math.Min(lw - 1, x * 2), x1 = Math.Min(lw - 1, x * 2 + 1);
                            int y0 = Math.Min(lh - 1, y * 2), y1 = Math.Min(lh - 1, y * 2 + 1);
                            int s = level[(y0 * lw + x0) * 4 + ch] + level[(y0 * lw + x1) * 4 + ch] +
                                    level[(y1 * lw + x0) * 4 + ch] + level[(y1 * lw + x1) * 4 + ch];
                            next[(y * nw + x) * 4 + ch] = (byte)((s + 2) / 4);
                        }
                levels.Add(next);
                level = next; lw = nw; lh = nh;
            }
            int total = 0;
            foreach (var l in levels) total += l.Length;
            var bytes = new byte[total];
            int off = 0;
            foreach (var l in levels) { Buffer.BlockCopy(l, 0, bytes, off, l.Length); off += l.Length; }
            var t = new UnityEngine.Texture2D(W, H, UnityEngine.TextureFormat.RGBA32, true, Linear)
            {
                hideFlags = UnityEngine.HideFlags.HideAndDontSave,
                wrapMode = Clamp ? UnityEngine.TextureWrapMode.Clamp : UnityEngine.TextureWrapMode.Repeat,
                filterMode = UnityEngine.FilterMode.Trilinear,
                anisoLevel = 8,
            };
            t.LoadRawTextureData(bytes);
            t.Apply(false, false);
            UnityEngine.Object.DontDestroyOnLoad(t);
            Tex = t;
            px = null;
        }

        public static implicit operator UnityEngine.Texture(PendingTexture p) => p?.Tex;
    }
}
