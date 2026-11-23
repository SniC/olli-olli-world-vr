using UnityEngine;

namespace OlliVR
{
    /// <summary>
    /// The lived-in layer of the lodge: extra lights at several heights (wall sconces, a table lamp, picture
    /// lights), curtains and a window seat, fireplace dressing, soft furnishings, wall decor and the sofa you sit on.
    /// </summary>
    internal partial class CozyRoom
    {
        sealed class Kit
        {
            public MeshBuilder Plain, Logs, Boards, Leather, Plaid, Stone, Painting, Wool, Cowhide, Map, Wicker, Clock;
        }

        static readonly Color Iron = new(0.07f, 0.06f, 0.06f);
        static readonly Color Brass = new(0.62f, 0.46f, 0.20f);
        static readonly Color ForestGreen = new(0.20f, 0.33f, 0.25f);
        static readonly Color DeepRed = new(0.55f, 0.12f, 0.10f);
        static readonly Color Cream = new(0.90f, 0.85f, 0.74f);

        void BuildDecor(Kit k, float F, Rect doorRect, Rect windowRect, float fz, float cx1, float mantelY, float cw, Vector3 sideTable)
        {
            float W = RoomHalfW;

            // ---- wall sconces either side of the door (back wall) ----
            foreach (var sx in new[] { doorRect.xMin - 0.32f, doorRect.xMax + 0.25f })
                Sconce(k, new Vector3(sx, F + 1.82f, ZBack + LogR + 0.01f), Vector3.forward);

            // ---- table lamp on the side table by the armchair ----
            TableLamp(k, sideTable + new Vector3(-0.09f, 0.555f, -0.08f));   // back of the table; books and mug in front

            // ---- curtains on an iron rod, and a cushioned window seat below ----
            float wz0 = windowRect.xMin, wz1 = windowRect.xMax, wy0 = windowRect.yMin, wy1 = windowRect.yMax;
            float rodX = W - LogR - 0.12f, rodY = wy1 + 0.1f;   // below the tie beams
            k.Plain.Rod(new Vector3(rodX, rodY, wz0 - 0.55f), new Vector3(rodX, rodY, wz1 + 0.55f), 0.014f, Iron, 10);
            k.Plain.Sphere(new Vector3(rodX, rodY, wz0 - 0.57f), 0.03f, Iron);
            k.Plain.Sphere(new Vector3(rodX, rodY, wz1 + 0.57f), 0.03f, Iron);
            foreach (var zr in new[] { wz0 - 0.5f, wz1 + 0.5f })
                k.Plain.Rod(new Vector3(W - LogR, rodY, zr), new Vector3(rodX, rodY, zr), 0.01f, Iron, 8);
            float seatTop = F + 0.46f;
            // Sill-length curtains: they stop just above the sill (which sticks out into the room) and the seat pillows.
            float curtainBottom = wy0 + 0.03f;
            Curtain(k.Wool, rodX + 0.02f, wz0 - 0.5f, wz0 + 0.08f, curtainBottom, rodY - 0.02f, ForestGreen, 1);
            Curtain(k.Wool, rodX + 0.02f, wz1 - 0.08f, wz1 + 0.5f, curtainBottom, rodY - 0.02f, ForestGreen, -1);
            // Window seat: a boarded bench along the wall with a long cushion, pillows and a folded throw.
            float benchX0 = W - LogR - 0.52f, benchX1 = W - LogR - 0.01f, bz0 = wz0 - 0.32f, bz1 = wz1 + 0.32f;
            k.Boards.RoundBox(new Vector3((benchX0 + benchX1) / 2, F + 0.2f, (bz0 + bz1) / 2), new Vector3(benchX1 - benchX0, 0.4f, bz1 - bz0), 0.01f, new Color(0.62f, 0.46f, 0.32f));
            k.Wool.RoundBox(new Vector3((benchX0 + benchX1) / 2, F + 0.43f, (bz0 + bz1) / 2), new Vector3(benchX1 - benchX0 - 0.02f, 0.07f, bz1 - bz0 - 0.03f), 0.03f, Cream);
            k.Wool.RoundBox(new Vector3(benchX1 - 0.12f, F + 0.62f, wz0 + 0.1f), new Vector3(0.14f, 0.36f, 0.4f), 0.08f, DeepRed, Quaternion.Euler(0, 0, 14f), occ: false);
            k.Plaid.RoundBox(new Vector3(benchX1 - 0.13f, F + 0.6f, wz0 + 0.55f), new Vector3(0.13f, 0.32f, 0.36f), 0.07f, Color.white, Quaternion.Euler(0, 0, 12f), occ: false);
            k.Wool.RoundBox(new Vector3(benchX1 - 0.12f, F + 0.6f, wz1 - 0.2f), new Vector3(0.13f, 0.32f, 0.38f), 0.07f, Cream * 0.95f, Quaternion.Euler(0, 0, 13f), occ: false);
            k.Plaid.RoundBox(new Vector3((benchX0 + benchX1) / 2 - 0.05f, F + 0.495f, wz1 + 0.05f), new Vector3(0.36f, 0.05f, 0.3f), 0.02f, Color.white, occ: false);
            k.Plain.RoundBox(new Vector3(benchX0 + 0.16f, F + 0.485f, wz1 + 0.06f), new Vector3(0.16f, 0.03f, 0.22f), 0.005f, new Color(0.25f, 0.30f, 0.42f), Quaternion.Euler(0, 20f, 0), occ: false);

            // ---- frost in the corners of the window panes (blended, drawn after the outside) ----
            var frost = new MeshBuilder(this, RoomTextures.Frost);
            float gx = W + 0.06f, wyT = wy0 + (wy1 - wy0) * 0.7f;
            for (int col = 0; col < 3; col++)
                foreach (var (y0, y1) in new[] { (wy0, wyT), (wyT, wy1) })
                {
                    float z0 = Mathf.Lerp(wz0, wz1, col / 3f), z1 = Mathf.Lerp(wz0, wz1, (col + 1) / 3f);
                    frost.Quad(new Vector3(gx, y0, z0), new Vector3(gx, y0, z1), new Vector3(gx, y1, z1), new Vector3(gx, y1, z0), Vector3.left, Color.white,
                               new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), emissive: true);
                }
            Outside.Add(new Part { Mesh = frost.ToMeshUnlit(), Tex = frost.Tex });

            // ---- fireplace dressing: tool set, log basket, antlers above the mantel, pine cones and books ----
            FireTools(k, new Vector3(cx1 + 0.22f, F + 0.06f, fz + cw / 2 - 0.05f));
            LogBasket(k, new Vector3(-W + LogR + 0.38f, F, fz - cw / 2 - 0.42f));
            Antlers(k, new Vector3(cx1 + 0.03f, mantelY + 0.62f, fz));
            for (int i = 0; i < 4; i++)
                PineCone(k.Plain, new Vector3(cx1 - 0.05f + 0.04f * (i % 2), mantelY + 0.1f, fz - 0.32f + i * 0.07f));
            for (int i = 0; i < 3; i++)
                k.Plain.RoundBox(new Vector3(cx1 - 0.08f, mantelY + 0.085f + i * 0.032f, fz + 0.42f), new Vector3(0.16f, 0.03f, 0.22f - 0.02f * i), 0.004f,
                                 new[] { DeepRed, ForestGreen, Cream }[i], Quaternion.Euler(0, 8f * i, 0), occ: false);

            // ---- wall decor: crossed snowshoes, a trail map, a wall clock ----
            Snowshoes(k, new Vector3(-1.72f, F + 1.78f, ZBack + LogR + 0.03f));   // back wall above the armchair
            Picture(k.Boards, k.Map, new Vector3(-W + LogR + 0.02f, F + 1.6f, -2.05f), Vector3.right, 0.56f, 0.42f, new Color(0.30f, 0.20f, 0.13f));
            WallClock(k, new Vector3(-W + LogR + 0.03f, F + 1.95f, -0.45f), Vector3.right);

            // ---- picture lights over the two paintings ----
            PictureLight(k, new Vector3(W - LogR - 0.02f, F + 1.62f + 0.27f + 0.06f, -1.75f), Vector3.left, 0.5f);
            PictureLight(k, new Vector3(-W + LogR + 0.02f, F + 1.65f + 0.225f + 0.06f, -1.2f), Vector3.right, 0.42f);

            // ---- cowhide under the coffee table, a basket of blankets by the sofa ----
            k.Cowhide.Blob(new Vector3(0.05f, F + 0.012f, 1.05f), 0.95f, 0.7f, Color.white, 5, 1.9f);
            BlanketBasket(k, new Vector3(1.32f, F, -0.15f));

            // ---- the sofa you sit on ----
            Sofa(k, new Vector3(0f, F, -0.05f));
        }

        // ---- lights -------------------------------------------------------------------------------------

        /// <summary>An iron wall sconce with a candle-style bulb under a small amber glass shade.</summary>
        void Sconce(Kit k, Vector3 wallPos, Vector3 n)
        {
            k.Plain.RoundBox(wallPos + n * 0.012f, new Vector3(0.08f, 0.16f, 0.024f), 0.006f, Iron, Quaternion.LookRotation(n), occ: false);
            var armEnd = wallPos + n * 0.17f + Vector3.up * 0.02f;
            k.Plain.Rod(wallPos + n * 0.02f - Vector3.up * 0.03f, armEnd - Vector3.up * 0.03f, 0.008f, Iron, 8);
            k.Plain.Cylinder(armEnd - Vector3.up * 0.05f, 0.03f, 0.022f, 0.03f, Iron, 12);
            var bulb = armEnd + Vector3.up * 0.04f;
            k.Plain.Cylinder(armEnd - Vector3.up * 0.02f, 0.014f, 0.014f, 0.05f, new Color(0.95f, 0.92f, 0.85f), 10);
            // Amber glass shade, lit from inside.
            k.Plain.Cylinder(bulb - Vector3.up * 0.03f, 0.05f, 0.075f, 0.1f, new Color(0.85f, 0.52f, 0.22f) * 0.55f, 16, emissive: true, open: true);
            k.Plain.Sphere(bulb, 0.016f, new Color(1f, 0.86f, 0.6f), emissive: true);
            lights.Add(new PointLight { Pos = bulb + n * 0.02f, Col = new Color(1.0f, 0.62f, 0.30f) * 0.38f, Radius = 0.6f });
            Glows.Add((bulb, 0.08f, new Color(0.16f, 0.10f, 0.04f), false));
            Glows.Add((bulb, 0.5f, new Color(0.035f, 0.02f, 0.008f), false));   // warm haze around it
        }

        /// <summary>A ceramic table lamp with a small fabric shade glowing from inside.</summary>
        void TableLamp(Kit k, Vector3 basePos)
        {
            var ceramic = new Color(0.32f, 0.42f, 0.40f);
            k.Plain.Cylinder(basePos, 0.06f, 0.07f, 0.03f, Iron, 16);
            k.Plain.Cylinder(basePos + Vector3.up * 0.03f, 0.07f, 0.05f, 0.14f, ceramic, 20);
            k.Plain.Cylinder(basePos + Vector3.up * 0.17f, 0.05f, 0.02f, 0.05f, ceramic, 20);
            k.Plain.Rod(basePos + Vector3.up * 0.22f, basePos + Vector3.up * 0.3f, 0.006f, Brass, 8);
            var shadeBase = basePos + Vector3.up * 0.26f;
            const int Bands = 14;
            for (int band = 0; band < Bands; band++)
            {
                float t0 = band / (float)Bands, t1 = (band + 1) / (float)Bands;
                float mid = 1f - Mathf.Abs((t0 + t1) / 2f - 0.45f) * 1.6f;
                k.Plain.Cylinder(shadeBase + Vector3.up * (0.16f * t0), Mathf.Lerp(0.13f, 0.08f, t0), Mathf.Lerp(0.13f, 0.08f, t1), 0.16f / Bands,
                                 new Color(0.62f, 0.42f, 0.22f) * (0.45f + 0.55f * mid * mid), 20, emissive: true, open: true);
            }
            var bulb = shadeBase + Vector3.up * 0.07f;
            k.Plain.Sphere(bulb, 0.025f, new Color(1f, 0.92f, 0.75f), emissive: true);
            lights.Add(new PointLight { Pos = bulb, Col = new Color(1.0f, 0.62f, 0.30f) * 0.75f, Radius = 0.55f, Lamp = true });
            Glows.Add((bulb, 0.55f, new Color(0.04f, 0.025f, 0.01f), false));   // warm haze
            MoteSources.Add((bulb, 0.6f));
        }

        /// <summary>A slim brass picture light, its strip glowing down onto the painting.</summary>
        void PictureLight(Kit k, Vector3 wallPos, Vector3 n, float width)
        {
            var across = Vector3.Cross(n, Vector3.up).normalized;
            k.Plain.Rod(wallPos, wallPos + n * 0.09f, 0.008f, Brass, 8);
            var bar = wallPos + n * 0.1f;
            k.Plain.Rod(bar - across * (width / 2), bar + across * (width / 2), 0.018f, Brass, 12);
            k.Plain.RoundBox(bar - Vector3.up * 0.017f, new Vector3(0.006f + Mathf.Abs(across.x) * width * 0.95f, 0.006f, 0.006f + Mathf.Abs(across.z) * width * 0.95f),
                             0.002f, new Color(1f, 0.88f, 0.62f), emissive: true, occ: false);
            lights.Add(new PointLight { Pos = bar - Vector3.up * 0.05f - n * 0.03f, Col = new Color(1.0f, 0.70f, 0.40f) * 0.22f, Radius = 0.35f });
        }

        // ---- curtains and soft furnishings --------------------------------------------------------------

        /// <summary>
        /// A heavy curtain hanging from the rod in vertical folds, gathered towards the side it is pulled to
        /// (side = +1: folds bunch towards z1... -1 towards z0). x = plane of the curtain (it hangs into the room).
        /// </summary>
        void Curtain(MeshBuilder b, float x, float z0, float z1, float yBottom, float yTop, Color col, int side)
        {
            const int NZ = 40, NY = 24;
            int baseIdx = b.Count;
            var P = new Vector3[NZ + 1, NY + 1];
            for (int j = 0; j <= NY; j++)
                for (int i = 0; i <= NZ; i++)
                {
                    float u = i / (float)NZ, v = j / (float)NY;
                    float z = Mathf.Lerp(z0, z1, u);
                    // Folds: deeper and tighter towards the gathered side and towards the bottom.
                    float gather = side > 0 ? u : 1f - u;
                    float depth = 0.025f + 0.03f * gather + 0.012f * (1f - v);
                    float fold = Mathf.Sin(u * Mathf.PI * 2f * 6.5f + v * 0.6f);
                    P[i, j] = new Vector3(x - depth * (fold * 0.5f + 0.5f), Mathf.Lerp(yBottom, yTop, v), z);
                }
            for (int j = 0; j <= NY; j++)
                for (int i = 0; i <= NZ; i++)
                {
                    var du = P[Mathf.Min(NZ, i + 1), j] - P[Mathf.Max(0, i - 1), j];
                    var dv = P[i, Mathf.Min(NY, j + 1)] - P[i, Mathf.Max(0, j - 1)];
                    var nrm = Vector3.Cross(dv, du).normalized;
                    if (nrm.x > 0) nrm = -nrm;   // facing into the room
                    b.VertLit(P[i, j], nrm, col, new Vector2(i / (float)NZ * 3f, j / (float)NY * 6f));
                }
            for (int j = 0; j < NY; j++)
                for (int i = 0; i < NZ; i++)
                {
                    int a = baseIdx + j * (NZ + 1) + i;
                    b.Tri(a, a + 1, a + NZ + 2);
                    b.Tri(a, a + NZ + 2, a + NZ + 1);
                }
            AddOcc(new Vector3(x - 0.03f, (yBottom + yTop) / 2, (z0 + z1) / 2), new Vector3(0.05f, yTop - yBottom, z1 - z0));
        }

        /// <summary>A wicker basket with rolled blankets standing in it.</summary>
        void BlanketBasket(Kit k, Vector3 p)
        {
            k.Wicker.Cylinder(p, 0.2f, 0.23f, 0.36f, Color.white, 24, open: true);
            k.Wicker.Cylinder(p + Vector3.up * 0.005f, 0.19f, 0.22f, 0.35f, Color.white * 0.7f, 24, open: true, inward: true);
            k.Wicker.Cylinder(p, 0.2f, 0.2f, 0.01f, Color.white * 0.6f, 24);
            k.Wool.Rod(p + new Vector3(-0.07f, 0.05f, -0.05f), p + new Vector3(-0.09f, 0.5f, -0.03f), 0.075f, Cream, 14);
            k.Wool.Rod(p + new Vector3(0.07f, 0.05f, 0.02f), p + new Vector3(0.1f, 0.46f, 0.05f), 0.07f, DeepRed, 14);
            k.Plaid.Rod(p + new Vector3(0f, 0.05f, 0.09f), p + new Vector3(-0.02f, 0.42f, 0.13f), 0.065f, Color.white, 14);
            AddOcc(p + Vector3.up * 0.2f, new Vector3(0.42f, 0.4f, 0.42f));
        }

        // ---- fireplace dressing -------------------------------------------------------------------------

        void FireTools(Kit k, Vector3 p)
        {
            k.Plain.Cylinder(p, 0.09f, 0.08f, 0.02f, Iron, 16);
            k.Plain.Rod(p, p + Vector3.up * 0.72f, 0.009f, Iron, 8);
            k.Plain.Sphere(p + Vector3.up * 0.74f, 0.022f, Brass);
            k.Plain.Rod(p + new Vector3(-0.07f, 0.62f, 0), p + new Vector3(0.07f, 0.62f, 0), 0.006f, Iron, 6);
            // Poker, brush, shovel hanging from the cross bar.
            k.Plain.Rod(p + new Vector3(-0.06f, 0.6f, 0.01f), p + new Vector3(-0.07f, 0.06f, 0.02f), 0.006f, Iron, 6);
            k.Plain.Rod(p + new Vector3(-0.07f, 0.1f, 0.02f), p + new Vector3(-0.04f, 0.05f, 0.03f), 0.006f, Iron, 6);
            k.Plain.Rod(p + new Vector3(0.0f, 0.6f, 0.03f), p + new Vector3(0.0f, 0.16f, 0.04f), 0.006f, Iron, 6);
            k.Plain.RoundBox(p + new Vector3(0.0f, 0.1f, 0.045f), new Vector3(0.06f, 0.11f, 0.035f), 0.01f, new Color(0.18f, 0.13f, 0.08f), occ: false);
            k.Plain.Rod(p + new Vector3(0.06f, 0.6f, -0.01f), p + new Vector3(0.06f, 0.16f, -0.01f), 0.006f, Iron, 6);
            k.Plain.RoundBox(p + new Vector3(0.06f, 0.09f, -0.01f), new Vector3(0.08f, 0.12f, 0.008f), 0.003f, Iron, occ: false);
        }

        void LogBasket(Kit k, Vector3 p)
        {
            k.Wicker.Cylinder(p, 0.24f, 0.27f, 0.3f, Color.white, 24, open: true);
            k.Wicker.Cylinder(p + Vector3.up * 0.005f, 0.23f, 0.26f, 0.29f, Color.white * 0.7f, 24, open: true, inward: true);
            var bark = new Color(0.36f, 0.25f, 0.16f);
            for (int i = 0; i < 5; i++)
            {
                float a = i * 1.26f;
                var c = p + new Vector3(Mathf.Cos(a) * 0.09f, 0.22f + 0.05f * (i % 2), Mathf.Sin(a) * 0.09f);
                k.Plain.Rod(c - new Vector3(0.03f, 0, 0.18f), c + new Vector3(0.03f, 0.02f, 0.18f), 0.04f + 0.01f * (i % 2), bark * (0.85f + 0.1f * i), 10);
            }
            AddOcc(p + Vector3.up * 0.16f, new Vector3(0.5f, 0.32f, 0.5f));
        }

        /// <summary>A pair of antlers on a wooden plaque, hung on the chimney above the mantel.</summary>
        void Antlers(Kit k, Vector3 p)
        {
            var bone = new Color(0.80f, 0.74f, 0.62f);
            k.Boards.RoundBox(p, new Vector3(0.04f, 0.26f, 0.2f), 0.02f, new Color(0.40f, 0.26f, 0.16f), occ: false);
            foreach (var s in new[] { -1f, 1f })
            {
                // Main beam: a sweeping curve outwards and up, with tines on its inner side.
                var prev = p + new Vector3(0.05f, 0.02f, s * 0.04f);
                Vector3 last = prev;
                for (int i = 1; i <= 8; i++)
                {
                    float t = i / 8f;
                    var q = p + new Vector3(0.05f + 0.12f * Mathf.Sin(t * 2.2f), 0.02f + 0.42f * t, s * (0.04f + 0.38f * Mathf.Sin(t * 1.6f)));
                    k.Plain.Rod(prev, q, 0.017f * (1f - 0.55f * t), bone, 8);
                    prev = q; last = q;
                    if (i == 3 || i == 5 || i == 7)
                    {
                        var tip = q + new Vector3(0.03f, 0.14f - 0.02f * i / 8f, -s * 0.05f);
                        k.Plain.Rod(q, tip, 0.009f, bone, 6);
                    }
                }
            }
        }

        static void PineCone(MeshBuilder b, Vector3 p)
        {
            var brown = new Color(0.30f, 0.19f, 0.10f);
            for (int i = 0; i < 5; i++)
                b.Sphere(p + new Vector3(0, 0.012f + i * 0.012f, 0), 0.022f * (1f - i * 0.14f), brown * (0.9f + 0.05f * i));
        }

        // ---- wall decor ---------------------------------------------------------------------------------

        /// <summary>A pair of traditional bentwood snowshoes, crossed, with leather bindings and lacing.</summary>
        void Snowshoes(Kit k, Vector3 p)
        {
            var wood = new Color(0.55f, 0.38f, 0.22f);
            var lace = new Color(0.55f, 0.45f, 0.30f);
            foreach (var tilt in new[] { -16f, 16f })
            {
                var rot = Quaternion.Euler(0, 0, tilt);
                Vector3 At(float x, float y) => p + rot * new Vector3(x, y, 0);
                Vector3 prev = At(0, 0.42f);
                const int N = 28;
                for (int i = 1; i <= N; i++)
                {
                    float a = i / (float)N * Mathf.PI * 2f;
                    var q = At(Mathf.Sin(a) * 0.13f * (Mathf.Cos(a) < 0 ? 0.7f + 0.3f * (1f + Mathf.Cos(a)) : 1f), Mathf.Cos(a) * 0.42f);
                    k.Plain.Rod(prev, q, 0.009f, wood, 6);
                    prev = q;
                }
                for (int j = -5; j <= 5; j++)
                {
                    float y = j * 0.06f;
                    float half = 0.12f * Mathf.Sqrt(Mathf.Max(0f, 1f - (y / 0.42f) * (y / 0.42f)));
                    k.Plain.Rod(At(-half, y), At(half, y), 0.0025f, lace, 4, caps: false);
                }
                for (int j = -2; j <= 2; j++)
                    k.Plain.Rod(At(j * 0.045f, -0.35f), At(j * 0.045f, 0.35f), 0.0025f, lace, 4, caps: false);
                k.Plain.RoundBox(At(0, 0.05f), new Vector3(0.12f, 0.08f, 0.02f), 0.006f, new Color(0.28f, 0.16f, 0.09f), rot, occ: false);
            }
        }

        /// <summary>A round wall clock with a wooden rim, cream dial and hands showing ten past ten.</summary>
        void WallClock(Kit k, Vector3 p, Vector3 n)
        {
            const float R = 0.16f;
            k.Boards.Rod(p, p + n * 0.05f, R + 0.025f, new Color(0.40f, 0.26f, 0.15f), 32);
            var across = Vector3.Cross(n, Vector3.up).normalized;
            var face = p + n * 0.052f;
            k.Clock.Quad(face - across * R - Vector3.up * R, face + across * R - Vector3.up * R, face + across * R + Vector3.up * R, face - across * R + Vector3.up * R,
                         n, Color.white, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), false, 6, 6);
            var hub = face + n * 0.004f;
            var hourDir = (Mathf.Sin(305f * Mathf.Deg2Rad) * across + Mathf.Cos(305f * Mathf.Deg2Rad) * Vector3.up).normalized;
            var minDir = (Mathf.Sin(60f * Mathf.Deg2Rad) * across + Mathf.Cos(60f * Mathf.Deg2Rad) * Vector3.up).normalized;
            k.Plain.Rod(hub, hub + hourDir * R * 0.5f, 0.006f, Iron, 6);
            k.Plain.Rod(hub + n * 0.003f, hub + n * 0.003f + minDir * R * 0.78f, 0.004f, Iron, 6);
            k.Plain.Sphere(hub, 0.01f, Brass);
        }

        // ---- the sofa -----------------------------------------------------------------------------------

        /// <summary>
        /// A big leather sofa where you sit: seat just below and in front of your head, the back behind you and a
        /// rolled arm at each side, with cushions, pillows and a knitted throw.
        /// </summary>
        void Sofa(Kit k, Vector3 p)
        {
            var hide = Color.white;
            const float Wd = 2.2f, D = 0.95f;
            // Base, back and arms.
            k.Leather.RoundBox(p + new Vector3(0, 0.22f, 0), new Vector3(Wd, 0.3f, D), 0.06f, hide * 0.8f);
            k.Leather.RoundBox(p + new Vector3(0, 0.62f, -D / 2 + 0.11f), new Vector3(Wd, 0.6f, 0.22f), 0.09f, hide * 0.85f);
            foreach (var s in new[] { -1f, 1f })
            {
                k.Leather.RoundBox(p + new Vector3(s * (Wd / 2 - 0.1f), 0.45f, 0.02f), new Vector3(0.2f, 0.3f, D - 0.04f), 0.08f, hide * 0.9f);
                k.Leather.Rod(p + new Vector3(s * (Wd / 2 - 0.1f), 0.62f, -D / 2 + 0.05f), p + new Vector3(s * (Wd / 2 - 0.1f), 0.62f, D / 2 - 0.02f), 0.085f, hide * 0.9f, 18);
            }
            // Three seat cushions and three back cushions.
            for (int i = -1; i <= 1; i++)
            {
                k.Leather.RoundBox(p + new Vector3(i * 0.6f, 0.43f, 0.06f), new Vector3(0.59f, 0.13f, D - 0.28f), 0.06f, hide);
                k.Leather.RoundBox(p + new Vector3(i * 0.6f, 0.72f, -D / 2 + 0.29f), new Vector3(0.58f, 0.46f, 0.16f), 0.07f, hide, Quaternion.Euler(-12f, 0, 0));
            }
            // Feet.
            foreach (var fx in new[] { -Wd / 2 + 0.1f, Wd / 2 - 0.1f })
                foreach (var fz in new[] { -D / 2 + 0.1f, D / 2 - 0.1f })
                    k.Plain.Cylinder(p + new Vector3(fx, 0f, fz), 0.022f, 0.03f, 0.07f, new Color(0.2f, 0.13f, 0.08f), 10);
            // Pillows and a knitted throw over the right arm.
            // Resting on the seat cushions against the back cushions, clear of the arms (inner faces at x ±0.9).
            k.Plaid.RoundBox(p + new Vector3(-0.66f, 0.69f, -0.03f), new Vector3(0.4f, 0.38f, 0.14f), 0.08f, Color.white, Quaternion.Euler(-10f, 15f, 6f), occ: false);
            k.Wool.RoundBox(p + new Vector3(0.62f, 0.68f, -0.03f), new Vector3(0.38f, 0.36f, 0.13f), 0.08f, ForestGreen * 1.2f, Quaternion.Euler(-12f, -18f, -5f), occ: false);
            k.Wool.RoundBox(p + new Vector3(Wd / 2 - 0.1f, 0.68f, 0.05f), new Vector3(0.28f, 0.04f, 0.6f), 0.02f, Cream, occ: false);
            k.Wool.RoundBox(p + new Vector3(Wd / 2 - 0.22f, 0.5f, 0.08f), new Vector3(0.04f, 0.34f, 0.56f), 0.02f, Cream, Quaternion.Euler(0, 0, 8f), occ: false);
        }
    }
}
