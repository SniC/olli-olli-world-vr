using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using V3 = System.Numerics.Vector3;

namespace OlliVR
{
    /// <summary>
    /// A small CPU ray tracer that bakes the room's lighting into per-vertex colours.
    ///
    /// Runs on worker threads, so it only uses plain managed maths (System.Numerics), never Unity types: those
    /// go through IL2CPP and must stay on Unity's thread.
    ///
    /// Per vertex: soft shadows from every light (several samples over each light's area, tested against the
    /// furniture as oriented boxes), ray-traced ambient occlusion against furniture and the room shell, a soft
    /// cool skylight through the window, the fire seen through its opening, and a filmic roll-off of bright
    /// values. The fire's share is also returned separately for the flicker pass.
    /// </summary>
    internal sealed class LightBaker
    {
        public struct Obb
        {
            public V3 C, X, Y, Z;   // centre and unit axes
            public V3 H;            // half extents
            public float R;         // bounding radius
        }

        public enum Kind { Point, Lamp, Chandelier, Fire, Window }

        public sealed class Light
        {
            public Kind Kind;
            public V3 Col;
            public float Radius;        // distance falloff
            public V3[] Samples;        // points on the light used for shadow rays
            public float MaxRange = 99f;
        }

        // Scene.
        public readonly List<Obb> Occluders = new();
        public readonly List<Light> Lights = new();
        public float RoomHalfW, ZBack, ZFront, Floor, EaveH, RidgeH, WallInset;
        public float FireOpeningX, FireZ0, FireZ1, FireY0, FireY1;
        public V3 FirePos;
        public V3 Ambient = new(0.020f, 0.016f, 0.015f);

        // Vertices in, colours out.
        public V3[] P, N, Tint;
        public bool[] Emissive, NoAo;
        public V3[] Base, FireOnly, TvOnly;

        // The TV screen as an area light facing -z (its colour is applied live, per frame; this bakes only how
        // much of the screen each point sees).
        public bool HasTv;
        public V3 TvCentre;
        public float TvHalfW, TvHalfH;
        V3[] tvSamples;

        static readonly V3[] AoDirs = MakeAoDirs(14);

        public Task BakeAsync() => Task.Run(() =>
        {
            int n = P.Length;
            Base = new V3[n];
            FireOnly = new V3[n];
            TvOnly = new V3[n];
            if (HasTv)
            {
                var s = new List<V3>();
                for (int j = 0; j < 3; j++)
                    for (int i = 0; i < 4; i++)
                        s.Add(TvCentre + new V3((i - 1.5f) / 1.5f * TvHalfW * 0.85f, (j - 1f) * TvHalfH * 0.8f, -0.004f));
                tvSamples = s.ToArray();
            }
            var occ = Occluders.ToArray();
            Parallel.For(0, n, i => BakeVertex(i, occ));
        });

        void BakeVertex(int i, Obb[] occ)
        {
            if (Emissive[i]) { Base[i] = Tint[i]; FireOnly[i] = V3.Zero; if (TvOnly != null) TvOnly[i] = V3.Zero; return; }
            var p = P[i];
            var nrm = N[i];
            var origin = p + nrm * 0.012f;

            float ao = NoAo[i] ? 1f : AmbientOcclusion(origin, nrm, occ);
            var light = Ambient * ao;
            var fire = V3.Zero;

            foreach (var l in Lights)
            {
                if (l.Kind == Kind.Window) { light += WindowLight(l, origin, nrm, occ); continue; }
                var centre = l.Samples[0];
                float dist = V3.Distance(centre, p);
                if (dist > l.MaxRange) continue;
                var dir = (centre - p) / MathF.Max(dist, 0.01f);
                float ndl = V3.Dot(nrm, dir);
                if (ndl <= 0f) continue;
                float att = 1f / (1f + (dist / l.Radius) * (dist / l.Radius));
                float shape = 1f;
                if (l.Kind == Kind.Lamp)
                {
                    float v = MathF.Abs(dir.Y);
                    shape = 0.08f + 1.4f * v * v * v;   // dense shade: light leaves through top and bottom
                }
                float vis = l.Kind == Kind.Fire ? FireVisibility(p) : 1f;
                if (vis <= 0f) continue;
                vis *= Visibility(origin, l.Samples, occ);
                var c = l.Col * (ndl * att * shape * vis);
                if (l.Kind == Kind.Fire) fire += c; else light += c;
            }

            // The fire: a steady part baked in, the rest flickers additively at run time.
            light += fire * 0.55f;
            Base[i] = Tint[i] * ToneMap(light);
            if (HasTv) TvOnly[i] = new V3(TvLight(p, origin, nrm, occ));
            FireOnly[i] = Tint[i] * (fire * 0.45f);
        }

        /// <summary>How much of the TV screen a point sees: lambert at both ends, distance falloff, shadows.</summary>
        float TvLight(V3 p, V3 origin, V3 nrm, Obb[] occ)
        {
            if (p.Z > TvCentre.Z - 0.01f) return 0f;                 // behind the screen plane
            float sum = 0f;
            foreach (var s in tvSamples)
            {
                var d = s - origin;
                float dist = d.Length();
                var dir = d / dist;
                float ndl = V3.Dot(nrm, dir);
                float emit = dir.Z;                                    // the screen emits towards -z
                if (ndl <= 0f || emit <= 0f) continue;
                if (Blocked(origin, s, occ)) continue;
                float att = 1f / (1f + (dist / 1.6f) * (dist / 1.6f));
                sum += ndl * emit * att;
            }
            return sum / tvSamples.Length;
        }

        /// <summary>Soft roll-off above ~0.55 so bright pools don't clip flat.</summary>
        static V3 ToneMap(V3 c) => new(Tm(c.X), Tm(c.Y), Tm(c.Z));
        static float Tm(float x) => x < 0.55f ? x : 0.55f + 0.45f * (1f - MathF.Exp(-(x - 0.55f) / 0.45f));

        float Visibility(V3 origin, V3[] samples, Obb[] occ)
        {
            int seen = 0;
            foreach (var s in samples)
                if (!Blocked(origin, s, occ)) seen++;
            return seen / (float)samples.Length;
        }

        /// <summary>Cool light from the night sky through the window: an area light over the window opening.</summary>
        V3 WindowLight(Light l, V3 origin, V3 nrm, Obb[] occ)
        {
            var sum = V3.Zero;
            foreach (var s in l.Samples)
            {
                var d = s - origin;
                float dist = d.Length();
                var dir = d / dist;
                float ndl = V3.Dot(nrm, dir);
                float facing = -dir.X;            // window faces -x into the room; light arrives from +x
                if (ndl <= 0f || facing <= 0f) continue;
                if (Blocked(origin, s, occ)) continue;
                float att = 1f / (1f + (dist / l.Radius) * (dist / l.Radius));
                sum += l.Col * (ndl * facing * att);
            }
            return sum / l.Samples.Length;
        }

        /// <summary>Fire seen through the opening: 1 inside the firebox, soft-edged through the opening outside.</summary>
        float FireVisibility(V3 p)
        {
            var s = FirePos;
            if (p.X <= FireOpeningX + 0.002f)
            {
                bool inBox = p.Z > FireZ0 - 0.01f && p.Z < FireZ1 + 0.01f && p.Y > FireY0 - 0.01f && p.Y < FireY1 + 0.01f;
                return inBox ? 1f : 0f;
            }
            float t = (FireOpeningX - s.X) / (p.X - s.X);
            float hz = s.Z + (p.Z - s.Z) * t, hy = s.Y + (p.Y - s.Y) * t;
            float inside = MathF.Min(MathF.Min(hz - FireZ0, FireZ1 - hz), MathF.Min(hy - FireY0, FireY1 - hy));
            float x = Math.Clamp((inside + 0.03f) / 0.06f, 0f, 1f);
            return x * x * (3f - 2f * x);
        }

        // ---- ambient occlusion -------------------------------------------------------------------------

        float AmbientOcclusion(V3 origin, V3 nrm, Obb[] occ)
        {
            // Tangent frame around the normal.
            var t = MathF.Abs(nrm.Y) < 0.9f ? V3.Normalize(V3.Cross(V3.UnitY, nrm)) : V3.Normalize(V3.Cross(V3.UnitX, nrm));
            var b = V3.Cross(nrm, t);
            const float MaxD = 0.7f;
            float occluded = 0f;
            foreach (var d in AoDirs)
            {
                var dir = t * d.X + b * d.Y + nrm * d.Z;
                float hit = ShellHit(origin, dir, MaxD);
                var end = origin + dir * MaxD;
                float h2 = FirstHit(origin, end, occ);
                hit = MathF.Min(hit, h2 * MaxD);
                if (hit < MaxD) occluded += 1f - hit / MaxD * 0.6f;
            }
            return 1f - 0.85f * occluded / AoDirs.Length;
        }

        /// <summary>Distance along dir to the room shell (walls, floor, roof), capped at maxD.</summary>
        float ShellHit(V3 o, V3 d, float maxD)
        {
            float best = maxD;
            float inX = RoomHalfW - WallInset;
            void Plane(float pos, float dirc, float oc, bool positive)
            {
                if (MathF.Abs(dirc) < 1e-5f) return;
                float tt = (pos - oc) / dirc;
                if (tt > 0.001f && tt < best && (positive ? dirc > 0 : dirc < 0)) best = tt;
            }
            Plane(inX, d.X, o.X, true);
            Plane(-inX, d.X, o.X, false);
            Plane(ZFront, d.Z, o.Z, true);
            Plane(ZBack + WallInset, d.Z, o.Z, false);
            Plane(Floor, d.Y, o.Y, false);
            // Roof: y = Floor + EaveH + (RidgeH-EaveH)(1-|x|/W). March a few steps (cheap and good enough).
            for (int k = 1; k <= 6; k++)
            {
                float tt = best * k / 6f;
                var q = o + d * tt;
                float roof = Floor + EaveH + (RidgeH - EaveH) * Math.Clamp(1f - MathF.Abs(q.X) / RoomHalfW, 0f, 1f);
                if (q.Y > roof) { best = MathF.Min(best, tt); break; }
            }
            return best;
        }

        static V3[] MakeAoDirs(int count)
        {
            // Cosine-weighted hemisphere, Fibonacci spiral (deterministic, even coverage).
            var dirs = new V3[count];
            float golden = MathF.PI * (3f - MathF.Sqrt(5f));
            for (int i = 0; i < count; i++)
            {
                float u = (i + 0.5f) / count;
                float r = MathF.Sqrt(u);
                float phi = i * golden;
                dirs[i] = new V3(r * MathF.Cos(phi), r * MathF.Sin(phi), MathF.Sqrt(1f - u));
            }
            return dirs;
        }

        // ---- ray vs boxes ------------------------------------------------------------------------------

        static bool Blocked(V3 a, V3 b, Obb[] occ) => FirstHit(a, b, occ) < 1f;

        /// <summary>Smallest segment parameter (0..1) at which a-b enters any box, or 1 if none.</summary>
        static float FirstHit(V3 a, V3 b, Obb[] occ)
        {
            var d = b - a;
            float len2 = d.LengthSquared();
            float best = 1f;
            for (int k = 0; k < occ.Length; k++)
            {
                ref var o = ref occ[k];
                // Broad phase: distance from the box centre to the segment vs its bounding radius.
                var ac = o.C - a;
                float t = Math.Clamp(V3.Dot(ac, d) / len2, 0f, 1f);
                var closest = a + d * t - o.C;
                if (closest.LengthSquared() > o.R * o.R) continue;
                // Slab test in the box's frame.
                var lo = a - o.C;
                float t0 = 0.0005f, t1 = best;
                if (!Slab(V3.Dot(lo, o.X), V3.Dot(d, o.X), o.H.X, ref t0, ref t1)) continue;
                if (!Slab(V3.Dot(lo, o.Y), V3.Dot(d, o.Y), o.H.Y, ref t0, ref t1)) continue;
                if (!Slab(V3.Dot(lo, o.Z), V3.Dot(d, o.Z), o.H.Z, ref t0, ref t1)) continue;
                if (t0 < best) best = t0;
            }
            return best;
        }

        static bool Slab(float o, float d, float h, ref float t0, ref float t1)
        {
            if (MathF.Abs(d) < 1e-7f) return o >= -h && o <= h;
            float ta = (-h - o) / d, tb = (h - o) / d;
            if (ta > tb) (ta, tb) = (tb, ta);
            if (ta > t0) t0 = ta;
            if (tb < t1) t1 = tb;
            return t0 <= t1;
        }

        public static Obb MakeObb(V3 c, V3 x, V3 y, V3 z, V3 half)
            => new() { C = c, X = x, Y = y, Z = z, H = half, R = half.Length() };
    }
}
