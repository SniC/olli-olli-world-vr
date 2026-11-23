using System.Collections.Generic;
using UnityEngine;

namespace OlliVR
{
    /// <summary>
    /// Geometry and baked lighting of the cozy room around the screen, in the recentered room frame
    /// (origin at the recentered head, x right, y up, z towards the screen).
    ///
    /// Layout: the screen floats in front of a dark accent wall above a low TV cabinet; floor lamps on both
    /// sides; a brick fireplace on the left wall; a window with a snowy night outside on the right wall; a rug,
    /// wooden floor and ceiling beams. Lighting is computed per vertex once (lamps, moonlight, embers, ambient,
    /// corner darkening). The fire's flicker is a separate additive mesh whose brightness animates per frame.
    /// </summary>
    internal class CozyRoom
    {
        public struct Part { public Mesh Mesh; public Texture Tex; }

        public readonly List<Part> Opaque = new();
        public Mesh FireLight;              // additive, same surfaces, fire contribution only
        public Vector3 FirePos;             // flames centre
        public float FireOpeningX, FireOpeningZ, FireOpeningY0;
        public Vector3 WindowCenter;        // on the wall plane
        public float WindowW, WindowH;
        public float RoomHalfW, ZBack, ZFront, Floor, Ceil;
        /// <summary>Solid furniture that blocks the screen glow (axis-aligned boxes, room frame).</summary>
        public readonly List<Bounds> Occluders = new();

        // Light sources (room frame).
        struct PointLight { public Vector3 Pos; public Color Col; public float Radius; public bool Lamp; }
        readonly List<PointLight> lights = new();
        PointLight fire;

        public void Build(float d, float screenCy, float sw, float sh, float floorY)
        {
            foreach (var p in Opaque) Object.Destroy(p.Mesh);
            Opaque.Clear();
            if (FireLight != null) Object.Destroy(FireLight);

            Floor = floorY; Ceil = floorY + 2.6f;
            RoomHalfW = 3.0f; ZBack = -2.6f; ZFront = d + 0.35f;
            float F = Floor;

            // ---- lights ----
            lights.Clear();
            Occluders.Clear();
            // One floor lamp on the right (the fire lights the left side).
            var lampCol = new Color(1.0f, 0.60f, 0.28f) * 0.95f;
            float lampZ = ZFront - 0.35f;
            lights.Add(new PointLight { Pos = new Vector3(2.35f, F + 1.42f, lampZ), Col = lampCol, Radius = 1.1f, Lamp = true });
            float fireZ = Mathf.Min(0.9f, ZFront - 1.2f);
            WindowCenter = new Vector3(RoomHalfW, F + 1.55f, fireZ);
            WindowW = 1.3f; WindowH = 1.25f;
            lights.Add(new PointLight { Pos = WindowCenter + new Vector3(-0.3f, 0, 0), Col = new Color(0.10f, 0.14f, 0.30f) * 0.6f, Radius = 1.3f });
            FireOpeningX = -RoomHalfW + 0.38f; FireOpeningZ = fireZ; FireOpeningY0 = F + 0.12f;
            FirePos = new Vector3(-RoomHalfW + 0.2f, F + 0.32f, fireZ);
            fire = new PointLight { Pos = new Vector3(FireOpeningX + 0.15f, F + 0.4f, fireZ), Col = new Color(1.0f, 0.42f, 0.12f) * 1.1f, Radius = 1.4f };
            // Steady part of the fire's glow (the flicker mesh adds the rest).
            lights.Add(new PointLight { Pos = fire.Pos, Col = fire.Col * 0.28f, Radius = fire.Radius });

            var floorB = new MeshBuilder(this, RoomTextures.Wood);
            var wood = new MeshBuilder(this, RoomTextures.Wood);
            var paper = new MeshBuilder(this, RoomTextures.Wallpaper);
            var plain = new MeshBuilder(this, RoomTextures.White);
            var rug = new MeshBuilder(this, RoomTextures.Rug);
            var brick = new MeshBuilder(this, RoomTextures.Brick);
            var sky = new MeshBuilder(this, RoomTextures.Sky);

            float W = RoomHalfW;
            var warmWall = new Color(0.93f, 0.80f, 0.62f);
            var accentWall = new Color(0.22f, 0.34f, 0.36f);

            // ---- shell ----
            // Floor: planks run along z.
            floorB.Grid(new Vector3(-W, F, ZBack), Vector3.right, Vector3.forward, 2 * W, ZFront - ZBack, Vector3.up,
                      Color.white, uvScale: new Vector2(1f / 1.6f, 1f / 1.6f), swapUV: true, shell: true);
            // Ceiling.
            plain.Grid(new Vector3(-W, Ceil, ZFront), Vector3.right, Vector3.back, 2 * W, ZFront - ZBack, Vector3.down,
                       new Color(0.90f, 0.84f, 0.74f), shell: true);
            // Front (accent) wall behind the screen.
            paper.Grid(new Vector3(W, F, ZFront), Vector3.left, Vector3.up, 2 * W, Ceil - F, Vector3.back,
                       accentWall, uvScale: new Vector2(1 / 1.2f, 1 / 1.2f), shell: true);
            // Back wall.
            paper.Grid(new Vector3(-W, F, ZBack), Vector3.right, Vector3.up, 2 * W, Ceil - F, Vector3.forward,
                       warmWall, uvScale: new Vector2(1 / 1.2f, 1 / 1.2f), shell: true);
            // Left wall (fireplace).
            paper.Grid(new Vector3(-W, F, ZFront), Vector3.back, Vector3.up, ZFront - ZBack, Ceil - F, Vector3.right,
                       warmWall, uvScale: new Vector2(1 / 1.2f, 1 / 1.2f), shell: true);
            // Right wall with the window hole.
            var hole = new Rect(WindowCenter.z - WindowW / 2 - ZBack, WindowCenter.y - WindowH / 2 - F, WindowW, WindowH);
            paper.Grid(new Vector3(W, F, ZBack), Vector3.forward, Vector3.up, ZFront - ZBack, Ceil - F, Vector3.left,
                       warmWall, uvScale: new Vector2(1 / 1.2f, 1 / 1.2f), shell: true, hole: hole);

            // Ceiling beams across the room.
            var beam = new Color(0.55f, 0.42f, 0.32f);
            for (float z = ZBack + 0.6f; z < ZFront - 0.3f; z += 1.0f)
                wood.Box(new Vector3(0, Ceil - 0.08f, z), new Vector3(2 * W, 0.16f, 0.14f), beam);
            // Skirting boards.
            var skirt = new Color(0.95f, 0.90f, 0.82f);
            plain.Box(new Vector3(0, F + 0.05f, ZFront - 0.01f), new Vector3(2 * W, 0.1f, 0.02f), skirt);
            plain.Box(new Vector3(0, F + 0.05f, ZBack + 0.01f), new Vector3(2 * W, 0.1f, 0.02f), skirt);
            plain.Box(new Vector3(-W + 0.01f, F + 0.05f, (ZFront + ZBack) / 2), new Vector3(0.02f, 0.1f, ZFront - ZBack), skirt);
            plain.Box(new Vector3(W - 0.01f, F + 0.05f, (ZFront + ZBack) / 2), new Vector3(0.02f, 0.1f, ZFront - ZBack), skirt);

            // ---- rug ----
            float rugZ = Mathf.Lerp(0f, ZFront - 0.5f, 0.45f);
            rug.Quad(new Vector3(-1.5f, F + 0.006f, rugZ - 1.0f), new Vector3(1.5f, F + 0.006f, rugZ - 1.0f),
                     new Vector3(1.5f, F + 0.006f, rugZ + 1.0f), new Vector3(-1.5f, F + 0.006f, rugZ + 1.0f),
                     Vector3.up, Color.white, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), subdiv: 10);

            // ---- TV around the screen (the screen image is the TV's panel) ----
            float screenBottom = screenCy - sh / 2;
            var tvBlack = new Color(0.05f, 0.05f, 0.06f);
            const float Bezel = 0.03f;
            float tvZ = d + 0.03f;   // bezel front just behind the screen plane
            plain.Box(new Vector3(0, screenCy, tvZ), new Vector3(sw + 2 * Bezel, sh + 2 * Bezel, 0.05f), tvBlack);
            plain.Box(new Vector3(0, screenCy, d + 0.075f), new Vector3(sw * 0.6f, sh * 0.6f, 0.05f), tvBlack * 0.8f);
            plain.Box(new Vector3(sw / 2 - 0.05f, screenBottom - Bezel * 0.5f, d + 0.004f), new Vector3(0.008f, 0.004f, 0.002f),
                      new Color(0.9f, 0.35f, 0.25f) * 0.6f, emissive: true); // standby LED

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
            wood.Box(new Vector3(0, y1 - T / 2, cabZ), new Vector3(cabW, T, Dc), cabCol);
            wood.Box(new Vector3(0, y0 + T / 2, cabZ), new Vector3(cabW, T, Dc), cabCol);
            wood.Box(new Vector3(-hw + T / 2, (y0 + y1) / 2, cabZ), new Vector3(T, cabH, Dc), cabCol);
            wood.Box(new Vector3(hw - T / 2, (y0 + y1) / 2, cabZ), new Vector3(T, cabH, Dc), cabCol);
            wood.Box(new Vector3(0, (y0 + y1) / 2, zb - 0.005f), new Vector3(cabW - 2 * T, cabH - 2 * T, 0.01f), cabInside);
            Occluders.Add(new Bounds(new Vector3(0, (y0 + y1) / 2, cabZ), new Vector3(cabW, cabH, Dc)));
            // Two dividers make three compartments.
            float xd1 = -cabW / 6, xd2 = cabW / 6;
            wood.Box(new Vector3(xd1, (y0 + y1) / 2, cabZ + 0.01f), new Vector3(T, cabH - 2 * T, Dc - 0.02f), cabCol * 0.9f);
            wood.Box(new Vector3(xd2, (y0 + y1) / 2, cabZ + 0.01f), new Vector3(T, cabH - 2 * T, Dc - 0.02f), cabCol * 0.9f);
            // Left compartment: a closed door with a brass knob.
            float doorX0 = -hw + T + 0.004f, doorX1 = xd1 - T / 2 - 0.004f;
            wood.Box(new Vector3((doorX0 + doorX1) / 2, (y0 + y1) / 2, zf + 0.01f), new Vector3(doorX1 - doorX0, cabH - 2 * T - 0.008f, 0.02f), cabCol * 1.08f);
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
            float neckTop = screenBottom - Bezel, neckZ = d + 0.07f;
            plain.Box(new Vector3(0, (y1 + neckTop) / 2, neckZ), new Vector3(0.08f, neckTop - y1, 0.035f), tvBlack * 1.3f);
            plain.Box(new Vector3(0, y1 + 0.008f, neckZ - 0.02f), new Vector3(0.42f, 0.016f, 0.22f), tvBlack * 1.3f);

            // ---- snake plant on the floor left of the cabinet (anything in front of the screen would be hidden by it) ----
            var potPos = new Vector3(-hw - 0.32f, F, cabZ - 0.05f);
            var potCol = new Color(0.88f, 0.84f, 0.76f);
            Occluders.Add(new Bounds(potPos + new Vector3(0, 0.195f, 0), new Vector3(0.32f, 0.39f, 0.32f)));
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
                plain.Cylinder(shadeBase, 0.22f, 0.20f, 0.34f, new Color(0.09f, 0.05f, 0.022f), 24, emissive: true, open: true);
                // Bright inside, seen through the openings.
                plain.Cylinder(shadeBase, 0.214f, 0.194f, 0.34f, new Color(1.0f, 0.82f, 0.55f), 24, emissive: true, open: true, inward: true);
                plain.Sphere(l.Pos, 0.035f, new Color(1f, 0.95f, 0.85f), emissive: true);
            }

            // ---- fireplace (left wall) ----
            float fx0 = -W, fx1 = FireOpeningX, fz = fireZ;
            float fw = 1.5f, fh = 1.15f, ow = 0.8f, oh = 0.6f, oy0 = FireOpeningY0;
            var brickCol = Color.white;
            // Front face with the opening (4 pieces), sides and top of the surround.
            brick.Face(new Vector3(fx1, F, fz - fw / 2), new Vector3(fx1, fh + F, fz - ow / 2), Vector3.right, brickCol, 0.8f);
            brick.Face(new Vector3(fx1, F, fz + ow / 2), new Vector3(fx1, fh + F, fz + fw / 2), Vector3.right, brickCol, 0.8f);
            brick.Face(new Vector3(fx1, F, fz - ow / 2), new Vector3(fx1, oy0, fz + ow / 2), Vector3.right, brickCol, 0.8f);
            brick.Face(new Vector3(fx1, oy0 + oh, fz - ow / 2), new Vector3(fx1, F + fh, fz + ow / 2), Vector3.right, brickCol, 0.8f);
            brick.Box(new Vector3((fx0 + fx1) / 2, F + fh / 2, fz - fw / 2 - 0.001f), new Vector3(fx1 - fx0, fh, 0.002f), brickCol, onlyFaces: BoxFaces.Back);
            brick.Box(new Vector3((fx0 + fx1) / 2, F + fh / 2, fz + fw / 2 + 0.001f), new Vector3(fx1 - fx0, fh, 0.002f), brickCol, onlyFaces: BoxFaces.Front);
            // Firebox interior (sooty).
            var soot = new Color(0.10f, 0.07f, 0.06f);
            float inner = fx0 + 0.04f;
            plain.Face(new Vector3(inner, oy0, fz - ow / 2), new Vector3(inner, oy0 + oh, fz + ow / 2), Vector3.right, soot, 1f);
            plain.Quad(new Vector3(inner, oy0, fz - ow / 2), new Vector3(fx1, oy0, fz - ow / 2), new Vector3(fx1, oy0 + oh, fz - ow / 2), new Vector3(inner, oy0 + oh, fz - ow / 2), Vector3.forward, soot);
            plain.Quad(new Vector3(inner, oy0, fz + ow / 2), new Vector3(inner, oy0 + oh, fz + ow / 2), new Vector3(fx1, oy0 + oh, fz + ow / 2), new Vector3(fx1, oy0, fz + ow / 2), Vector3.back, soot);
            plain.Quad(new Vector3(inner, oy0 + oh, fz - ow / 2), new Vector3(fx1, oy0 + oh, fz - ow / 2), new Vector3(fx1, oy0 + oh, fz + ow / 2), new Vector3(inner, oy0 + oh, fz + ow / 2), Vector3.down, soot);
            plain.Quad(new Vector3(inner, oy0, fz - ow / 2), new Vector3(inner, oy0, fz + ow / 2), new Vector3(fx1, oy0, fz + ow / 2), new Vector3(fx1, oy0, fz - ow / 2), Vector3.up, soot * 1.5f);
            // Logs and glowing embers.
            var logCol = new Color(0.30f, 0.18f, 0.10f);
            plain.LogZ(new Vector3(fx0 + 0.20f, oy0 + 0.06f, fz), 0.055f, 0.6f, logCol);
            plain.LogZ(new Vector3(fx0 + 0.28f, oy0 + 0.06f, fz + 0.05f), 0.05f, 0.5f, logCol * 0.9f);
            plain.Box(new Vector3(fx0 + 0.24f, oy0 + 0.012f, fz), new Vector3(0.28f, 0.02f, 0.55f), new Color(1.0f, 0.35f, 0.08f), emissive: true);
            // Mantel and hearth.
            wood.Box(new Vector3((fx0 + fx1 + 0.08f) / 2, F + fh + 0.04f, fz), new Vector3(fx1 - fx0 + 0.08f, 0.08f, fw + 0.2f), new Color(0.55f, 0.38f, 0.26f));
            plain.Box(new Vector3((fx0 + fx1 + 0.25f) / 2, F + 0.02f, fz), new Vector3(fx1 - fx0 + 0.25f, 0.04f, fw + 0.2f), new Color(0.45f, 0.42f, 0.40f));
            // Candles on the mantel.
            for (int i = -1; i <= 1; i++)
            {
                var cp = new Vector3(fx1 - 0.18f, F + fh + 0.08f, fz + i * 0.45f);
                float ch = 0.10f + 0.04f * (i + 1);
                plain.Cylinder(cp, 0.03f, 0.03f, ch, new Color(0.95f, 0.90f, 0.80f), 10);
                plain.Sphere(cp + new Vector3(0, ch + 0.025f, 0), 0.016f, new Color(1f, 0.8f, 0.4f), emissive: true);
            }

            // ---- window (right wall) ----
            float wz0 = WindowCenter.z - WindowW / 2, wz1 = WindowCenter.z + WindowW / 2;
            float wy0 = WindowCenter.y - WindowH / 2, wy1 = WindowCenter.y + WindowH / 2;
            float depth = 0.18f;
            var jamb = new Color(0.92f, 0.88f, 0.80f);
            plain.Quad(new Vector3(W, wy0, wz0), new Vector3(W + depth, wy0, wz0), new Vector3(W + depth, wy1, wz0), new Vector3(W, wy1, wz0), Vector3.forward, jamb);
            plain.Quad(new Vector3(W, wy0, wz1), new Vector3(W, wy1, wz1), new Vector3(W + depth, wy1, wz1), new Vector3(W + depth, wy0, wz1), Vector3.back, jamb);
            plain.Quad(new Vector3(W, wy1, wz0), new Vector3(W + depth, wy1, wz0), new Vector3(W + depth, wy1, wz1), new Vector3(W, wy1, wz1), Vector3.down, jamb);
            plain.Quad(new Vector3(W, wy0, wz0), new Vector3(W, wy0, wz1), new Vector3(W + depth, wy0, wz1), new Vector3(W + depth, wy0, wz0), Vector3.up, jamb);
            var frame = new Color(0.70f, 0.55f, 0.40f);
            float fxw = W + depth * 0.6f;
            wood.Box(new Vector3(fxw, WindowCenter.y, WindowCenter.z), new Vector3(0.04f, WindowH, 0.05f), frame);
            wood.Box(new Vector3(fxw, WindowCenter.y, WindowCenter.z), new Vector3(0.04f, 0.05f, WindowW), frame);
            wood.Box(new Vector3(W - 0.07f, wy0 - 0.025f, WindowCenter.z), new Vector3(0.16f, 0.05f, WindowW + 0.2f), frame);
            // Trim around the window on the wall.
            wood.Box(new Vector3(W - 0.01f, wy1 + 0.04f, WindowCenter.z), new Vector3(0.02f, 0.08f, WindowW + 0.16f), frame);
            wood.Box(new Vector3(W - 0.01f, WindowCenter.y, wz0 - 0.04f), new Vector3(0.02f, WindowH, 0.08f), frame);
            wood.Box(new Vector3(W - 0.01f, WindowCenter.y, wz1 + 0.04f), new Vector3(0.02f, WindowH, 0.08f), frame);
            // Night sky far outside.
            float sx = W + 4.0f;
            sky.Quad(new Vector3(sx, F - 0.3f, WindowCenter.z - 6f), new Vector3(sx, F - 0.3f, WindowCenter.z + 6f),
                     new Vector3(sx, F + 4.7f, WindowCenter.z + 6f), new Vector3(sx, F + 4.7f, WindowCenter.z - 6f),
                     Vector3.left, Color.white, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), emissive: true);

            foreach (var b in new[] { floorB, wood, paper, plain, rug, brick, sky })
                if (b.Count > 0) Opaque.Add(new Part { Mesh = b.ToMesh(false), Tex = b.Tex });

            // Fire flicker: the same surfaces, lit by the fire only, combined into one additive mesh.
            var combined = new MeshBuilder(this, RoomTextures.White);
            foreach (var b in new[] { floorB, wood, paper, plain, rug, brick }) combined.Append(b);
            FireLight = combined.ToMesh(true);
            Plugin.L.LogInfo($"Cozy room built: {Opaque.Count} parts, {combined.Count} vertices.");
        }

        // ---- lighting -----------------------------------------------------------------------------------

        Color Ambient(Vector3 p, Vector3 n, bool shell)
        {
            var c = new Color(0.016f, 0.013f, 0.013f) + new Color(0.005f, 0.004f, 0.003f) * (n.y * 0.5f + 0.5f);
            foreach (var l in lights) c += PointContribution(l, p, n);
            if (shell) c *= CornerOcclusion(p, n);
            return c;
        }

        Color PointContribution(PointLight l, Vector3 p, Vector3 n)
        {
            var to = l.Pos - p;
            float dist = Mathf.Max(0.05f, to.magnitude);
            var dir = to / dist;
            float ndl = Mathf.Max(0f, Vector3.Dot(n, dir));
            float att = 1f / (1f + (dist / l.Radius) * (dist / l.Radius));
            float shape = 1f;
            if (l.Lamp)
            {
                // Dense shade open at top and bottom: strong pools straight up and down, little through the fabric.
                float vertical = Mathf.Abs(dir.y);
                shape = 0.08f + 1.4f * vertical * vertical * vertical;
            }
            return l.Col * (ndl * att * shape);
        }

        Color FireContribution(Vector3 p, Vector3 n)
        {
            var c = PointContribution(fire, p, n);
            // The surround and wall beside/above the opening are shadowed from the fire inside.
            if (p.x < FireOpeningX - 0.01f && p.y > FireOpeningY0 + 0.62f) c *= 0.15f;
            return c;
        }

        float CornerOcclusion(Vector3 p, Vector3 n)
        {
            float o = 1f;
            o *= Occ(p.y - Floor, n.y > 0.5f);
            o *= Occ(Ceil - p.y, n.y < -0.5f);
            o *= Occ(p.x + RoomHalfW, n.x > 0.5f);
            o *= Occ(RoomHalfW - p.x, n.x < -0.5f);
            o *= Occ(p.z - ZBack, n.z > 0.5f);
            o *= Occ(ZFront - p.z, n.z < -0.5f);
            return o;
        }

        static float Occ(float dist, bool onThisPlane) => onThisPlane ? 1f : 1f - 0.35f * Mathf.Exp(-Mathf.Max(0, dist) / 0.25f);

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
                             bool emissive = false, int subdiv = 1)
            {
                var A = ta ?? new Vector2(0, 0); var B = tb ?? new Vector2(1, 0); var C = tc ?? new Vector2(1, 1); var D = td ?? new Vector2(0, 1);
                int s = Mathf.Max(1, subdiv);
                int baseIdx = pos.Count;
                for (int j = 0; j <= s; j++)
                    for (int i = 0; i <= s; i++)
                    {
                        float u = i / (float)s, v = j / (float)s;
                        var p = Vector3.Lerp(Vector3.Lerp(a, b, u), Vector3.Lerp(d, c, u), v);
                        var t = Vector2.Lerp(Vector2.Lerp(A, B, u), Vector2.Lerp(D, C, u), v);
                        V(p, n, t, col, emissive, false);
                    }
                for (int j = 0; j < s; j++)
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
                             Vector2? uvScale = null, bool swapUV = false, bool shell = false, Rect? hole = null)
            {
                var sc = uvScale ?? Vector2.one;
                var us = Breaks(uLen, hole?.xMin, hole?.xMax);
                var vs = Breaks(vLen, hole?.yMin, hole?.yMax);
                var idx = new int[us.Count, vs.Count];
                for (int j = 0; j < vs.Count; j++)
                    for (int i = 0; i < us.Count; i++)
                    {
                        var p = origin + uDir * us[i] + vDir * vs[j];
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

            static List<float> Breaks(float len, float? h0, float? h1)
            {
                const float Step = 0.15f;
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

            public void Box(Vector3 c, Vector3 size, Color col, bool emissive = false, BoxFaces onlyFaces = BoxFaces.All)
            {
                var h = size / 2;
                float uvs = 0.6f;
                void F(BoxFaces f, Vector3 n, Vector3 a, Vector3 b, Vector3 cc, Vector3 d, float uw, float vh)
                {
                    if ((onlyFaces & f) == 0) return;
                    int sub = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(uw, vh) / 0.3f), 1, 20);
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

            public Mesh ToMesh(bool fireOnly)
            {
                int n = pos.Count;
                var v = new Vector3[n]; var t = new Vector2[n]; var c = new Color[n];
                for (int i = 0; i < n; i++)
                {
                    v[i] = pos[i]; t[i] = uv[i];
                    if (fireOnly)
                        c[i] = emissive[i] ? Color.black : Mul(tint[i], room.FireContribution(pos[i], nrm[i]));
                    else
                        c[i] = emissive[i] ? tint[i] : Mul(tint[i], room.Ambient(pos[i], nrm[i], shell[i]));
                    c[i].a = 1f;
                }
                var m = new Mesh { name = "OlliVR_Room", hideFlags = HideFlags.HideAndDontSave };
                if (n > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.vertices = v; m.uv = t; m.colors = c;
                m.triangles = tris.ToArray();
                m.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
                m.UploadMeshData(true);
                Object.DontDestroyOnLoad(m);
                return m;
            }

            static Color Mul(Color a, Color b) => new(Mathf.Clamp01(a.r * b.r), Mathf.Clamp01(a.g * b.g), Mathf.Clamp01(a.b * b.b), 1f);
        }
    }
}
