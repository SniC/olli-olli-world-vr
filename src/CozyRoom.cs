using System.Collections.Generic;
using UnityEngine;

namespace OlliVR
{
    /// <summary>
    /// Geometry and baked lighting of the wooden lodge around the screen, in the recentered room frame
    /// (origin at the recentered head, x right, y up, z towards the screen).
    ///
    /// Log walls on three sides, a vertical-plank feature wall behind the TV, a pitched roof with exposed trusses
    /// and a wrought-iron chandelier; a river-stone chimney with an open fire on the left; a big window onto a
    /// moonlit snowy mountain range on the right; a plank door with a skateboard leaning next to it behind you.
    /// Lighting is computed per vertex once (lamp, chandelier, moonlight, embers, ambient, corner darkening).
    /// The fire's flicker is a separate additive mesh whose brightness animates per frame. The view outside is a
    /// stack of layers at different distances (sky, mountains, forest, near pines) for real parallax.
    /// </summary>
    internal partial class CozyRoom
    {
        public struct Part { public Mesh Mesh; public Texture Tex; }

        public readonly List<Part> Opaque = new();
        /// <summary>Alpha-blended scenery outside the window, far to near.</summary>
        public readonly List<Part> Outside = new();
        public Mesh FireLight;              // additive, same surfaces, fire contribution only
        public Mesh TvLight;                // same surfaces, how much of the TV screen each point sees (lit live)
        Vector3 tvCentre; float tvHalfW, tvHalfH;
        public Vector3 FirePos;             // flames centre
        public float FireOpeningX, FireOpeningZ, FireOpeningY0;
        public Vector3 WindowCenter;        // on the wall plane
        public float WindowW, WindowH;
        public float RoomHalfW, ZBack, ZFront, Floor, Ceil;
        /// <summary>Solid furniture that blocks the screen glow (axis-aligned boxes, room frame).</summary>
        public readonly List<Bounds> Occluders = new();
        /// <summary>Candle flame positions (room frame) and size, drawn as animated flames each frame.</summary>
        public readonly List<(Vector3 pos, float scale)> Candles = new();
        /// <summary>Soft glow halos (bloom stand-in): position, radius, colour, follows the fire flicker.</summary>
        public readonly List<(Vector3 pos, float radius, Color colour, bool fire)> Glows = new();
        const float ChimW = 1.7f;

        const float EaveH = 2.4f, RidgeH = 3.9f, LogR = 0.125f, LogStep = 0.24f;
        float RoofY(float x) => Floor + EaveH + (RidgeH - EaveH) * Mathf.Clamp01(1f - Mathf.Abs(x) / RoomHalfW);

        // Light sources (room frame).
        struct PointLight { public Vector3 Pos; public Color Col; public float Radius; public bool Lamp, Fire, Window; public Vector3[] Samples; }
        float fireOpenZ0, fireOpenZ1, fireOpenY1;
        readonly List<PointLight> lights = new();
        PointLight fire;

        // The room layout uses the screen size seen at the first build; later size changes only move the TV.
        float refSw = -1f, refSh = -1f;
        float cabTopY;
        public Part? TvPart;
        public Vector3 LampPos;

        public void Build(float d, float screenCy, float sw, float sh, float floorY)
        {
            if (refSw < 0f) { refSw = sw; refSh = sh; }
            sw = refSw; sh = refSh;
            // Opaque meshes and the fire light are replaced when the new lighting bake finishes (TryFinish).
            foreach (var p in Outside) Object.Destroy(p.Mesh);
            Outside.Clear();
            bakeOcc.Clear(); recordOcc = true;

            Floor = floorY; Ceil = floorY + RidgeH;
            tvCentre = new Vector3(0, screenCy, d); tvHalfW = sw / 2; tvHalfH = sh / 2;
            RoomHalfW = 3.0f; ZBack = -2.6f; ZFront = d + 0.35f;
            float F = Floor, W = RoomHalfW;

            // ---- openings and lights ----
            lights.Clear();
            Occluders.Clear();
            Candles.Clear();
            Glows.Clear();
            var lampCol = new Color(1.0f, 0.60f, 0.28f) * 1.9f;
            float lampZ = ZFront - 0.35f;
            lights.Add(new PointLight { Pos = new Vector3(2.35f, F + 1.42f, lampZ), Col = lampCol, Radius = 0.75f, Lamp = true });
            LampPos = new Vector3(2.35f, F + 1.42f, lampZ);
            var chandelierPos = new Vector3(0f, F + 2.75f, 0.2f);
            var chandelierSamples = new Vector3[8];
            for (int i = 0; i < 8; i++) { float a = i / 8f * Mathf.PI * 2f; chandelierSamples[i] = chandelierPos + new Vector3(Mathf.Cos(a) * 0.48f, 0.12f, Mathf.Sin(a) * 0.48f); }
            lights.Add(new PointLight { Pos = chandelierPos, Col = new Color(1.0f, 0.62f, 0.30f) * 0.55f, Radius = 1.5f, Samples = chandelierSamples });

            float fireZ = Mathf.Min(0.9f, ZFront - 1.2f);
            WindowW = 2.3f; WindowH = 1.35f;
            WindowCenter = new Vector3(W, F + 1.5f, Mathf.Min(0.25f, lampZ - 0.4f - WindowW / 2));
            // Night skylight through the window (an area light over the opening, so the mullions cast shadows).
            lights.Add(new PointLight { Pos = WindowCenter, Col = new Color(0.16f, 0.22f, 0.42f) * 1.1f, Radius = 2.2f, Window = true });

            float chimX1 = -W + 0.5f;           // chimney front face
            float openW = 0.85f, openH = 0.65f;
            FireOpeningX = chimX1; FireOpeningZ = fireZ; FireOpeningY0 = F + 0.15f;
            FirePos = new Vector3(chimX1 - 0.25f, F + 0.35f, fireZ);
            // The fire sits inside the firebox; it only lights what it can see through the opening.
            fire = new PointLight { Pos = new Vector3(chimX1 - 0.24f, F + 0.38f, fireZ), Col = new Color(1.0f, 0.42f, 0.12f) * 1.25f, Radius = 1.5f, Fire = true };
            fireOpenZ0 = fireZ - openW / 2; fireOpenZ1 = fireZ + openW / 2; fireOpenY1 = FireOpeningY0 + openH;
            Glows.Add((new Vector3(chimX1 + 0.02f, FireOpeningY0 + openH * 0.4f, fireZ), 0.55f, new Color(0.20f, 0.08f, 0.02f), true));
            // Most of the fire light is baked (respects surface colours); only a smaller part flickers additively.
            lights.Add(fire);

            var doorRect = new Rect(0.95f, F, 1.0f, 2.05f);                 // back wall, x/y
            var windowRect = new Rect(WindowCenter.z - WindowW / 2, WindowCenter.y - WindowH / 2, WindowW, WindowH); // right wall, z/y

            // ---- builders (one per texture) ----
            var floorB = new MeshBuilder(this, RoomTextures.BoardGrain);
            var boards = new MeshBuilder(this, RoomTextures.BoardGrain);
            var leather = new MeshBuilder(this, RoomTextures.Leather);
            var sheep = new MeshBuilder(this, RoomTextures.Sheepskin);
            var painting = new MeshBuilder(this, RoomTextures.Painting);
            var wood = new MeshBuilder(this, RoomTextures.Wood);
            var logs = new MeshBuilder(this, RoomTextures.Log);
            var plain = new MeshBuilder(this, RoomTextures.White);
            var rug = new MeshBuilder(this, RoomTextures.Rug);
            var stone = new MeshBuilder(this, RoomTextures.Stone);
            var plaid = new MeshBuilder(this, RoomTextures.Plaid);
            var deck = new MeshBuilder(this, RoomTextures.Deck);
            var sky = new MeshBuilder(this, RoomTextures.NightSky);

            var timber = new Color(0.50f, 0.34f, 0.20f);
            var pine = new Color(0.80f, 0.64f, 0.48f);
            var chinking = new Color(0.55f, 0.48f, 0.38f);
            var iron = new Color(0.07f, 0.06f, 0.06f);

            // ---- floor: individual oak boards, staggered joints, a dark underlay showing in the gaps ----
            var rngB = new System.Random(21);
            const float FloorBoardW = 0.165f, Gap = 0.003f;
            for (float x = -W; x < W - 0.01f; x += FloorBoardW + Gap)
            {
                float bw = Mathf.Min(FloorBoardW, W - x);
                float z = ZBack - (float)rngB.NextDouble() * 1.2f;
                while (z < ZFront)
                {
                    float len = 1.2f + (float)rngB.NextDouble() * 1.6f;
                    float z0 = Mathf.Max(z, ZBack), z1 = Mathf.Min(z + len, ZFront);
                    if (z1 - z0 > 0.05f)
                    {
                        var tone = new Color(0.86f, 0.80f, 0.72f) * (0.88f + 0.22f * (float)rngB.NextDouble());
                        floorB.Board(new Vector3(x + bw / 2, F - 0.011f, (z0 + z1) / 2), Vector3.forward, Vector3.right, Vector3.up,
                                     z1 - z0 - Gap, bw, 0.022f, tone, (float)rngB.NextDouble() * 8f, rngB.Next(4), 2.0f);
                    }
                    z += len;
                }
            }
            plain.Grid(new Vector3(-W, F - 0.02f, ZBack), Vector3.right, Vector3.forward, 2 * W, ZFront - ZBack, Vector3.up,
                       new Color(0.03f, 0.025f, 0.02f), shell: true, step: 0.5f);

            // ---- log walls (left, right, back), chinking behind them ----
            float logTop = F + EaveH;
            plain.Grid(new Vector3(-W, F, ZFront), Vector3.back, Vector3.up, ZFront - ZBack, EaveH, Vector3.right, chinking, shell: true);
            plain.Grid(new Vector3(W, F, ZBack), Vector3.forward, Vector3.up, ZFront - ZBack, EaveH, Vector3.left, chinking, shell: true,
                       hole: new Rect(windowRect.x - ZBack, windowRect.y - F, windowRect.width, windowRect.height));
            plain.Grid(new Vector3(-W, F, ZBack), Vector3.right, Vector3.up, 2 * W, EaveH, Vector3.forward, chinking, shell: true,
                       hole: new Rect(doorRect.x + W, doorRect.y - F, doorRect.width, doorRect.height));
            int row = 0;
            for (float y = F + LogStep / 2; y < logTop - 0.05f; y += LogStep, row++)
            {
                var tone = pine * (0.92f + 0.08f * ((row * 37) % 5) / 4f);
                // Left wall: logs along z, cut where the chimney stands (they would show inside the firebox).
                logs.LogRun(new Vector3(-W, y, ZBack), new Vector3(-W, y, fireZ - ChimW / 2), Vector3.right, LogR, tone);
                logs.LogRun(new Vector3(-W, y, fireZ + ChimW / 2), new Vector3(-W, y, ZFront), Vector3.right, LogR, tone);
                // Right wall: split around the window.
                if (y + LogR > windowRect.yMin && y - LogR < windowRect.yMax)
                {
                    logs.LogRun(new Vector3(W, y, ZBack), new Vector3(W, y, windowRect.xMin), Vector3.left, LogR, tone);
                    logs.LogRun(new Vector3(W, y, windowRect.xMax), new Vector3(W, y, ZFront), Vector3.left, LogR, tone);
                }
                else logs.LogRun(new Vector3(W, y, ZBack), new Vector3(W, y, ZFront), Vector3.left, LogR, tone);
                // Back wall: logs along x, split around the door.
                if (y - LogR < doorRect.yMax)
                {
                    logs.LogRun(new Vector3(-W, y, ZBack), new Vector3(doorRect.xMin, y, ZBack), Vector3.forward, LogR, tone);
                    logs.LogRun(new Vector3(doorRect.xMax, y, ZBack), new Vector3(W, y, ZBack), Vector3.forward, LogR, tone);
                }
                else logs.LogRun(new Vector3(-W, y, ZBack), new Vector3(W, y, ZBack), Vector3.forward, LogR, tone);
            }

            // ---- board walls: the dark-stained feature wall behind the TV (full gable) and the back gable ----
            const float WallBoardW = 0.14f;
            for (float x = -W; x < W - 0.01f; x += WallBoardW + Gap)
            {
                float bw = Mathf.Min(WallBoardW, W - x);
                float top = Mathf.Max(RoofY(x), RoofY(x + bw)) + 0.03f;
                var tone = new Color(0.56f, 0.42f, 0.32f) * (0.85f + 0.25f * (float)rngB.NextDouble());
                boards.Board(new Vector3(x + bw / 2, (F + top) / 2, ZFront - 0.011f), Vector3.up, Vector3.right, Vector3.back,
                             top - F, bw, 0.022f, tone, (float)rngB.NextDouble() * 8f, rngB.Next(4), 2.0f);
                if (top > logTop + 0.05f)
                    boards.Board(new Vector3(x + bw / 2, (logTop + top) / 2, ZBack + 0.011f), Vector3.up, Vector3.right, Vector3.forward,
                                 top - logTop, bw, 0.022f, pine * (0.8f + 0.15f * (float)rngB.NextDouble()), (float)rngB.NextDouble() * 8f, rngB.Next(4), 2.0f);
            }
            plain.Grid(new Vector3(W, F, ZFront + 0.002f), Vector3.left, Vector3.up, 2 * W, RidgeH, Vector3.back,
                       new Color(0.03f, 0.025f, 0.02f), shell: true, step: 0.5f);

            // ---- pitched roof: boards along the room on both slopes, ridge beam and trusses ----
            float rise = RidgeH - EaveH, slopeLen = Mathf.Sqrt(W * W + rise * rise);
            var slopeL = new Vector3(W, rise, 0) / slopeLen;          // up the left slope
            var slopeR = new Vector3(-W, rise, 0) / slopeLen;         // up the right slope
            var nL = new Vector3(rise, -W, 0).normalized;             // left roof normal, into the room
            var nR = new Vector3(-rise, -W, 0).normalized;
            const float RoofBoardW = 0.15f;
            for (float s = 0f; s < slopeLen - 0.02f; s += RoofBoardW + Gap)
            {
                float bw = Mathf.Min(RoofBoardW, slopeLen - s);
                var cL = new Vector3(-W, F + EaveH, (ZFront + ZBack) / 2) + slopeL * (s + bw / 2) - nL * 0.011f;
                var cR = new Vector3(W, F + EaveH, (ZFront + ZBack) / 2) + slopeR * (s + bw / 2) - nR * 0.011f;
                boards.Board(cL, Vector3.forward, slopeL, nL, ZFront - ZBack, bw, 0.022f, pine * (0.82f + 0.2f * (float)rngB.NextDouble()),
                             (float)rngB.NextDouble() * 8f, rngB.Next(4), 2.0f);
                boards.Board(cR, Vector3.forward, slopeR, nR, ZFront - ZBack, bw, 0.022f, pine * (0.82f + 0.2f * (float)rngB.NextDouble()),
                             (float)rngB.NextDouble() * 8f, rngB.Next(4), 2.0f);
            }
            logs.Box(new Vector3(0, F + RidgeH - 0.14f, (ZFront + ZBack) / 2), new Vector3(0.2f, 0.26f, ZFront - ZBack), timber);
            float angle = Mathf.Atan2(rise, W) * Mathf.Rad2Deg;
            for (float z = ZBack + 0.9f; z < ZFront - 0.5f; z += 1.6f)
            {
                // Tie beam at eave height, king post, and a rafter under each slope.
                logs.Box(new Vector3(0, F + EaveH - 0.09f, z), new Vector3(2 * W, 0.18f, 0.16f), timber);
                logs.Box(new Vector3(0, F + EaveH + rise / 2, z), new Vector3(0.14f, rise, 0.14f), timber);
                var midL = new Vector3(-W / 2, F + EaveH + rise / 2, z) + new Vector3(rise, -W, 0).normalized * 0.11f;
                var midR = new Vector3(W / 2, F + EaveH + rise / 2, z) + new Vector3(-rise, -W, 0).normalized * 0.11f;
                logs.BoxRotated(midL, new Vector3(slopeLen, 0.2f, 0.15f), Quaternion.Euler(0, 0, angle), timber);
                logs.BoxRotated(midR, new Vector3(slopeLen, 0.2f, 0.15f), Quaternion.Euler(0, 0, -angle), timber);
                // Angled braces from the king post to the tie beam.
                logs.BoxRotated(new Vector3(-0.45f, F + EaveH + 0.35f, z), new Vector3(1.0f, 0.1f, 0.1f), Quaternion.Euler(0, 0, 40f), timber * 0.95f);
                logs.BoxRotated(new Vector3(0.45f, F + EaveH + 0.35f, z), new Vector3(1.0f, 0.1f, 0.1f), Quaternion.Euler(0, 0, -40f), timber * 0.95f);
            }

            // ---- rug ----
            float rugZ = Mathf.Lerp(0f, ZFront - 0.5f, 0.45f);
            rug.Quad(new Vector3(-1.5f, F + 0.006f, rugZ - 1.0f), new Vector3(1.5f, F + 0.006f, rugZ - 1.0f),
                     new Vector3(1.5f, F + 0.006f, rugZ + 1.0f), new Vector3(-1.5f, F + 0.006f, rugZ + 1.0f),
                     Vector3.up, Color.white, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), subdiv: 48);

            // ---- TV around the screen (the screen image is the TV's panel) ----
            float screenBottom = screenCy - sh / 2;
            var tvBlack = new Color(0.05f, 0.05f, 0.06f);
            const float Bezel = 0.03f;
            float tvZ = d + 0.03f;   // bezel front just behind the screen plane
            // The TV itself (bezel, back, LED, stand) is built separately in BuildTv, so a change of the game's
            // field of view (screen size) only rebuilds the TV, not the whole baked room. Here: its shadow volume.
            AddOcc(new Vector3(0, screenCy, tvZ + 0.01f), new Vector3(sw + 2 * Bezel, sh + 2 * Bezel, 0.07f));

            // ---- TV cabinet: legs, open compartments, a door, books inside, a console ----
            var cabCol = new Color(0.80f, 0.60f, 0.42f);
            var cabInside = cabCol * 0.55f;
            const float LegH = 0.14f, Dc = 0.42f, T = 0.025f;
            float cabW = Mathf.Max(1.8f, sw + 0.1f);
            float cabH = Mathf.Clamp(screenBottom - Bezel - F - LegH - 0.22f, 0.38f, 0.6f);
            float cabZ = d + 0.12f;
            float y0 = F + LegH, y1 = y0 + cabH;
            float hw = cabW / 2;
            float zf = cabZ - Dc / 2, zb = cabZ + Dc / 2;
            // Tapered legs.
            foreach (var lx in new[] { -hw + 0.08f, hw - 0.08f })
                foreach (var lz in new[] { zf + 0.06f, zb - 0.06f })
                    plain.Cylinder(new Vector3(lx, F, lz), 0.013f, 0.022f, LegH, new Color(0.28f, 0.18f, 0.11f), 10);
            // Carcass: top, bottom, sides, back.
            logs.Box(new Vector3(0, y1 - T / 2, cabZ), new Vector3(cabW, T, Dc), cabCol);
            logs.Box(new Vector3(0, y0 + T / 2, cabZ), new Vector3(cabW, T, Dc), cabCol);
            logs.Box(new Vector3(-hw + T / 2, (y0 + y1) / 2, cabZ), new Vector3(T, cabH, Dc), cabCol);
            logs.Box(new Vector3(hw - T / 2, (y0 + y1) / 2, cabZ), new Vector3(T, cabH, Dc), cabCol);
            logs.Box(new Vector3(0, (y0 + y1) / 2, zb - 0.005f), new Vector3(cabW - 2 * T, cabH - 2 * T, 0.01f), cabInside);
            Occluders.Add(new Bounds(new Vector3(0, (y0 + y1) / 2, cabZ), new Vector3(cabW, cabH, Dc)));
            // Two dividers make three compartments.
            float xd1 = -cabW / 6, xd2 = cabW / 6;
            logs.Box(new Vector3(xd1, (y0 + y1) / 2, cabZ + 0.01f), new Vector3(T, cabH - 2 * T, Dc - 0.02f), cabCol * 0.9f);
            logs.Box(new Vector3(xd2, (y0 + y1) / 2, cabZ + 0.01f), new Vector3(T, cabH - 2 * T, Dc - 0.02f), cabCol * 0.9f);
            // Left compartment: a closed door with a brass knob.
            float doorX0 = -hw + T + 0.004f, doorX1 = xd1 - T / 2 - 0.004f;
            logs.Box(new Vector3((doorX0 + doorX1) / 2, (y0 + y1) / 2, zf + 0.01f), new Vector3(doorX1 - doorX0, cabH - 2 * T - 0.008f, 0.02f), cabCol * 1.08f);
            plain.Sphere(new Vector3(doorX1 - 0.06f, (y0 + y1) / 2, zf - 0.008f), 0.014f, new Color(0.75f, 0.58f, 0.25f));
            // Middle compartment: a game console with a glowing light strip.
            float midY = y0 + T;
            plain.Box(new Vector3(0, midY + 0.035f, cabZ), new Vector3(0.32f, 0.07f, 0.26f), new Color(0.12f, 0.12f, 0.14f));
            plain.Box(new Vector3(0, midY + 0.035f, cabZ - 0.131f), new Vector3(0.2f, 0.006f, 0.002f), new Color(0.35f, 0.65f, 1.0f) * 0.8f, emissive: true);
            // Right compartment: books standing and a small stack lying down.
            var bookCols = new[]
            {
                new Color(0.62f, 0.22f, 0.18f), new Color(0.22f, 0.36f, 0.52f), new Color(0.80f, 0.64f, 0.30f),
                new Color(0.30f, 0.45f, 0.30f), new Color(0.50f, 0.30f, 0.45f), new Color(0.85f, 0.80f, 0.70f),
                new Color(0.25f, 0.25f, 0.30f), new Color(0.70f, 0.40f, 0.20f),
            };
            float innerH = cabH - 2 * T;
            float bx = xd2 + T / 2 + 0.01f;
            for (int i = 0; i < 6; i++)
            {
                float bw = 0.03f + 0.012f * ((i * 5) % 3), bh = Mathf.Min(innerH - 0.02f, 0.21f + 0.035f * ((i * 7) % 3));
                float bd = 0.16f + 0.03f * (i % 2);
                plain.Box(new Vector3(bx + bw / 2, y0 + T + bh / 2, zf + 0.02f + bd / 2), new Vector3(bw, bh, bd), bookCols[i]);
                bx += bw + 0.003f;
            }
            float sy = y0 + T;
            for (int i = 0; i < 3; i++)
            {
                float th = 0.035f + 0.01f * (i % 2);
                plain.Box(new Vector3(hw - T - 0.17f + 0.01f * i, sy + th / 2, zf + 0.13f), new Vector3(0.24f - 0.02f * i, th, 0.17f), bookCols[5 + i]);
                sy += th;
            }

            // ---- TV stand on the cabinet ----
            cabTopY = y1;

            // ---- snake plant on the floor left of the cabinet (anything in front of the screen would be hidden by it) ----
            var potPos = new Vector3(-hw - 0.32f, F, cabZ - 0.05f);
            var potCol = new Color(0.88f, 0.84f, 0.76f);
            Occluders.Add(new Bounds(potPos + new Vector3(0, 0.195f, 0), new Vector3(0.32f, 0.39f, 0.32f)));
            AddOcc(potPos + new Vector3(0, 0.195f, 0), new Vector3(0.28f, 0.39f, 0.28f));
            plain.Cylinder(potPos, 0.13f, 0.17f, 0.36f, potCol, 24);
            plain.Cylinder(potPos + new Vector3(0, 0.36f, 0), 0.178f, 0.178f, 0.03f, potCol * 0.95f, 24, open: true);
            plain.Cylinder(potPos + new Vector3(0, 0.33f, 0), 0.165f, 0.165f, 0.005f, new Color(0.18f, 0.12f, 0.08f), 24);
            var rngP = new System.Random(7);
            for (int i = 0; i < 13; i++)
            {
                float yaw = i * 27.7f + (float)rngP.NextDouble() * 20f;
                float h = 0.55f + 0.40f * (float)rngP.NextDouble();
                float lean = 0.05f + 0.14f * (float)rngP.NextDouble();
                float r = 0.02f + 0.08f * (float)rngP.NextDouble();
                float rad = yaw * Mathf.Deg2Rad;
                var radial = new Vector3(Mathf.Cos(rad), 0, Mathf.Sin(rad));
                plain.Blade(potPos + new Vector3(0, 0.33f, 0) + radial * r, radial, h, 0.07f + 0.025f * (float)rngP.NextDouble(), lean, yaw + 70f);
            }

            // ---- floor lamp: dense shade, light escapes through the open top and bottom ----
            foreach (var l in lights)
            {
                if (!l.Lamp) continue;
                var basePos = new Vector3(l.Pos.x, F, l.Pos.z);
                var dark = new Color(0.12f, 0.10f, 0.09f);
                plain.Cylinder(basePos, 0.16f, 0.16f, 0.03f, dark, 20);
                plain.Cylinder(basePos + new Vector3(0, 0.03f, 0), 0.015f, 0.015f, l.Pos.y - F - 0.03f, dark, 8);
                var shadeBase = new Vector3(l.Pos.x, l.Pos.y - 0.17f, l.Pos.z);
                // Outer fabric: only a faint glow through it.
                // Outer fabric: lit from inside, a warm glow strongest at the height of the bulb, dimmer at the rims.
                const int Bands = 20;
                for (int band = 0; band < Bands; band++)
                {
                    float t0 = band / (float)Bands, t1 = (band + 1) / (float)Bands;
                    float mid = 1f - Mathf.Abs((t0 + t1) / 2f - 0.5f) * 1.6f;
                    var glowCol = new Color(0.46f, 0.25f, 0.09f) * (0.4f + 0.6f * mid * mid);
                    plain.Cylinder(shadeBase + new Vector3(0, 0.34f * t0, 0), Mathf.Lerp(0.22f, 0.20f, t0), Mathf.Lerp(0.22f, 0.20f, t1), 0.34f / Bands, glowCol, 24, emissive: true, open: true);
                }
                // Bright inside, seen through the openings.
                plain.Cylinder(shadeBase, 0.214f, 0.194f, 0.34f, new Color(1.0f, 0.82f, 0.55f), 24, emissive: true, open: true, inward: true);
                plain.Sphere(l.Pos, 0.035f, new Color(1f, 0.95f, 0.85f), emissive: true);
            }

            // ---- river-stone chimney and fireplace (left wall) ----
            float cx0 = -W, cx1 = chimX1, fz = fireZ, cw = ChimW;
            float chimTop = RoofY(cx1) + 0.4f;
            float oy0 = FireOpeningY0;
            var stoneCol = Color.white;
            // Shadow-casting volume of the chimney: two pillars and the mass above the opening (the firebox stays open).
            AddOcc(new Vector3((cx0 + cx1) / 2, (F + chimTop) / 2, fz - (cw + openW) / 4), new Vector3(cx1 - cx0, chimTop - F, (cw - openW) / 2));
            AddOcc(new Vector3((cx0 + cx1) / 2, (F + chimTop) / 2, fz + (cw + openW) / 4), new Vector3(cx1 - cx0, chimTop - F, (cw - openW) / 2));
            AddOcc(new Vector3((cx0 + cx1) / 2, (oy0 + openH + chimTop) / 2, fz), new Vector3(cx1 - cx0, chimTop - oy0 - openH, openW));
            // One continuous front with a hole for the opening (no seams between pieces).
            stone.StoneFace(new Vector3(cx1, F, fz - cw / 2), new Vector3(cx1, chimTop, fz + cw / 2), Vector3.right, stoneCol, 1.1f,
                            hole: new Rect(fz - openW / 2, oy0, openW, openH));
            stone.StoneFace(new Vector3(cx0, F, fz - cw / 2), new Vector3(cx1, chimTop, fz - cw / 2), Vector3.back, stoneCol, 1.1f);
            stone.StoneFace(new Vector3(cx0, F, fz + cw / 2), new Vector3(cx1, chimTop, fz + cw / 2), Vector3.forward, stoneCol, 1.1f);
            // Firebox interior (sooty).
            var soot = new Color(0.30f, 0.26f, 0.24f); // sooty stone lining
            float inner = cx0 + 0.06f;
            // Firebox lining: real displaced stone on the back and both sides (it faces into the firebox).
            stone.StoneFace(new Vector3(inner, oy0, fz - openW / 2), new Vector3(inner, oy0 + openH, fz + openW / 2), Vector3.right, soot, 0.8f, 0.03f);
            stone.StoneFace(new Vector3(inner, oy0, fz - openW / 2), new Vector3(cx1, oy0 + openH, fz - openW / 2), Vector3.forward, soot, 0.8f, 0.03f);
            stone.StoneFace(new Vector3(inner, oy0, fz + openW / 2), new Vector3(cx1, oy0 + openH, fz + openW / 2), Vector3.back, soot, 0.8f, 0.03f);
            stone.Quad(new Vector3(inner, oy0 + openH, fz - openW / 2), new Vector3(cx1, oy0 + openH, fz - openW / 2), new Vector3(cx1, oy0 + openH, fz + openW / 2), new Vector3(inner, oy0 + openH, fz + openW / 2), Vector3.down, soot);
            stone.Quad(new Vector3(inner, oy0, fz - openW / 2), new Vector3(inner, oy0, fz + openW / 2), new Vector3(cx1, oy0, fz + openW / 2), new Vector3(cx1, oy0, fz - openW / 2), Vector3.up, soot * 1.5f);
            var logCol = new Color(0.30f, 0.18f, 0.10f);
            plain.LogZ(new Vector3(cx0 + 0.24f, oy0 + 0.06f, fz), 0.055f, 0.6f, logCol);
            plain.LogZ(new Vector3(cx0 + 0.32f, oy0 + 0.06f, fz + 0.05f), 0.05f, 0.5f, logCol * 0.9f);
            plain.Box(new Vector3(cx0 + 0.28f, oy0 + 0.012f, fz), new Vector3(0.3f, 0.02f, 0.6f), new Color(1.0f, 0.35f, 0.08f), emissive: true);
            // Hearth slab and a rough timber mantel with candles and a lantern.
            stone.Box(new Vector3((cx0 + cx1 + 0.4f) / 2, F + 0.03f, fz), new Vector3(cx1 - cx0 + 0.4f, 0.06f, cw + 0.3f), stoneCol * 0.85f);
            float mantelY = F + 1.2f;
            logs.Box(new Vector3((cx0 + cx1 + 0.14f) / 2, mantelY, fz), new Vector3(cx1 - cx0 + 0.14f, 0.14f, cw + 0.25f), timber);
            for (int i = -1; i <= 1; i += 2)
            {
                var cp = new Vector3(cx1 + 0.075f, mantelY + 0.07f, fz + i * 0.6f); // in front of the stones
                float ch = 0.14f + 0.04f * (i + 1);
                plain.Cylinder(cp, 0.035f, 0.035f, ch, new Color(0.95f, 0.90f, 0.80f), 10);
                plain.Cylinder(cp + new Vector3(0, ch, 0), 0.0035f, 0.0035f, 0.012f, new Color(0.08f, 0.06f, 0.05f), 6);
                AddCandle(cp + new Vector3(0, ch + 0.012f, 0), 1f, 0.35f);
            }
            Lantern(plain, new Vector3(cx1 + 0.07f, mantelY + 0.07f, fz + 0.15f), iron);
            // Evergreen garland with warm fairy lights, draped in two swags along the front of the mantel.
            {
                float gz0 = fz - cw / 2 - 0.05f, gz1 = fz + cw / 2 + 0.05f, gx = cx1 + 0.16f;
                int n = 0;
                for (float z = gz0; z <= gz1; z += 0.032f, n++)
                {
                    float t = (z - gz0) / (gz1 - gz0);
                    float sag = 0.09f * Mathf.Abs(Mathf.Sin(t * Mathf.PI * 2f));
                    var gp = new Vector3(gx + 0.015f * Mathf.Sin(n * 2.1f), mantelY - 0.05f - sag, z);
                    var green = new Color(0.10f, 0.26f, 0.14f) * (0.8f + 0.35f * Mathf.Abs(Mathf.Sin(n * 1.7f)));
                    plain.Sphere(gp, 0.03f, green);
                    plain.Sphere(gp + new Vector3(0.012f, 0.018f * Mathf.Sin(n * 3.3f), 0.01f), 0.022f, green * 1.15f);
                    if (n % 3 == 0)
                    {
                        var bulb = gp + new Vector3(0.03f, -0.012f, 0);
                        plain.Sphere(bulb, 0.007f, new Color(1f, 0.78f, 0.42f), emissive: true);
                        Glows.Add((bulb, 0.045f, new Color(0.09f, 0.055f, 0.02f), false));
                    }
                }
                lights.Add(new PointLight { Pos = new Vector3(gx + 0.08f, mantelY - 0.1f, fz), Col = new Color(1.0f, 0.65f, 0.32f) * 0.16f, Radius = 0.8f });
            }
            // Firewood stacked beside the chimney.
            var rngW = new System.Random(11);
            AddOcc(new Vector3(-W + LogR + 0.27f, F + 0.25f, fz + cw / 2 + 0.35f), new Vector3(0.5f, 0.5f, 0.48f));
            float wz = fz + cw / 2 + 0.32f;
            for (int r = 0; r < 4; r++)
                for (int i = 0; i < 4 - r; i++)
                {
                    float lx = -W + LogR + 0.08f + i * 0.13f + r * 0.065f, ly = F + 0.065f + r * 0.115f;
                    var lc = new Color(0.42f, 0.28f, 0.17f) * (0.8f + 0.4f * (float)rngW.NextDouble());
                    plain.LogZ(new Vector3(lx, ly, wz + 0.03f * ((i + r) % 2)), 0.062f, 0.48f, lc);
                }

            // ---- wrought-iron chandelier under the ridge ----
            float ringR = 0.48f;
            plain.Cylinder(chandelierPos - new Vector3(0, 0.02f, 0), ringR, ringR, 0.04f, iron, 32, open: true);
            plain.Box(chandelierPos, new Vector3(2 * ringR, 0.018f, 0.018f), iron);
            plain.Box(chandelierPos, new Vector3(0.018f, 0.018f, 2 * ringR), iron);
            plain.Box(new Vector3(0, (chandelierPos.y + F + RidgeH - 0.27f) / 2, chandelierPos.z), new Vector3(0.02f, F + RidgeH - 0.27f - chandelierPos.y, 0.02f), iron);
            for (int i = 0; i < 8; i++)
            {
                float a = i / 8f * Mathf.PI * 2f;
                var cp = chandelierPos + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * ringR + new Vector3(0, 0.02f, 0);
                plain.Cylinder(cp - new Vector3(0, 0.005f, 0), 0.03f, 0.03f, 0.01f, iron, 10);
                plain.Cylinder(cp, 0.018f, 0.018f, 0.09f, new Color(0.95f, 0.90f, 0.80f), 8);
                plain.Cylinder(cp + new Vector3(0, 0.09f, 0), 0.003f, 0.003f, 0.01f, new Color(0.08f, 0.06f, 0.05f), 6);
                AddCandle(cp + new Vector3(0, 0.10f, 0), 0.8f, 0f); // the chandelier light covers these
            }

            // ---- leather club armchair (back left), rolled arms, loose cushions, plaid throw ----
            var hide = new Color(1f, 1f, 1f);
            var chair = new Vector3(-1.75f, F, -1.45f);
            leather.RoundBox(chair + new Vector3(0, 0.24f, 0), new Vector3(0.86f, 0.3f, 0.82f), 0.06f, hide * 0.85f);
            leather.RoundBox(chair + new Vector3(0, 0.44f, 0.05f), new Vector3(0.6f, 0.13f, 0.66f), 0.06f, hide);
            leather.RoundBox(chair + new Vector3(0, 0.74f, -0.33f), new Vector3(0.86f, 0.74f, 0.17f), 0.08f, hide * 0.9f);
            leather.RoundBox(chair + new Vector3(0, 0.72f, -0.2f), new Vector3(0.58f, 0.52f, 0.14f), 0.07f, hide, Quaternion.Euler(-10f, 0, 0));
            foreach (var sx in new[] { -1f, 1f })
            {
                leather.RoundBox(chair + new Vector3(sx * 0.36f, 0.47f, 0.02f), new Vector3(0.15f, 0.34f, 0.8f), 0.07f, hide * 0.95f);
                leather.Rod(chair + new Vector3(sx * 0.37f, 0.64f, -0.38f), chair + new Vector3(sx * 0.37f, 0.64f, 0.41f), 0.06f, hide * 0.95f, 16);
            }
            foreach (var fx in new[] { -0.36f, 0.36f })
                foreach (var fzz in new[] { -0.34f, 0.34f })
                    plain.Cylinder(chair + new Vector3(fx, 0f, fzz), 0.018f, 0.026f, 0.09f, timber * 0.6f, 10);
            plaid.RoundBox(chair + new Vector3(0.12f, 0.86f, -0.215f), new Vector3(0.5f, 0.5f, 0.03f), 0.012f, Color.white, Quaternion.Euler(-10f, 0, 0), occ: false);
            plaid.RoundBox(chair + new Vector3(0.36f, 0.6f, 0.1f), new Vector3(0.2f, 0.32f, 0.45f), 0.03f, Color.white, Quaternion.Euler(0, 0, -8f), occ: false);

            // ---- round side table by the chair: books, a mug, reading glasses ----
            var side = new Vector3(-1.05f, F, -1.2f);
            boards.Cylinder(side + new Vector3(0, 0.52f, 0), 0.24f, 0.24f, 0.035f, timber * 1.2f, 32);
            plain.Cylinder(side + new Vector3(0, 0.04f, 0), 0.035f, 0.03f, 0.48f, timber * 0.7f, 12);
            plain.Cylinder(side, 0.16f, 0.13f, 0.04f, timber * 0.7f, 24);
            AddOcc(side + new Vector3(0, 0.27f, 0), new Vector3(0.3f, 0.5f, 0.3f));
            float st = F + 0.555f;
            plain.RoundBox(new Vector3(side.x - 0.05f, st + 0.02f, side.z - 0.03f), new Vector3(0.2f, 0.04f, 0.14f), 0.006f, new Color(0.25f, 0.32f, 0.45f), Quaternion.Euler(0, 12f, 0), occ: false);
            plain.RoundBox(new Vector3(side.x - 0.05f, st + 0.055f, side.z - 0.03f), new Vector3(0.18f, 0.03f, 0.13f), 0.006f, new Color(0.60f, 0.20f, 0.16f), Quaternion.Euler(0, -5f, 0), occ: false);
            Mug(plain, new Vector3(side.x + 0.12f, st, side.z + 0.06f), new Color(0.88f, 0.86f, 0.80f));

            // ---- coffee table on the rug: thick top, turned legs; a gamepad, a mug, a book and a candle jar ----
            var ct = new Vector3(0f, F, 1.08f);
            boards.RoundBox(ct + new Vector3(0, 0.42f, 0), new Vector3(1.05f, 0.055f, 0.56f), 0.015f, timber * 1.25f);
            foreach (var lx in new[] { -0.45f, 0.45f })
                foreach (var lz in new[] { -0.22f, 0.22f })
                {
                    boards.Cylinder(ct + new Vector3(lx, 0f, lz), 0.03f, 0.024f, 0.395f, timber, 12);
                    boards.Sphere(ct + new Vector3(lx, 0.25f, lz), 0.032f, timber * 0.95f);
                }
            boards.RoundBox(ct + new Vector3(0, 0.12f, 0), new Vector3(0.94f, 0.025f, 0.46f), 0.008f, timber * 1.1f); // lower shelf
            float tt = F + 0.4475f;
            Gamepad(plain, new Vector3(-0.18f, tt, 1.02f), 28f);
            Mug(plain, new Vector3(0.3f, tt, 1.12f), new Color(0.70f, 0.20f, 0.16f));
            plain.RoundBox(new Vector3(0.05f, tt + 0.016f, 1.2f), new Vector3(0.22f, 0.032f, 0.16f), 0.006f, new Color(0.18f, 0.30f, 0.24f), Quaternion.Euler(0, -14f, 0), occ: false);
            plain.RoundBox(new Vector3(0.05f, tt + 0.016f, 0.95f), new Vector3(0.24f, 0.03f, 0.17f), 0.006f, new Color(0.78f, 0.70f, 0.52f), Quaternion.Euler(0, 9f, 0), occ: false);
            {
                var jar = new Vector3(0.38f, tt, 0.92f);
                plain.Cylinder(jar, 0.045f, 0.045f, 0.09f, new Color(0.55f, 0.35f, 0.25f), 16, open: true);
                plain.Cylinder(jar + new Vector3(0, 0.002f, 0), 0.042f, 0.042f, 0.06f, new Color(0.95f, 0.9f, 0.8f), 16);
                plain.Cylinder(jar + new Vector3(0, 0.062f, 0), 0.003f, 0.003f, 0.01f, new Color(0.08f, 0.06f, 0.05f), 6);
                AddCandle(jar + new Vector3(0, 0.072f, 0), 0.9f, 0.3f);
            }
            // A second rug-sized sheepskin in front of the hearth.
            sheep.Blob(new Vector3(-1.95f, F + 0.012f, 0.95f), 0.62f, 0.42f, Color.white, 11);

            // ---- bookshelf on the back wall: five shelves of books and a few keepsakes ----
            var shelfC = new Vector3(-0.62f, F, ZBack + LogR + 0.16f);
            const float ShW = 1.0f, ShD = 0.3f, ShH = 1.95f, ShT = 0.03f;
            boards.RoundBox(shelfC + new Vector3(-ShW / 2 + ShT / 2, ShH / 2, 0), new Vector3(ShT, ShH, ShD), 0.006f, timber * 1.15f);
            boards.RoundBox(shelfC + new Vector3(ShW / 2 - ShT / 2, ShH / 2, 0), new Vector3(ShT, ShH, ShD), 0.006f, timber * 1.15f);
            boards.RoundBox(shelfC + new Vector3(0, ShH / 2, -ShD / 2 + 0.006f), new Vector3(ShW, ShH, 0.012f), 0.003f, timber * 0.7f, occ: false);
            AddOcc(shelfC + new Vector3(0, ShH / 2, 0), new Vector3(ShW, ShH, 0.02f) + new Vector3(0, 0, 0)); // back board
            var rngS = new System.Random(31);
            var spines = new[]
            {
                new Color(0.55f, 0.16f, 0.13f), new Color(0.16f, 0.27f, 0.42f), new Color(0.78f, 0.62f, 0.30f), new Color(0.22f, 0.38f, 0.26f),
                new Color(0.42f, 0.24f, 0.40f), new Color(0.85f, 0.80f, 0.70f), new Color(0.20f, 0.20f, 0.24f), new Color(0.65f, 0.36f, 0.18f),
                new Color(0.30f, 0.45f, 0.50f), new Color(0.70f, 0.55f, 0.45f),
            };
            boards.RoundBox(shelfC + new Vector3(0, ShH - ShT / 2, 0), new Vector3(ShW + 0.02f, ShT, ShD + 0.01f), 0.006f, timber * 1.2f, occ: false);
            for (int sIdx = 0; sIdx < 4; sIdx++)
            {
                float shY = F + 0.08f + sIdx * 0.46f;
                boards.RoundBox(shelfC + new Vector3(0, shY, 0), new Vector3(ShW - 2 * ShT, ShT, ShD - 0.01f), 0.006f, timber * 1.2f, occ: false);
                float shX = shelfC.x - ShW / 2 + ShT + 0.01f, endX = shelfC.x + ShW / 2 - ShT - 0.01f;
                int mode = sIdx % 3;
                while (shX < endX - 0.03f)
                {
                    float r = (float)rngS.NextDouble();
                    if (r < 0.08f && shX < endX - 0.25f)
                    {
                        // A keepsake: a jar, a small plant or a framed photo.
                        int kind = rngS.Next(3);
                        if (kind == 0) { plain.Cylinder(new Vector3(shX + 0.05f, shY + ShT / 2, shelfC.z), 0.045f, 0.04f, 0.14f, new Color(0.55f, 0.62f, 0.55f), 14); plain.Cylinder(new Vector3(shX + 0.05f, shY + ShT / 2 + 0.14f, shelfC.z), 0.03f, 0.03f, 0.02f, new Color(0.3f, 0.2f, 0.12f), 12); }
                        else if (kind == 1) { plain.Cylinder(new Vector3(shX + 0.06f, shY + ShT / 2, shelfC.z), 0.05f, 0.06f, 0.08f, new Color(0.75f, 0.42f, 0.28f), 14); plain.Sphere(new Vector3(shX + 0.06f, shY + ShT / 2 + 0.12f, shelfC.z), 0.06f, new Color(0.22f, 0.40f, 0.20f)); }
                        else { plain.RoundBox(new Vector3(shX + 0.07f, shY + ShT / 2 + 0.08f, shelfC.z - 0.08f), new Vector3(0.13f, 0.16f, 0.015f), 0.004f, timber * 0.5f, Quaternion.Euler(-12f, 0, 0), occ: false); painting.Quad(new Vector3(shX + 0.015f, shY + ShT / 2 + 0.02f, shelfC.z - 0.074f), new Vector3(shX + 0.125f, shY + ShT / 2 + 0.02f, shelfC.z - 0.074f), new Vector3(shX + 0.125f, shY + ShT / 2 + 0.14f, shelfC.z - 0.1f), new Vector3(shX + 0.015f, shY + ShT / 2 + 0.14f, shelfC.z - 0.1f), Vector3.back, Color.white); }
                        shX += 0.16f;
                        continue;
                    }
                    float bw = 0.022f + 0.03f * (float)rngS.NextDouble();
                    float bh = 0.2f + 0.12f * (float)rngS.NextDouble();
                    float bd = 0.16f + 0.08f * (float)rngS.NextDouble();
                    var col = spines[rngS.Next(spines.Length)] * (0.85f + 0.3f * (float)rngS.NextDouble());
                    bool lying = mode == 1 && r > 0.85f;
                    if (lying)
                    {
                        float stack = shY + ShT / 2;
                        for (int k = 0; k < 3; k++) { float th = 0.03f + 0.012f * k; plain.RoundBox(new Vector3(shX + 0.11f, stack + th / 2, shelfC.z - 0.01f), new Vector3(0.22f - 0.02f * k, th, 0.17f), 0.004f, spines[rngS.Next(spines.Length)], occ: false); stack += th; }
                        shX += 0.24f;
                    }
                    else
                    {
                        float lean = (r > 0.93f) ? 12f : 0f;
                        plain.RoundBox(new Vector3(shX + bw / 2 + (lean > 0 ? bh * 0.1f : 0), shY + ShT / 2 + bh / 2, shelfC.z + ShD / 2 - bd / 2 - 0.02f),
                                       new Vector3(bw, bh, bd), 0.004f, col, Quaternion.Euler(0, 0, -lean), occ: false);
                        shX += bw + 0.002f + (lean > 0 ? 0.04f : 0f);
                    }
                }
            }

            // ---- framed paintings: right wall near the back corner, left wall above the armchair ----
            Picture(boards, painting, new Vector3(W - LogR - 0.02f, F + 1.62f, -1.75f), Vector3.left, 0.72f, 0.54f, timber * 0.8f);
            Picture(boards, painting, new Vector3(-W + LogR + 0.02f, F + 1.65f, -1.2f), Vector3.right, 0.6f, 0.45f, timber * 0.8f);

            // ---- skis and poles leaning in the back-right corner ----
            for (int k = 0; k < 2; k++)
            {
                var baseP = new Vector3(W - LogR - 0.3f, F, -2.05f - k * 0.09f);
                var tilt = Quaternion.Euler(0, 0, -11f) * Quaternion.Euler(0, 90f, 0);
                var c0 = baseP + Quaternion.Euler(0, 0, -11f) * new Vector3(0, 0.88f, 0);
                plain.RoundBox(c0, new Vector3(0.016f, 1.75f, 0.075f), 0.007f, new Color(0.72f, 0.18f, 0.14f), Quaternion.Euler(0, 0, -11f));
                plain.RoundBox(c0 + Quaternion.Euler(0, 0, -11f) * new Vector3(-0.02f, -0.1f, 0), new Vector3(0.03f, 0.12f, 0.06f), 0.006f, new Color(0.12f, 0.12f, 0.13f), Quaternion.Euler(0, 0, -11f), occ: false);
            }
            plain.Rod(new Vector3(W - LogR - 0.45f, F, -2.3f), new Vector3(W - LogR - 0.05f, F + 1.25f, -2.36f), 0.009f, new Color(0.55f, 0.55f, 0.58f), 8);
            plain.Rod(new Vector3(W - LogR - 0.4f, F, -2.38f), new Vector3(W - LogR - 0.06f, F + 1.22f, -2.28f), 0.009f, new Color(0.55f, 0.55f, 0.58f), 8);

            // ---- a garland with fairy lights along the mantel (set in the fireplace section) ----

            // ---- door (back wall), coat rack and skateboard ----
            float dz = ZBack + 0.04f;
            var doorCol = new Color(0.62f, 0.42f, 0.27f);
            wood.Grid(new Vector3(doorRect.xMin, F, dz), Vector3.right, Vector3.up, doorRect.width, doorRect.height, Vector3.forward,
                      doorCol, uvScale: new Vector2(1 / 1.1f, 1 / 1.1f), swapUV: true);
            logs.Box(new Vector3(doorRect.center.x, F + 0.35f, dz + 0.02f), new Vector3(doorRect.width - 0.08f, 0.14f, 0.035f), doorCol * 0.85f);
            logs.Box(new Vector3(doorRect.center.x, F + 1.7f, dz + 0.02f), new Vector3(doorRect.width - 0.08f, 0.14f, 0.035f), doorCol * 0.85f);
            float braceLen = Mathf.Sqrt((doorRect.width - 0.2f) * (doorRect.width - 0.2f) + 1.35f * 1.35f);
            logs.BoxRotated(new Vector3(doorRect.center.x, F + 1.025f, dz + 0.02f), new Vector3(braceLen, 0.12f, 0.03f),
                            Quaternion.Euler(0, 0, Mathf.Atan2(1.35f, doorRect.width - 0.2f) * Mathf.Rad2Deg), doorCol * 0.85f);
            foreach (var hy in new[] { F + 0.35f, F + 1.7f })
                plain.Box(new Vector3(doorRect.xMin + 0.22f, hy, dz + 0.045f), new Vector3(0.4f, 0.05f, 0.008f), iron);
            plain.Box(new Vector3(doorRect.xMax - 0.12f, F + 1.0f, dz + 0.03f), new Vector3(0.03f, 0.16f, 0.02f), iron);
            plain.Sphere(new Vector3(doorRect.xMax - 0.12f, F + 1.0f, dz + 0.06f), 0.025f, iron * 1.5f);
            // Heavy timber frame around the door, covering the log ends.
            float fd = LogR + 0.05f;
            logs.Box(new Vector3(doorRect.xMin - 0.07f, F + (doorRect.height + 0.14f) / 2, ZBack + fd / 2), new Vector3(0.14f, doorRect.height + 0.14f, fd), timber);
            logs.Box(new Vector3(doorRect.xMax + 0.07f, F + (doorRect.height + 0.14f) / 2, ZBack + fd / 2), new Vector3(0.14f, doorRect.height + 0.14f, fd), timber);
            logs.Box(new Vector3(doorRect.center.x, F + doorRect.height + 0.07f, ZBack + fd / 2), new Vector3(doorRect.width + 0.28f, 0.14f, fd), timber);
            // Coat rack right of the door with a scarf and a beanie.
            float rackX = doorRect.xMax + 0.55f, rackY = F + 1.65f, rackZ = ZBack + LogR + 0.02f;
            logs.Box(new Vector3(rackX, rackY, rackZ), new Vector3(0.55f, 0.1f, 0.03f), timber * 1.1f);
            for (int i = -1; i <= 1; i++)
                plain.Box(new Vector3(rackX + i * 0.17f, rackY, rackZ + 0.06f), new Vector3(0.025f, 0.025f, 0.1f), iron);
            plaid.Box(new Vector3(rackX - 0.17f, rackY - 0.3f, rackZ + 0.09f), new Vector3(0.14f, 0.6f, 0.025f), Color.white);
            plain.Sphere(new Vector3(rackX + 0.17f, rackY - 0.04f, rackZ + 0.12f), 0.095f, new Color(0.15f, 0.40f, 0.42f));
            // Skateboard leaning on the logs left of the door, graphic towards the room.
            BuildSkateboard(plain, deck, new Vector3(doorRect.xMin - 0.55f, F, ZBack + LogR + 0.23f));

            // ---- big window (right wall) ----
            float wz0 = windowRect.xMin, wz1 = windowRect.xMax, wy0 = windowRect.yMin, wy1 = windowRect.yMax;
            float jamb0 = W - LogR - 0.03f, jamb1 = W + 0.12f;
            plain.Quad(new Vector3(jamb0, wy0, wz0), new Vector3(jamb1, wy0, wz0), new Vector3(jamb1, wy1, wz0), new Vector3(jamb0, wy1, wz0), Vector3.forward, pine * 0.8f);
            plain.Quad(new Vector3(jamb0, wy0, wz1), new Vector3(jamb0, wy1, wz1), new Vector3(jamb1, wy1, wz1), new Vector3(jamb1, wy0, wz1), Vector3.back, pine * 0.8f);
            plain.Quad(new Vector3(jamb0, wy1, wz0), new Vector3(jamb1, wy1, wz0), new Vector3(jamb1, wy1, wz1), new Vector3(jamb0, wy1, wz1), Vector3.down, pine * 0.8f);
            plain.Quad(new Vector3(jamb0, wy0, wz0), new Vector3(jamb0, wy0, wz1), new Vector3(jamb1, wy0, wz1), new Vector3(jamb1, wy0, wz0), Vector3.up, pine * 0.8f);
            float fxw = W + 0.06f;
            for (int i = 1; i <= 2; i++)
                logs.Box(new Vector3(fxw, WindowCenter.y, wz0 + WindowW * i / 3f), new Vector3(0.05f, WindowH, 0.06f), timber);
            logs.Box(new Vector3(fxw, wy0 + WindowH * 0.7f, WindowCenter.z), new Vector3(0.05f, 0.05f, WindowW), timber);
            // Timber trim around the opening covering the log ends, and a deep sill with a lantern.
            float trimX = W - LogR - 0.04f;
            logs.Box(new Vector3(trimX, wy1 + 0.07f, WindowCenter.z), new Vector3(0.08f, 0.14f, WindowW + 0.28f), timber);
            logs.Box(new Vector3(trimX, WindowCenter.y, wz0 - 0.07f), new Vector3(0.08f, WindowH, 0.14f), timber);
            logs.Box(new Vector3(trimX, WindowCenter.y, wz1 + 0.07f), new Vector3(0.08f, WindowH, 0.14f), timber);
            logs.Box(new Vector3(W - LogR - 0.06f, wy0 - 0.03f, WindowCenter.z), new Vector3(0.3f, 0.06f, WindowW + 0.3f), timber);
            Lantern(plain, new Vector3(W - LogR - 0.1f, wy0, wz1 - 0.3f), iron);
            // Two small potted plants on the sill.
            foreach (var pz in new[] { wz0 + 0.25f, wz0 + 0.55f })
            {
                var pp = new Vector3(W - LogR - 0.1f, wy0, pz);
                plain.Cylinder(pp, 0.04f, 0.05f, 0.08f, new Color(0.72f, 0.40f, 0.26f), 14);
                for (int k = 0; k < 6; k++) { float a = k * 1.05f; plain.Sphere(pp + new Vector3(Mathf.Cos(a) * 0.025f, 0.1f + 0.01f * (k % 2), Mathf.Sin(a) * 0.025f), 0.022f, new Color(0.28f, 0.48f, 0.30f)); }
            }

            // ---- the view: 3D terrain, trees, mountains and a sky dome (see CozyOutside.cs) ----
            BuildOutside();

            // Lighting is ray traced on worker threads; the previous meshes stay visible until it finishes.
            floorB.Alpha = 1f - Mathf.Clamp01(Plugin.Cfg.FloorReflection.Value);
            StartBake(new[] { floorB, boards, wood, logs, plain, rug, stone, plaid, deck, leather, sheep, painting, sky });
        }

        /// <summary>
        /// The TV around the current screen size: bezel, back, standby LED and a stand down to the cabinet.
        /// Cheap (no light bake: it is black plastic, lit with fixed colours), so it can follow the game's field
        /// of view without rebuilding the room.
        /// </summary>
        public void BuildTv(float d, float screenCy, float sw, float sh)
        {
            if (TvPart.HasValue) Object.Destroy(TvPart.Value.Mesh);
            var tv = new MeshBuilder(this, RoomTextures.White);
            bool rec = recordOcc; recordOcc = false;
            var tvBlack = new Color(0.008f, 0.008f, 0.009f);   // unlit: final colour, so near black
            const float Bezel = 0.03f;
            float screenBottom = screenCy - sh / 2;
            tv.Box(new Vector3(0, screenCy, d + 0.03f), new Vector3(sw + 2 * Bezel, sh + 2 * Bezel, 0.05f), tvBlack);
            tv.Box(new Vector3(0, screenCy, d + 0.075f), new Vector3(sw * 0.6f, sh * 0.6f, 0.05f), tvBlack * 0.8f);
            tv.Box(new Vector3(sw / 2 - 0.05f, screenBottom - Bezel * 0.5f, d + 0.004f), new Vector3(0.008f, 0.004f, 0.002f),
                   new Color(0.9f, 0.35f, 0.25f) * 0.6f, emissive: true);
            float neckTop = screenBottom - Bezel, neckZ = d + 0.07f;
            if (neckTop > cabTopY + 0.02f)
                tv.Box(new Vector3(0, (cabTopY + neckTop) / 2, neckZ), new Vector3(0.08f, neckTop - cabTopY, 0.035f), tvBlack * 1.8f);
            tv.Box(new Vector3(0, cabTopY + 0.008f, neckZ - 0.02f), new Vector3(0.42f, 0.016f, 0.22f), tvBlack * 1.8f);
            recordOcc = rec;
            TvPart = new Part { Mesh = tv.ToMeshUnlit(), Tex = tv.Tex };
        }

        /// <summary>A flat scenery layer outside the right window (facing the room), emissive.</summary>
        void AddLayer(Texture tex, float x, float zc, float halfW, float y0, float y1)
        {
            var b = new MeshBuilder(this, tex);
            b.Quad(new Vector3(x, y0, zc - halfW), new Vector3(x, y0, zc + halfW), new Vector3(x, y1, zc + halfW), new Vector3(x, y1, zc - halfW),
                   Vector3.left, Color.white, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), emissive: true);
            Outside.Add(new Part { Mesh = b.ToMeshUnlit(), Tex = tex });
        }

        /// <summary>A ceramic mug with coffee in it and a looped handle.</summary>
        static void Mug(MeshBuilder b, Vector3 p, Color col)
        {
            b.Cylinder(p, 0.040f, 0.042f, 0.095f, col, 20, open: true);
            b.Cylinder(p + new Vector3(0, 0.004f, 0), 0.036f, 0.038f, 0.091f, col * 0.75f, 20, open: true, inward: true);
            b.Cylinder(p, 0.040f, 0.040f, 0.004f, col * 0.9f, 20);
            b.Cylinder(p + new Vector3(0, 0.072f, 0), 0.037f, 0.037f, 0.002f, new Color(0.16f, 0.09f, 0.05f), 20);
            b.Rod(p + new Vector3(0.040f, 0.078f, 0), p + new Vector3(0.068f, 0.070f, 0), 0.007f, col, 8);
            b.Rod(p + new Vector3(0.068f, 0.070f, 0), p + new Vector3(0.070f, 0.030f, 0), 0.007f, col, 8);
            b.Rod(p + new Vector3(0.070f, 0.030f, 0), p + new Vector3(0.040f, 0.024f, 0), 0.007f, col, 8);
        }

        /// <summary>A game controller lying on a table (built around its centre, turned by yaw).</summary>
        static void Gamepad(MeshBuilder b, Vector3 p, float yawDeg)
        {
            var rot = Quaternion.Euler(0, yawDeg, 0);
            Vector3 At(float x, float y, float z) => p + rot * new Vector3(x, y, z);
            var shell = new Color(0.13f, 0.13f, 0.15f);
            b.RoundBox(At(0, 0.016f, 0.005f), new Vector3(0.15f, 0.032f, 0.075f), 0.014f, shell, rot, occ: false);
            b.RoundBox(At(-0.055f, 0.014f, -0.03f), new Vector3(0.05f, 0.028f, 0.075f), 0.014f, shell, rot * Quaternion.Euler(0, 18f, 0), occ: false);
            b.RoundBox(At(0.055f, 0.014f, -0.03f), new Vector3(0.05f, 0.028f, 0.075f), 0.014f, shell, rot * Quaternion.Euler(0, -18f, 0), occ: false);
            b.Cylinder(At(-0.03f, 0.03f, 0.012f), 0.011f, 0.011f, 0.012f, new Color(0.08f, 0.08f, 0.09f), 12);
            b.Cylinder(At(0.025f, 0.03f, -0.012f), 0.011f, 0.011f, 0.012f, new Color(0.08f, 0.08f, 0.09f), 12);
            var abxy = new[] { new Color(0.2f, 0.65f, 0.25f), new Color(0.75f, 0.2f, 0.18f), new Color(0.2f, 0.35f, 0.75f), new Color(0.85f, 0.7f, 0.15f) };
            var offs = new[] { new Vector2(0, -0.009f), new Vector2(0.009f, 0), new Vector2(-0.009f, 0), new Vector2(0, 0.009f) };
            for (int i = 0; i < 4; i++) b.Sphere(At(0.05f + offs[i].x, 0.033f, 0.012f + offs[i].y), 0.0045f, abxy[i]);
            b.RoundBox(At(-0.05f, 0.033f, -0.006f), new Vector3(0.022f, 0.005f, 0.007f), 0.002f, new Color(0.06f, 0.06f, 0.07f), rot, occ: false);
            b.RoundBox(At(-0.05f, 0.033f, -0.006f), new Vector3(0.007f, 0.005f, 0.022f), 0.002f, new Color(0.06f, 0.06f, 0.07f), rot, occ: false);
            b.Sphere(At(0f, 0.033f, 0.022f), 0.006f, new Color(0.6f, 0.8f, 1f) * 0.9f, emissive: true);
        }

        /// <summary>A framed picture hanging on a wall; n points out of the wall into the room.</summary>
        static void Picture(MeshBuilder frameB, MeshBuilder picB, Vector3 c, Vector3 n, float w, float h, Color frameCol)
        {
            var u = Vector3.Cross(n, Vector3.up).normalized;   // the viewer's right when facing the wall
            const float T = 0.045f, D = 0.03f;
            var rot = Quaternion.LookRotation(n, Vector3.up);
            var lu = Quaternion.Inverse(rot) * u;              // u in the frame's local space (+/- x)
            float su = Mathf.Sign(lu.x);
            frameB.RoundBox(c + n * (D / 2) + Vector3.up * (h / 2 + T / 2), new Vector3(w + 2 * T, T, D), 0.006f, frameCol, rot, occ: false);
            frameB.RoundBox(c + n * (D / 2) - Vector3.up * (h / 2 + T / 2), new Vector3(w + 2 * T, T, D), 0.006f, frameCol, rot, occ: false);
            frameB.RoundBox(c + n * (D / 2) + u * (w / 2 + T / 2), new Vector3(T, h, D), 0.006f, frameCol, rot, occ: false);
            frameB.RoundBox(c + n * (D / 2) - u * (w / 2 + T / 2), new Vector3(T, h, D), 0.006f, frameCol, rot, occ: false);
            var pc = c + n * 0.01f;
            picB.Quad(pc - u * (w / 2) - Vector3.up * (h / 2), pc + u * (w / 2) - Vector3.up * (h / 2),
                      pc + u * (w / 2) + Vector3.up * (h / 2), pc - u * (w / 2) + Vector3.up * (h / 2), n, Color.white,
                      new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), false, 6, 5);
        }

        /// <summary>A small iron lantern with a glowing glass box.</summary>
        /// <summary>Registers a candle flame (drawn animated each frame) and, optionally, its small warm light.</summary>
        void AddCandle(Vector3 wickTop, float scale, float light)
        {
            Candles.Add((wickTop, scale));
            Glows.Add((wickTop + new Vector3(0, 0.025f * scale, 0), 0.11f * scale, new Color(0.20f, 0.12f, 0.045f) * scale, false));
            if (light > 0f)
                lights.Add(new PointLight { Pos = wickTop + new Vector3(0, 0.03f, 0), Col = new Color(1.0f, 0.62f, 0.30f) * light, Radius = 0.5f });
        }

        void Lantern(MeshBuilder b, Vector3 basePos, Color iron)
        {
            b.Box(basePos + new Vector3(0, 0.01f, 0), new Vector3(0.12f, 0.02f, 0.12f), iron);
            b.Box(basePos + new Vector3(0, 0.1f, 0), new Vector3(0.09f, 0.15f, 0.09f), new Color(1f, 0.72f, 0.35f) * 0.85f, emissive: true);
            lights.Add(new PointLight { Pos = basePos + new Vector3(0, 0.1f, 0), Col = new Color(1.0f, 0.62f, 0.30f) * 0.28f, Radius = 0.6f });
            Glows.Add((basePos + new Vector3(0, 0.1f, 0), 0.2f, new Color(0.17f, 0.10f, 0.04f), false));
            b.Box(basePos + new Vector3(0, 0.19f, 0), new Vector3(0.12f, 0.025f, 0.12f), iron);
            b.Cylinder(basePos + new Vector3(0, 0.2f, 0), 0.05f, 0.01f, 0.06f, iron, 8);
        }

        /// <summary>
        /// A skateboard standing on its tail, leaning back against the wall (towards -z), graphic facing the room.
        /// tail = where the tail touches the floor.
        /// </summary>
        void BuildSkateboard(MeshBuilder plain, MeshBuilder deckB, Vector3 tail)
        {
            recordOcc = false; // parts are built at the origin and then moved; register one box instead
            const float L = 0.80f, Wd = 0.21f, T = 0.014f;
            var tilt = Quaternion.Euler(-15f, 0f, 0f);
            var centre = tail + tilt * new Vector3(0, L / 2, 0) + new Vector3(0, 0.012f, 0);
            var m = Matrix4x4.TRS(centre, tilt, Vector3.one);
            // Deck (black grip/edges) and the graphic on the room side (+z local).
            int s0 = plain.Count;
            plain.Box(Vector3.zero, new Vector3(Wd, L, T), new Color(0.05f, 0.05f, 0.05f));
            plain.Transform(s0, m);
            int d0 = deckB.Count;
            deckB.Quad(new Vector3(-Wd / 2, -L / 2, T / 2 + 0.0008f), new Vector3(-Wd / 2, L / 2, T / 2 + 0.0008f),
                       new Vector3(Wd / 2, L / 2, T / 2 + 0.0008f), new Vector3(Wd / 2, -L / 2, T / 2 + 0.0008f),
                       Vector3.forward, Color.white, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
            deckB.Transform(d0, m);
            // Trucks and wheels on the wall side (-z local); wheels poke out at the sides.
            foreach (var ty in new[] { -0.27f, 0.27f })
            {
                int t0 = plain.Count;
                plain.Box(new Vector3(0, ty, -T / 2 - 0.02f), new Vector3(0.16f, 0.04f, 0.04f), new Color(0.6f, 0.62f, 0.66f));
                plain.Transform(t0, m);
                foreach (var wx in new[] { -0.095f, 0.095f })
                {
                    int w0 = plain.Count;
                    plain.Cylinder(new Vector3(0, -0.016f, 0), 0.027f, 0.027f, 0.032f, new Color(0.95f, 0.88f, 0.55f), 14);
                    plain.Transform(w0, m * Matrix4x4.TRS(new Vector3(wx, ty, -T / 2 - 0.045f), Quaternion.Euler(0, 0, 90f), Vector3.one));
                }
            }
            recordOcc = true;
            AddOcc(centre, tilt * Vector3.right, tilt * Vector3.up, tilt * Vector3.forward, new Vector3(Wd / 2, L / 2, 0.04f));
        }

        // ---- baked lighting -----------------------------------------------------------------------------

        readonly List<LightBaker.Obb> bakeOcc = new();
        bool recordOcc = true;
        System.Threading.Tasks.Task bakeTask;
        LightBaker baker;
        MeshBuilder[] pendingBuilders;
        float bakeStarted;

        static System.Numerics.Vector3 N3(Vector3 v) => new(v.x, v.y, v.z);

        /// <summary>Registers a solid box (centre, axes, half extents) that casts shadows and occludes ambient light.</summary>
        void AddOcc(Vector3 c, Vector3 x, Vector3 y, Vector3 z, Vector3 half)
        {
            if (!recordOcc) return;
            bakeOcc.Add(LightBaker.MakeObb(N3(c), N3(x), N3(y), N3(z), N3(half)));
        }
        void AddOcc(Vector3 c, Vector3 size) => AddOcc(c, Vector3.right, Vector3.up, Vector3.forward, size / 2);

        void StartBake(MeshBuilder[] builders)
        {
            var b = new LightBaker
            {
                RoomHalfW = RoomHalfW, ZBack = ZBack, ZFront = ZFront, Floor = Floor, EaveH = EaveH, RidgeH = RidgeH,
                WallInset = LogR * 0.6f,
                FireOpeningX = FireOpeningX, FireZ0 = fireOpenZ0, FireZ1 = fireOpenZ1, FireY0 = FireOpeningY0, FireY1 = fireOpenY1,
                FirePos = N3(fire.Pos),
                HasTv = true, TvCentre = N3(tvCentre), TvHalfW = tvHalfW, TvHalfH = tvHalfH,
            };
            b.Occluders.AddRange(bakeOcc);
            foreach (var l in lights)
            {
                var bl = new LightBaker.Light { Col = new System.Numerics.Vector3(l.Col.r, l.Col.g, l.Col.b), Radius = l.Radius };
                if (l.Window)
                {
                    bl.Kind = LightBaker.Kind.Window;
                    var s = new List<System.Numerics.Vector3>();
                    for (int j = 0; j < 3; j++)
                        for (int i = 0; i < 4; i++)
                            s.Add(N3(new Vector3(RoomHalfW + 0.14f, WindowCenter.y + (j - 1) * WindowH * 0.32f, WindowCenter.z + (i - 1.5f) * WindowW * 0.27f)));
                    bl.Samples = s.ToArray();
                }
                else if (l.Samples != null) { bl.Kind = LightBaker.Kind.Chandelier; bl.Samples = System.Array.ConvertAll(l.Samples, N3); }
                else if (l.Lamp)
                {
                    bl.Kind = LightBaker.Kind.Lamp;
                    bl.Samples = new[] { N3(l.Pos), N3(l.Pos + new Vector3(0.05f, 0, 0)), N3(l.Pos + new Vector3(-0.05f, 0, 0)),
                                         N3(l.Pos + new Vector3(0, 0, 0.05f)), N3(l.Pos + new Vector3(0, 0, -0.05f)), N3(l.Pos + new Vector3(0, 0.04f, 0)) };
                }
                else if (l.Fire)
                {
                    bl.Kind = LightBaker.Kind.Fire;
                    var c = l.Pos;
                    bl.Samples = new[] { N3(c), N3(c + new Vector3(0.06f, 0.08f, -0.22f)), N3(c + new Vector3(0.06f, 0.08f, 0.22f)),
                                         N3(c + new Vector3(0.1f, -0.05f, -0.1f)), N3(c + new Vector3(0.1f, -0.05f, 0.1f)), N3(c + new Vector3(0, 0.2f, 0)) };
                }
                else
                {
                    bl.Kind = LightBaker.Kind.Point;
                    bl.Samples = new[] { N3(l.Pos), N3(l.Pos + new Vector3(0, 0.015f, 0)) };
                    bl.MaxRange = l.Radius * 6f;
                }
                b.Lights.Add(bl);
            }

            int total = 0;
            foreach (var mb in builders) total += mb.Count;
            b.P = new System.Numerics.Vector3[total]; b.N = new System.Numerics.Vector3[total]; b.Tint = new System.Numerics.Vector3[total];
            b.Emissive = new bool[total]; b.NoAo = new bool[total];
            int off = 0;
            foreach (var mb in builders) { mb.Export(b, off); off += mb.Count; }

            baker = b;
            pendingBuilders = builders;
            bakeStarted = Time.realtimeSinceStartup;
            bakeTask = b.BakeAsync();
            Plugin.L.LogInfo($"Lodge geometry ready: {total} vertices, {bakeOcc.Count} occluders, {b.Lights.Count} lights; baking lighting...");
        }

        /// <summary>Call every frame: swaps in the lit meshes once the bake has finished. True when swapped.</summary>
        public bool TryFinish()
        {
            if (bakeTask == null || !bakeTask.IsCompleted) return false;
            var task = bakeTask; bakeTask = null;
            if (task.IsFaulted) { Plugin.L.LogError("Light bake failed: " + task.Exception); return false; }

            foreach (var p in Opaque) Object.Destroy(p.Mesh);
            Opaque.Clear();
            if (FireLight != null) Object.Destroy(FireLight);

            int off = 0;
            foreach (var mb in pendingBuilders)
            {
                if (mb.Count > 0) Opaque.Add(new Part { Mesh = mb.ToMesh(baker.Base, off), Tex = mb.Tex });
                off += mb.Count;
            }
            // Fire flicker: every lit surface (the sky is last and excluded), fire contribution only.
            var combined = new MeshBuilder(this, RoomTextures.White);
            for (int i = 0; i < pendingBuilders.Length - 1; i++) combined.Append(pendingBuilders[i]);
            FireLight = combined.ToMesh(baker.FireOnly, 0);
            if (TvLight != null) Object.Destroy(TvLight);
            TvLight = combined.ToMesh(baker.TvOnly, 0);
            Plugin.L.LogInfo($"Lodge lighting baked in {Time.realtimeSinceStartup - bakeStarted:0.00} s ({combined.Count} lit vertices).");
            pendingBuilders = null; baker = null;
            return true;
        }

        // ---- mesh building ------------------------------------------------------------------------------

        [System.Flags] public enum BoxFaces { Right = 1, Left = 2, Top = 4, Bottom = 8, Front = 16, Back = 32, All = 63 }

        class MeshBuilder
        {
            readonly CozyRoom room;
            public readonly Texture Tex;
            readonly List<Vector3> pos = new(), nrm = new();
            readonly List<Vector2> uv = new();
            readonly List<Color> tint = new();
            readonly List<bool> emissive = new(), shell = new();
            readonly List<int> tris = new();
            public int Count => pos.Count;

            public MeshBuilder(CozyRoom r, Texture t) { room = r; Tex = t; }

            int V(Vector3 p, Vector3 n, Vector2 t, Color c, bool em, bool sh)
            {
                pos.Add(p); nrm.Add(n); uv.Add(t); tint.Add(c); emissive.Add(em); shell.Add(sh);
                return pos.Count - 1;
            }

            public void Append(MeshBuilder o)
            {
                int b = pos.Count;
                pos.AddRange(o.pos); nrm.AddRange(o.nrm); uv.AddRange(o.uv); tint.AddRange(o.tint);
                emissive.AddRange(o.emissive); shell.AddRange(o.shell);
                foreach (var t in o.tris) tris.Add(b + t);
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Color col,
                             Vector2? ta = null, Vector2? tb = null, Vector2? tc = null, Vector2? td = null,
                             bool emissive = false, int subdiv = 1, int subdivV = -1)
            {
                var A = ta ?? new Vector2(0, 0); var B = tb ?? new Vector2(1, 0); var C = tc ?? new Vector2(1, 1); var D = td ?? new Vector2(0, 1);
                int s = Mathf.Max(1, subdiv), sv = subdivV > 0 ? subdivV : s;
                int baseIdx = pos.Count;
                for (int j = 0; j <= sv; j++)
                    for (int i = 0; i <= s; i++)
                    {
                        float u = i / (float)s, v = j / (float)sv;
                        var p = Vector3.Lerp(Vector3.Lerp(a, b, u), Vector3.Lerp(d, c, u), v);
                        var t = Vector2.Lerp(Vector2.Lerp(A, B, u), Vector2.Lerp(D, C, u), v);
                        V(p, n, t, col, emissive, false);
                    }
                for (int j = 0; j < sv; j++)
                    for (int i = 0; i < s; i++)
                    {
                        int i0 = baseIdx + j * (s + 1) + i;
                        tris.Add(i0); tris.Add(i0 + s + 1); tris.Add(i0 + s + 2);
                        tris.Add(i0); tris.Add(i0 + s + 2); tris.Add(i0 + 1);
                    }
            }

            /// <summary>An axis-aligned rectangle given by two opposite corners on a plane with normal n.</summary>
            public void Face(Vector3 c0, Vector3 c1, Vector3 n, Color col, float uvSize)
            {
                Vector3 a, b, c, d;
                if (Mathf.Abs(n.x) > 0.5f) { a = new Vector3(c0.x, c0.y, c0.z); b = new Vector3(c0.x, c0.y, c1.z); c = new Vector3(c0.x, c1.y, c1.z); d = new Vector3(c0.x, c1.y, c0.z); }
                else if (Mathf.Abs(n.z) > 0.5f) { a = new Vector3(c0.x, c0.y, c0.z); b = new Vector3(c1.x, c0.y, c0.z); c = new Vector3(c1.x, c1.y, c0.z); d = new Vector3(c0.x, c1.y, c0.z); }
                else { a = new Vector3(c0.x, c0.y, c0.z); b = new Vector3(c1.x, c0.y, c0.z); c = new Vector3(c1.x, c0.y, c1.z); d = new Vector3(c0.x, c0.y, c1.z); }
                Vector2 T(Vector3 p) => Mathf.Abs(n.x) > 0.5f ? new Vector2(p.z, p.y) / uvSize : Mathf.Abs(n.z) > 0.5f ? new Vector2(p.x, p.y) / uvSize : new Vector2(p.x, p.z) / uvSize;
                float size = Mathf.Max((b - a).magnitude, (d - a).magnitude);
                Quad(a, b, c, d, n, col, T(a), T(b), T(c), T(d), subdiv: Mathf.Clamp(Mathf.CeilToInt(size / 0.2f), 1, 12));
            }

            /// <summary>A finely tessellated plane (so the per-vertex lighting is smooth), optionally with a hole.</summary>
            public void Grid(Vector3 origin, Vector3 uDir, Vector3 vDir, float uLen, float vLen, Vector3 n, Color col,
                             Vector2? uvScale = null, bool swapUV = false, bool shell = false, Rect? hole = null,
                             System.Func<Vector3, Vector3> warp = null, float step = 0.1f)
            {
                var sc = uvScale ?? Vector2.one;
                var us = Breaks(uLen, hole?.xMin, hole?.xMax, step);
                var vs = Breaks(vLen, hole?.yMin, hole?.yMax, step);
                var idx = new int[us.Count, vs.Count];
                for (int j = 0; j < vs.Count; j++)
                    for (int i = 0; i < us.Count; i++)
                    {
                        var p = origin + uDir * us[i] + vDir * vs[j];
                        if (warp != null) p = warp(p);
                        var t = swapUV ? new Vector2(vs[j] * sc.x, us[i] * sc.y) : new Vector2(us[i] * sc.x, vs[j] * sc.y);
                        idx[i, j] = V(p, n, t, col, false, shell);
                    }
                for (int j = 0; j < vs.Count - 1; j++)
                    for (int i = 0; i < us.Count - 1; i++)
                    {
                        if (hole.HasValue)
                        {
                            float cu = (us[i] + us[i + 1]) / 2, cv = (vs[j] + vs[j + 1]) / 2;
                            if (hole.Value.Contains(new Vector2(cu, cv))) continue;
                        }
                        int a = idx[i, j], b = idx[i + 1, j], c = idx[i + 1, j + 1], d = idx[i, j + 1];
                        tris.Add(a); tris.Add(d); tris.Add(c);
                        tris.Add(a); tris.Add(c); tris.Add(b);
                    }
            }

            static List<float> Breaks(float len, float? h0, float? h1, float step)
            {
                float Step = step;
                // Fixed lines (ends and hole edges) are always kept; regular steps too close to them are dropped.
                var fixedLines = new List<float> { 0f, len };
                if (h0.HasValue) { fixedLines.Add(h0.Value); fixedLines.Add(h1.Value); }
                var l = new List<float>(fixedLines);
                for (float x = Step; x < len - 1e-4f; x += Step)
                {
                    bool near = false;
                    foreach (var f in fixedLines) if (Mathf.Abs(f - x) < 0.04f) { near = true; break; }
                    if (!near) l.Add(x);
                }
                l.Sort();
                return l;
            }

            /// <summary>
            /// A box. Full boxes get chamfered edges (bevel, default ~1.5 cm) so edges catch the light instead of
            /// meeting at a hard 90 degrees; pass a larger bevel for soft, rounded shapes like cushions.
            /// </summary>
            public void Box(Vector3 c, Vector3 size, Color col, bool emissive = false, BoxFaces onlyFaces = BoxFaces.All, float bevel = -1f)
            {
                float minDim = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
                float b = bevel >= 0f ? bevel : Mathf.Min(0.015f, minDim * 0.2f);
                b = Mathf.Min(b, minDim * 0.45f);
                if (onlyFaces == BoxFaces.All && !emissive && minDim >= 0.008f) room.AddOcc(c, size);
                if (onlyFaces == BoxFaces.All && b > 0.002f) { BevelBox(c, size, col, emissive, b); return; }
                SharpBox(c, size, col, emissive, onlyFaces);
            }

            void BevelBox(Vector3 c, Vector3 size, Color col, bool emissive, float b)
            {
                var h = size / 2;
                const float uvs = 0.6f;
                Vector3 Axis(int a) => a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward;
                float Hc(int a) => a == 0 ? h.x : a == 1 ? h.y : h.z;
                // Faces, inset by the bevel.
                for (int a = 0; a < 3; a++)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        int a1 = (a + 1) % 3, a2 = (a + 2) % 3;
                        var n = Axis(a) * s;
                        var o = c + n * Hc(a);
                        var e1 = Axis(a1) * (Hc(a1) - b);
                        var e2 = Axis(a2) * (Hc(a2) - b);
                        float uw = 2 * (Hc(a1) - b), vh = 2 * (Hc(a2) - b);
                        int sub = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(uw, vh) / 0.12f), 1, 40);
                        // Consistent winding doesn't matter (no culling); UVs in metres / uvs.
                        Quad(o - e1 - e2, o + e1 - e2, o + e1 + e2, o - e1 + e2, n, col,
                             new Vector2(0, 0), new Vector2(uw / uvs, 0), new Vector2(uw / uvs, vh / uvs), new Vector2(0, vh / uvs), emissive, sub);
                    }
                // Edge strips between two faces, along the third axis.
                for (int a3 = 0; a3 < 3; a3++)
                {
                    int a1 = (a3 + 1) % 3, a2 = (a3 + 2) % 3;
                    for (int s1 = -1; s1 <= 1; s1 += 2)
                        for (int s2 = -1; s2 <= 1; s2 += 2)
                        {
                            var n = (Axis(a1) * s1 + Axis(a2) * s2).normalized;
                            var p1 = c + Axis(a1) * (s1 * Hc(a1)) + Axis(a2) * (s2 * (Hc(a2) - b));
                            var p2 = c + Axis(a1) * (s1 * (Hc(a1) - b)) + Axis(a2) * (s2 * Hc(a2));
                            var e = Axis(a3) * (Hc(a3) - b);
                            float len = 2 * (Hc(a3) - b);
                            int sub = Mathf.Clamp(Mathf.CeilToInt(len / 0.12f), 1, 40);
                            Quad(p1 - e, p1 + e, p2 + e, p2 - e, n, col,
                                 new Vector2(0, 0), new Vector2(len / uvs, 0), new Vector2(len / uvs, b / uvs), new Vector2(0, b / uvs), emissive, sub);
                        }
                }
                // Corner triangles.
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            var n = new Vector3(sx, sy, sz).normalized;
                            int i0 = V(c + new Vector3(sx * h.x, sy * (h.y - b), sz * (h.z - b)), n, Vector2.zero, col, emissive, false);
                            int i1 = V(c + new Vector3(sx * (h.x - b), sy * h.y, sz * (h.z - b)), n, Vector2.zero, col, emissive, false);
                            int i2 = V(c + new Vector3(sx * (h.x - b), sy * (h.y - b), sz * h.z), n, Vector2.zero, col, emissive, false);
                            tris.Add(i0); tris.Add(i1); tris.Add(i2);
                        }
            }

            /// <summary>
            /// A stone surface displaced by the river-stone height map, so the stones physically stand out and the
            /// per-vertex lighting shades their sides. Rectangle given by two opposite corners on a vertical plane
            /// with normal n (x or z facing), optionally with a rectangular hole (in u = z or x, and y).
            /// The grid lines sit on a world-aligned lattice and the displacement fades to zero at every edge, so
            /// faces that meet (corners, the opening) join flush without seams.
            /// </summary>
            public void StoneFace(Vector3 c0, Vector3 c1, Vector3 n, Color col, float uvSize, float depth = 0.045f, Rect? hole = null)
            {
                bool xFacing = Mathf.Abs(n.x) > 0.5f;
                float u0 = xFacing ? Mathf.Min(c0.z, c1.z) : Mathf.Min(c0.x, c1.x);
                float u1 = xFacing ? Mathf.Max(c0.z, c1.z) : Mathf.Max(c0.x, c1.x);
                float y0 = Mathf.Min(c0.y, c1.y), y1 = Mathf.Max(c0.y, c1.y);
                float plane = xFacing ? c0.x : c0.z;
                const float Step = 0.025f, Fade = 0.05f;
                var us = Lattice(u0, u1, Step, hole.HasValue ? new[] { hole.Value.xMin, hole.Value.xMax } : null);
                var vs = Lattice(y0, y1, Step, hole.HasValue ? new[] { hole.Value.yMin, hole.Value.yMax } : null);
                int nu = us.Count - 1, nv = vs.Count - 1;
                var P = new Vector3[nu + 1, nv + 1];
                var T = new Vector2[nu + 1, nv + 1];
                for (int j = 0; j <= nv; j++)
                    for (int i = 0; i <= nu; i++)
                    {
                        float u = us[i], y = vs[j];
                        var t = new Vector2(u, y) / uvSize;
                        float edge = Mathf.Min(Mathf.Min(u - u0, u1 - u), Mathf.Min(y - y0, y1 - y));
                        if (hole.HasValue)
                        {
                            var hr = hole.Value;
                            float dx = Mathf.Max(hr.xMin - u, 0f, u - hr.xMax), dy = Mathf.Max(hr.yMin - y, 0f, y - hr.yMax);
                            edge = Mathf.Min(edge, Mathf.Sqrt(dx * dx + dy * dy));
                        }
                        float fade = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(edge / Fade));
                        float hgt = RoomTextures.SampleStone(t.x, t.y) * fade;
                        var p = xFacing ? new Vector3(plane, y, u) : new Vector3(u, y, plane);
                        P[i, j] = p + n * (hgt * depth);
                        T[i, j] = t;
                    }
                int baseIdx = pos.Count;
                for (int j = 0; j <= nv; j++)
                    for (int i = 0; i <= nu; i++)
                    {
                        var du = P[Mathf.Min(nu, i + 1), j] - P[Mathf.Max(0, i - 1), j];
                        var dv = P[i, Mathf.Min(nv, j + 1)] - P[i, Mathf.Max(0, j - 1)];
                        var nn = Vector3.Cross(du, dv).normalized;
                        if (Vector3.Dot(nn, n) < 0) nn = -nn;
                        V(P[i, j], nn, T[i, j], col, false, false);
                    }
                for (int j = 0; j < nv; j++)
                    for (int i = 0; i < nu; i++)
                    {
                        if (hole.HasValue && hole.Value.Contains(new Vector2((us[i] + us[i + 1]) / 2, (vs[j] + vs[j + 1]) / 2))) continue;
                        int i0 = baseIdx + j * (nu + 1) + i, i1 = i0 + nu + 1;
                        tris.Add(i0); tris.Add(i1); tris.Add(i1 + 1);
                        tris.Add(i0); tris.Add(i1 + 1); tris.Add(i0 + 1);
                    }
            }

            /// <summary>Grid lines at world multiples of step between a and b, plus a, b and any extra lines.</summary>
            static List<float> Lattice(float a, float b, float step, float[] extra)
            {
                var fixedLines = new List<float> { a, b };
                if (extra != null) foreach (var e in extra) if (e > a && e < b) fixedLines.Add(e);
                var l = new List<float>(fixedLines);
                for (float x = Mathf.Ceil(a / step) * step; x < b; x += step)
                {
                    bool near = false;
                    foreach (var f in fixedLines) if (Mathf.Abs(f - x) < step * 0.3f) { near = true; break; }
                    if (!near) l.Add(x);
                }
                l.Sort();
                return l;
            }

            void SharpBox(Vector3 c, Vector3 size, Color col, bool emissive, BoxFaces onlyFaces)
            {
                var h = size / 2;
                float uvs = 0.6f;
                void F(BoxFaces f, Vector3 n, Vector3 a, Vector3 b, Vector3 cc, Vector3 d, float uw, float vh)
                {
                    if ((onlyFaces & f) == 0) return;
                    int sub = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(uw, vh) / 0.12f), 1, 40);
                    Quad(a, b, cc, d, n, col, new Vector2(0, 0), new Vector2(uw / uvs, 0), new Vector2(uw / uvs, vh / uvs), new Vector2(0, vh / uvs), emissive, sub);
                }
                var p = c;
                F(BoxFaces.Right, Vector3.right, p + new Vector3(h.x, -h.y, -h.z), p + new Vector3(h.x, -h.y, h.z), p + new Vector3(h.x, h.y, h.z), p + new Vector3(h.x, h.y, -h.z), size.z, size.y);
                F(BoxFaces.Left, Vector3.left, p + new Vector3(-h.x, -h.y, h.z), p + new Vector3(-h.x, -h.y, -h.z), p + new Vector3(-h.x, h.y, -h.z), p + new Vector3(-h.x, h.y, h.z), size.z, size.y);
                F(BoxFaces.Top, Vector3.up, p + new Vector3(-h.x, h.y, -h.z), p + new Vector3(h.x, h.y, -h.z), p + new Vector3(h.x, h.y, h.z), p + new Vector3(-h.x, h.y, h.z), size.x, size.z);
                F(BoxFaces.Bottom, Vector3.down, p + new Vector3(-h.x, -h.y, h.z), p + new Vector3(h.x, -h.y, h.z), p + new Vector3(h.x, -h.y, -h.z), p + new Vector3(-h.x, -h.y, -h.z), size.x, size.z);
                F(BoxFaces.Front, Vector3.forward, p + new Vector3(h.x, -h.y, h.z), p + new Vector3(-h.x, -h.y, h.z), p + new Vector3(-h.x, h.y, h.z), p + new Vector3(h.x, h.y, h.z), size.x, size.y);
                F(BoxFaces.Back, Vector3.back, p + new Vector3(-h.x, -h.y, -h.z), p + new Vector3(h.x, -h.y, -h.z), p + new Vector3(h.x, h.y, -h.z), p + new Vector3(-h.x, h.y, -h.z), size.x, size.y);
            }

            /// <summary>Vertical (truncated) cylinder from basePos upwards; open = no caps.</summary>
            public void Cylinder(Vector3 basePos, float rBottom, float rTop, float height, Color col, int seg,
                                 bool emissive = false, bool open = false, bool inward = false)
            {
                int b = pos.Count;
                for (int i = 0; i <= seg; i++)
                {
                    float a = i / (float)seg * Mathf.PI * 2f;
                    var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    var n = (dir + Vector3.up * ((rBottom - rTop) / Mathf.Max(0.01f, height))).normalized;
                    if (inward) n = -n;
                    V(basePos + dir * rBottom, n, new Vector2(i / (float)seg, 0), col, emissive, false);
                    V(basePos + dir * rTop + Vector3.up * height, n, new Vector2(i / (float)seg, 1), col, emissive, false);
                }
                for (int i = 0; i < seg; i++)
                {
                    int i0 = b + i * 2;
                    tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 3);
                    tris.Add(i0); tris.Add(i0 + 3); tris.Add(i0 + 2);
                }
                if (open) return;
                int ct = V(basePos + Vector3.up * height, Vector3.up, new Vector2(0.5f, 0.5f), col, emissive, false);
                for (int i = 0; i < seg; i++)
                {
                    float a0 = i / (float)seg * Mathf.PI * 2f, a1 = (i + 1) / (float)seg * Mathf.PI * 2f;
                    int p0 = V(basePos + Vector3.up * height + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * rTop, Vector3.up, Vector2.zero, col, emissive, false);
                    int p1 = V(basePos + Vector3.up * height + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * rTop, Vector3.up, Vector2.zero, col, emissive, false);
                    tris.Add(ct); tris.Add(p1); tris.Add(p0);
                }
            }

            /// <summary>
            /// A snake-plant leaf: a tall pointed blade, widest a third of the way up, leaning outwards, dark
            /// green with lighter yellow-green edges.
            /// </summary>
            public void Blade(Vector3 basePos, Vector3 radial, float h, float w, float lean, float yawDeg)
            {
                float rad = yawDeg * Mathf.Deg2Rad;
                var across = new Vector3(Mathf.Cos(rad), 0, Mathf.Sin(rad));
                // Lit like a surface facing up and towards the room, so both sides read well.
                var n = (Vector3.up * 0.6f + Vector3.back * 0.8f).normalized;
                Vector3 At(float t, float side) => basePos + radial * (lean * t * t) + Vector3.up * (h * t) + across * (side * w * 0.5f);
                var dark = new Color(0.10f, 0.26f, 0.12f);
                var mid = new Color(0.16f, 0.36f, 0.16f);
                var edge = new Color(0.55f, 0.62f, 0.24f);
                int b0 = V(At(0f, -0.55f), n, Vector2.zero, dark, false, false);
                int b1 = V(At(0f, 0.55f), n, Vector2.zero, dark, false, false);
                int m0 = V(At(0.35f, -1f), n, Vector2.zero, edge, false, false);
                int m1 = V(At(0.35f, 1f), n, Vector2.zero, edge, false, false);
                int mc = V(At(0.35f, 0f), n, Vector2.zero, mid, false, false);
                int u0 = V(At(0.7f, -0.6f), n, Vector2.zero, edge * 0.9f, false, false);
                int u1 = V(At(0.7f, 0.6f), n, Vector2.zero, edge * 0.9f, false, false);
                int uc = V(At(0.7f, 0f), n, Vector2.zero, mid, false, false);
                int tip = V(At(1f, 0f), n, Vector2.zero, dark, false, false);
                void Tri(int a, int c, int e) { tris.Add(a); tris.Add(c); tris.Add(e); }
                Tri(b0, mc, m0); Tri(b0, b1, mc); Tri(b1, m1, mc);
                Tri(m0, mc, uc); Tri(m0, uc, u0); Tri(mc, m1, u1); Tri(mc, u1, uc);
                Tri(u0, uc, tip); Tri(uc, u1, tip);
            }

            /// <summary>
            /// One wooden board: a top face (towards n), small chamfers on its edges and thin sides that show in the
            /// gaps between boards. u runs along the board (uvLen metres per texture repeat) from uvOff.x; v covers
            /// one board strip of the board-grain texture [strip/4, (strip+1)/4]. Not a shadow caster (part of the
            /// room shell), so it doesn't slow the light bake.
            /// </summary>
            public void Board(Vector3 c, Vector3 lenDir, Vector3 widDir, Vector3 n, float len, float wid, float thick,
                              Color col, float uOff, int strip, float uvLen = 2.0f, bool isShell = true)
            {
                const float b = 0.004f;
                float hl = len / 2, hw = wid / 2, ht = thick / 2;
                float v0 = strip * 0.25f + 0.01f, v1 = (strip + 1) * 0.25f - 0.01f;
                int su = Mathf.Clamp(Mathf.CeilToInt(len / 0.1f), 1, 60), sw = Mathf.Clamp(Mathf.CeilToInt(wid / 0.08f), 1, 6);
                Vector3 P(float l, float w, float t) => c + lenDir * l + widDir * w + n * t;
                float U(float l) => uOff + (l + hl) / uvLen;
                int from = pos.Count;
                // Top.
                Quad(P(-hl + b, -hw + b, ht), P(hl - b, -hw + b, ht), P(hl - b, hw - b, ht), P(-hl + b, hw - b, ht), n, col,
                     new Vector2(U(-hl + b), v0), new Vector2(U(hl - b), v0), new Vector2(U(hl - b), v1), new Vector2(U(-hl + b), v1), false, su, sw);
                // Chamfers along both long edges and both ends.
                var nA = (n - widDir).normalized; var nB = (n + widDir).normalized;
                Quad(P(-hl + b, -hw, ht - b), P(hl - b, -hw, ht - b), P(hl - b, -hw + b, ht), P(-hl + b, -hw + b, ht), nA, col * 0.92f,
                     new Vector2(U(-hl), v0), new Vector2(U(hl), v0), new Vector2(U(hl), v0 + 0.01f), new Vector2(U(-hl), v0 + 0.01f), false, su, 1);
                Quad(P(-hl + b, hw - b, ht), P(hl - b, hw - b, ht), P(hl - b, hw, ht - b), P(-hl + b, hw, ht - b), nB, col * 0.92f,
                     new Vector2(U(-hl), v1 - 0.01f), new Vector2(U(hl), v1 - 0.01f), new Vector2(U(hl), v1), new Vector2(U(-hl), v1), false, su, 1);
                var nC = (n - lenDir).normalized; var nD = (n + lenDir).normalized;
                Quad(P(-hl, -hw + b, ht - b), P(-hl + b, -hw + b, ht), P(-hl + b, hw - b, ht), P(-hl, hw - b, ht - b), nC, col * 0.85f,
                     new Vector2(U(-hl), v0), new Vector2(U(-hl), v0), new Vector2(U(-hl), v1), new Vector2(U(-hl), v1), false, 1, sw);
                Quad(P(hl - b, -hw + b, ht), P(hl, -hw + b, ht - b), P(hl, hw - b, ht - b), P(hl - b, hw - b, ht), nD, col * 0.85f,
                     new Vector2(U(hl), v0), new Vector2(U(hl), v0), new Vector2(U(hl), v1), new Vector2(U(hl), v1), false, 1, sw);
                // Sides (seen in the gaps).
                Quad(P(-hl, -hw, -ht), P(hl, -hw, -ht), P(hl, -hw, ht - b), P(-hl, -hw, ht - b), -widDir, col * 0.6f,
                     new Vector2(U(-hl), v0), new Vector2(U(hl), v0), new Vector2(U(hl), v0), new Vector2(U(-hl), v0), false, su, 1);
                Quad(P(-hl, hw, ht - b), P(hl, hw, ht - b), P(hl, hw, -ht), P(-hl, hw, -ht), widDir, col * 0.6f,
                     new Vector2(U(-hl), v1), new Vector2(U(hl), v1), new Vector2(U(hl), v1), new Vector2(U(-hl), v1), false, su, 1);
                if (isShell) for (int i = from; i < pos.Count; i++) shell[i] = true;
            }

            /// <summary>
            /// A box with properly rounded edges and corners (radius r): a subdivided cube whose points are pushed
            /// onto the rounded shape, with smooth normals. For cushions, leather, soft furniture and rounded wood.
            /// </summary>
            public void RoundBox(Vector3 c, Vector3 size, float r, Color col, Quaternion? rotation = null,
                                 bool emissive = false, float uvScale = 0.6f, bool occ = true)
            {
                var rot = rotation ?? Quaternion.identity;
                var h = size / 2;
                r = Mathf.Min(r, Mathf.Min(h.x, Mathf.Min(h.y, h.z)) * 0.98f);
                var inner = new Vector3(h.x - r, h.y - r, h.z - r);
                // Grid lines: denser near the edges where the rounding is.
                List<float> Lines(float half)
                {
                    var l = new List<float>();
                    float flat = half - r;
                    for (int k = 0; k <= 4; k++) l.Add(-half + r * (1f - Mathf.Cos(k / 4f * Mathf.PI / 2f)));
                    int mid = Mathf.Max(1, Mathf.CeilToInt(2f * flat / 0.08f));
                    for (int k = 1; k < mid; k++) l.Add(-flat + 2f * flat * k / mid);
                    for (int k = 4; k >= 0; k--) l.Add(half - r * (1f - Mathf.Cos(k / 4f * Mathf.PI / 2f)));
                    return l;
                }
                var lx = Lines(h.x); var ly = Lines(h.y); var lz = Lines(h.z);
                var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
                var lines = new[] { lx, ly, lz };
                float Hc(int a) => a == 0 ? h.x : a == 1 ? h.y : h.z;
                float Ic(int a) => a == 0 ? inner.x : a == 1 ? inner.y : inner.z;
                for (int a = 0; a < 3; a++)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        int a1 = (a + 1) % 3, a2 = (a + 2) % 3;
                        var L1 = lines[a1]; var L2 = lines[a2];
                        int baseIdx = pos.Count;
                        for (int j = 0; j < L2.Count; j++)
                            for (int i = 0; i < L1.Count; i++)
                            {
                                var p = axes[a] * (s * Hc(a)) + axes[a1] * L1[i] + axes[a2] * L2[j];
                                var q = new Vector3(Mathf.Clamp(p.x, -inner.x, inner.x), Mathf.Clamp(p.y, -inner.y, inner.y), Mathf.Clamp(p.z, -inner.z, inner.z));
                                var d = p - q;
                                var nn = d.sqrMagnitude > 1e-10f ? d.normalized : axes[a] * s;
                                var pr = q + nn * r;
                                var uvp = new Vector2((L1[i] + Hc(a1)) / uvScale, (L2[j] + Hc(a2)) / uvScale);
                                V(c + rot * pr, rot * nn, uvp, col, emissive, false);
                            }
                        for (int j = 0; j < L2.Count - 1; j++)
                            for (int i = 0; i < L1.Count - 1; i++)
                            {
                                int i0 = baseIdx + j * L1.Count + i, i1 = i0 + L1.Count;
                                tris.Add(i0); tris.Add(i1); tris.Add(i1 + 1);
                                tris.Add(i0); tris.Add(i1 + 1); tris.Add(i0 + 1);
                            }
                    }
                if (occ && !emissive) room.AddOcc(c, rot * Vector3.right, rot * Vector3.up, rot * Vector3.forward, h * 0.95f);
            }

            /// <summary>Horizontal cylinder along an arbitrary axis (rounded rails, handles, poles, legs).</summary>
            public void Rod(Vector3 a, Vector3 b, float r, Color col, int seg = 10, bool caps = true)
            {
                var axis = b - a;
                float len = axis.magnitude;
                if (len < 1e-4f) return;
                var dir = axis / len;
                var side = Mathf.Abs(dir.y) < 0.9f ? Vector3.Cross(dir, Vector3.up).normalized : Vector3.Cross(dir, Vector3.right).normalized;
                var up = Vector3.Cross(side, dir);
                int along = Mathf.Clamp(Mathf.CeilToInt(len / 0.15f), 1, 30);
                int baseIdx = pos.Count;
                for (int j = 0; j <= along; j++)
                    for (int i = 0; i <= seg; i++)
                    {
                        float t = i / (float)seg * Mathf.PI * 2f;
                        var n = side * Mathf.Cos(t) + up * Mathf.Sin(t);
                        V(a + axis * (j / (float)along) + n * r, n, new Vector2(i / (float)seg, j * len / along), col, false, false);
                    }
                for (int j = 0; j < along; j++)
                    for (int i = 0; i < seg; i++)
                    {
                        int i0 = baseIdx + j * (seg + 1) + i, i1 = i0 + seg + 1;
                        tris.Add(i0); tris.Add(i1); tris.Add(i1 + 1);
                        tris.Add(i0); tris.Add(i1 + 1); tris.Add(i0 + 1);
                    }
                if (!caps) return;
                foreach (var (end, nd) in new[] { (a, -dir), (b, dir) })
                {
                    int ci = V(end, nd, Vector2.zero, col, false, false);
                    for (int i = 0; i < seg; i++)
                    {
                        float t0 = i / (float)seg * Mathf.PI * 2f, t1 = (i + 1) / (float)seg * Mathf.PI * 2f;
                        int p0 = V(end + (side * Mathf.Cos(t0) + up * Mathf.Sin(t0)) * r, nd, Vector2.zero, col, false, false);
                        int p1 = V(end + (side * Mathf.Cos(t1) + up * Mathf.Sin(t1)) * r, nd, Vector2.zero, col, false, false);
                        tris.Add(ci); tris.Add(p0); tris.Add(p1);
                    }
                }
            }

            /// <summary>An irregular, slightly domed flat shape lying on the floor (a sheepskin).</summary>
            public void Blob(Vector3 c, float rx, float rz, Color col, int seed)
            {
                const int Seg = 40, Rings = 5;
                int centre = V(c + Vector3.up * 0.012f, Vector3.up, new Vector2(0.5f, 0.5f), col, false, false);
                int prevStart = -1;
                for (int k = 1; k <= Rings; k++)
                {
                    float f = k / (float)Rings;
                    int start = pos.Count;
                    for (int i = 0; i < Seg; i++)
                    {
                        float a = i / (float)Seg * Mathf.PI * 2f;
                        float wob = 1f + 0.12f * Mathf.Sin(a * 3f + seed) + 0.07f * Mathf.Sin(a * 7f + seed * 2.3f) + 0.04f * Mathf.Sin(a * 13f + seed);
                        var off = new Vector3(Mathf.Cos(a) * rx, 0, Mathf.Sin(a) * rz) * (f * (k == Rings ? wob : 1f + (wob - 1f) * f));
                        float hgt = 0.012f * (1f - f * f);
                        var nn = (Vector3.up + off.normalized * (0.6f * f)).normalized;
                        V(c + off + Vector3.up * hgt, nn, new Vector2(off.x / 0.5f + 0.5f, off.z / 0.5f + 0.5f), col, false, false);
                    }
                    for (int i = 0; i < Seg; i++)
                    {
                        int a0 = start + i, a1 = start + (i + 1) % Seg;
                        if (k == 1) { tris.Add(centre); tris.Add(a1); tris.Add(a0); }
                        else
                        {
                            int b0 = prevStart + i, b1 = prevStart + (i + 1) % Seg;
                            tris.Add(b0); tris.Add(a1); tris.Add(a0);
                            tris.Add(b0); tris.Add(b1); tris.Add(a1);
                        }
                    }
                    prevStart = start;
                }
            }

            /// <summary>A vertex with a final (unlit) colour; returns its index.</summary>
            public int Vert(Vector3 p, Vector3 n, Color c, Vector2? uv = null) => V(p, n, uv ?? Vector2.zero, c, true, false);
            public void Tri(int a, int b, int c) { tris.Add(a); tris.Add(b); tris.Add(c); }

            /// <summary>
            /// A (truncated) cone with per-vertex colours from colourFn(position, normal); jag > 0 makes the bottom
            /// rim ragged (alternating radius) like drooping branches.
            /// </summary>
            public void ConeColoured(Vector3 basePos, float rBottom, float rTop, float height, int seg,
                                     System.Func<Vector3, Vector3, Color> colourFn, float jag = 0f, float twist = 0f)
            {
                int baseIdx = pos.Count;
                for (int i = 0; i <= seg; i++)
                {
                    float a = i / (float)seg * Mathf.PI * 2f + twist;
                    var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    float rb = rBottom * (1f + ((i % 2 == 0) ? jag : -jag * 0.6f));
                    var pb = basePos + dir * rb - Vector3.up * (jag * rBottom * 0.5f * (i % 2));
                    var pt = basePos + dir * rTop + Vector3.up * height;
                    var n = (dir * height + Vector3.up * (rb - rTop)).normalized;
                    V(pb, n, Vector2.zero, colourFn(pb, n), true, false);
                    V(pt, n, Vector2.zero, colourFn(pt, (n + Vector3.up).normalized), true, false);
                }
                for (int i = 0; i < seg; i++)
                {
                    int i0 = baseIdx + i * 2;
                    tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 3);
                    tris.Add(i0); tris.Add(i0 + 3); tris.Add(i0 + 2);
                }
                // Underside (seen from the low window when looking up at near trees).
                int c = V(basePos, Vector3.down, Vector2.zero, colourFn(basePos, Vector3.down) * 0.5f, true, false);
                for (int i = 0; i < seg; i++) { tris.Add(c); tris.Add(baseIdx + i * 2); tris.Add(baseIdx + i * 2 + 2); }
            }

            /// <summary>Applies a transform to every vertex added since index 'from' (positions and normals).</summary>
            public void Transform(int from, Matrix4x4 m)
            {
                for (int i = from; i < pos.Count; i++)
                {
                    pos[i] = m.MultiplyPoint3x4(pos[i]);
                    nrm[i] = m.MultiplyVector(nrm[i]).normalized;
                }
            }

            /// <summary>A box rotated about its centre.</summary>
            public void BoxRotated(Vector3 c, Vector3 size, Quaternion rot, Color col, bool emissive = false)
            {
                int from = pos.Count;
                bool rec = room.recordOcc; room.recordOcc = false;
                Box(Vector3.zero, size, col, emissive);
                room.recordOcc = rec;
                if (!emissive) room.AddOcc(c, rot * Vector3.right, rot * Vector3.up, rot * Vector3.forward, size / 2);
                Transform(from, Matrix4x4.TRS(c, rot, Vector3.one));
            }

            /// <summary>
            /// Half a log from a to b, bulging towards 'inward' (the other half is inside the wall). Tessellated
            /// along its length so the baked lighting stays smooth.
            /// </summary>
            public void LogRun(Vector3 a, Vector3 b, Vector3 inward, float r, Color col)
            {
                var axis = b - a;
                float len = axis.magnitude;
                if (len < 0.05f) return;
                const int Around = 10;
                float seed = (a.x * 12.9898f + a.y * 78.233f + a.z * 37.719f) % 6.28f;
                int along = Mathf.Clamp(Mathf.CeilToInt(len / 0.12f), 1, 80);
                int baseIdx = pos.Count;
                for (int j = 0; j <= along; j++)
                {
                    float t = j / (float)along;
                    for (int i = 0; i <= Around; i++)
                    {
                        float th = Mathf.Lerp(-90f, 90f, i / (float)Around) * Mathf.Deg2Rad;
                        var n = inward * Mathf.Cos(th) + Vector3.up * Mathf.Sin(th);
                        // Hand-hewn: the radius and centre wander a little along the log.
                        float s = t * len;
                        float wob = 1f + 0.045f * Mathf.Sin(s * 1.3f + seed) + 0.025f * Mathf.Sin(s * 3.1f + seed * 1.7f) + 0.012f * Mathf.Sin(th * 3f + s * 2f + seed);
                        var sag = Vector3.up * (0.006f * Mathf.Sin(s * 0.9f + seed * 0.5f));
                        V(a + axis * t + sag + n * (r * wob), n, new Vector2(t * len / 1.6f + seed, i / (float)Around), col, false, true);
                    }
                }
                for (int j = 0; j < along; j++)
                    for (int i = 0; i < Around; i++)
                    {
                        int i0 = baseIdx + j * (Around + 1) + i, i1 = i0 + Around + 1;
                        tris.Add(i0); tris.Add(i1); tris.Add(i1 + 1);
                        tris.Add(i0); tris.Add(i1 + 1); tris.Add(i0 + 1);
                    }
            }

            /// <summary>A log lying along z.</summary>
            public void LogZ(Vector3 center, float r, float len, Color col)
            {
                const int Seg = 10;
                int b = pos.Count;
                for (int i = 0; i <= Seg; i++)
                {
                    float a = i / (float)Seg * Mathf.PI * 2f;
                    var n = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                    V(center + n * r + Vector3.back * len / 2, n, new Vector2(i / (float)Seg, 0), col, false, false);
                    V(center + n * r + Vector3.forward * len / 2, n, new Vector2(i / (float)Seg, 1), col, false, false);
                }
                for (int i = 0; i < Seg; i++)
                {
                    int i0 = b + i * 2;
                    tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 3);
                    tris.Add(i0); tris.Add(i0 + 3); tris.Add(i0 + 2);
                }
            }

            public void Sphere(Vector3 c, float r, Color col, bool emissive = false)
            {
                const int Lon = 12, Lat = 8;
                int b = pos.Count;
                for (int j = 0; j <= Lat; j++)
                {
                    float t = j / (float)Lat * Mathf.PI;
                    for (int i = 0; i <= Lon; i++)
                    {
                        float a = i / (float)Lon * Mathf.PI * 2f;
                        var n = new Vector3(Mathf.Sin(t) * Mathf.Cos(a), Mathf.Cos(t), Mathf.Sin(t) * Mathf.Sin(a));
                        V(c + n * r, n, new Vector2(i / (float)Lon, j / (float)Lat), col, emissive, false);
                    }
                }
                for (int j = 0; j < Lat; j++)
                    for (int i = 0; i < Lon; i++)
                    {
                        int i0 = b + j * (Lon + 1) + i, i1 = i0 + Lon + 1;
                        tris.Add(i0); tris.Add(i0 + 1); tris.Add(i1 + 1);
                        tris.Add(i0); tris.Add(i1 + 1); tris.Add(i1);
                    }
            }

            /// <summary>Opacity written into the vertex colours (the floor is partly see-through to its reflection).</summary>
            public float Alpha = 1f;

            /// <summary>Copies this builder's vertices into the baker's arrays at the given offset.</summary>
            public void Export(LightBaker b, int off)
            {
                for (int i = 0; i < pos.Count; i++)
                {
                    b.P[off + i] = N3(pos[i]);
                    b.N[off + i] = N3(nrm[i]);
                    b.Tint[off + i] = new System.Numerics.Vector3(tint[i].r, tint[i].g, tint[i].b);
                    b.Emissive[off + i] = emissive[i];
                    b.NoAo[off + i] = false;
                }
            }

            /// <summary>Mesh with the baked colours colours[off + i].</summary>
            public Mesh ToMesh(System.Numerics.Vector3[] colours, int off)
            {
                int n = pos.Count;
                var c = new Color[n];
                for (int i = 0; i < n; i++)
                {
                    var k = colours[off + i];
                    c[i] = new Color(Mathf.Clamp01(k.X), Mathf.Clamp01(k.Y), Mathf.Clamp01(k.Z), Alpha);
                }
                return Build(c);
            }

            /// <summary>Mesh coloured by its tint only (emissive scenery).</summary>
            public Mesh ToMeshUnlit()
            {
                var c = new Color[pos.Count];
                for (int i = 0; i < c.Length; i++) { c[i] = tint[i]; c[i].a = Alpha; }
                return Build(c);
            }

            Mesh Build(Color[] c)
            {
                int n = pos.Count;
                var v = new Vector3[n]; var t = new Vector2[n];
                for (int i = 0; i < n; i++) { v[i] = pos[i]; t[i] = uv[i]; }
                var m = new Mesh { name = "OlliVR_Room", hideFlags = HideFlags.HideAndDontSave };
                if (n > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.vertices = v; m.uv = t; m.colors = c;
                m.triangles = tris.ToArray();
                m.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
                m.UploadMeshData(true);
                Object.DontDestroyOnLoad(m);
                return m;
            }
        }
    }
}
