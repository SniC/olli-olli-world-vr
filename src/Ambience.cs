using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;

namespace OlliVR
{
    /// <summary>
    /// The room around the virtual screen, rendered per eye as SteamVR's scene layer (the screen overlay is
    /// composited on top of it).
    ///
    /// A cozy living room (see CozyRoom) with lamps, a crackling fireplace and a snowy window, plus an
    /// "Ambilight" glow: the game image is reduced to a tiny, temporally smoothed texture that the wall behind the
    /// screen and the floor sample with clamped UVs, so each edge's colour spills outwards and fades with distance.
    ///
    /// The game ships no textured shader that writes depth, so opaque geometry is drawn twice: a depth-only pass
    /// (Hidden/Internal-Colored with ZWrite) and then the textured pass (UI/Default with ZTest LEqual), which
    /// gives correct occlusion. All static geometry lives in meshes built once; per frame only a handful of draw
    /// calls are issued.
    /// </summary>
    internal class Ambience
    {
        public RenderTexture Left { get; private set; }
        public RenderTexture Right { get; private set; }

        static readonly Color ClearColor = new(0.02f, 0.02f, 0.026f, 1f);
        const float GlowFalloff = 0.28f;   // metres for the glow to drop to ~37%: concentrated behind the TV
        const float GlowBoost = 1.6f;      // colour gain on the glow (the source is an average, so it reads dim)

        readonly CozyRoom room = new();
        RenderTexture mipSource, glowRaw, glowSmooth, msaaL, msaaR;
        Material depthMat, texMat, texMatZ, glowMat, addMat;
        bool glowAdditive;
        Matrix4x4 projL, projR;
        Vector3 eyePosL, eyePosR;
        Quaternion eyeRotL = Quaternion.identity, eyeRotR = Quaternion.identity;
        float seatedHeight;
        bool ready;
        float snowOffset1, snowOffset2;

        public bool Init()
        {
            int w = Mathf.Max(256, (int)(VRSession.EyeWidth * 0.75f));
            int h = Mathf.Max(256, (int)(VRSession.EyeHeight * 0.75f));
            // Eyes render into 4x MSAA targets, resolved into the plain textures SteamVR receives.
            msaaL = MakeRT("OlliVR_RoomL_MSAA", w, h, false, 24, 4);
            msaaR = MakeRT("OlliVR_RoomR_MSAA", w, h, false, 24, 4);
            Left = MakeRT("OlliVR_RoomL", w, h, false, 0);
            Right = MakeRT("OlliVR_RoomR", w, h, false, 0);
            Clear(Left, ClearColor); Clear(Right, ClearColor);

            mipSource = MakeRT("OlliVR_GlowMips", 256, 144, true, 0);
            glowRaw = MakeRT("OlliVR_GlowRaw", 24, 14, false, 0);
            glowSmooth = MakeRT("OlliVR_Glow", 24, 14, false, 0);
            Clear(glowSmooth, Color.black);
            mipSource.mipMapBias = 1.5f;

            var colored = Shader.Find("Hidden/Internal-Colored");
            var textured = Shader.Find("UI/Default");
            if (colored == null || textured == null)
            {
                Plugin.L.LogWarning("Ambience: shaders not found; plain backdrop.");
                return true; // textures still valid as a plain backdrop
            }

            depthMat = ColoredMat(colored, BlendMode.One, BlendMode.Zero, zWrite: true, CompareFunction.LessEqual);
            depthMat.color = Color.black;
            addMat = ColoredMat(colored, BlendMode.One, BlendMode.One, zWrite: false, CompareFunction.LessEqual);
            texMat = TexturedMat(textured, CompareFunction.Always);
            texMatZ = TexturedMat(textured, CompareFunction.LessEqual);

            // An additive textured shader makes the screen glow add light instead of tinting; fall back otherwise.
            var additive = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Mobile/Particles/Additive");
            if (additive != null)
            {
                glowMat = new Material(additive) { hideFlags = HideFlags.HideAndDontSave };
                glowMat.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
                Object.DontDestroyOnLoad(glowMat);
                glowAdditive = true;
            }
            else
            {
                // Its own material so the colour gain only applies to the glow.
                glowMat = TexturedMat(textured, CompareFunction.LessEqual);
                glowMat.color = new Color(GlowBoost, GlowBoost, GlowBoost, 1f);
            }

            RoomTextures.Generate();

            projL = VRSession.GetEyeProjection(EVREye.Eye_Left, 0.05f, 100f);
            projR = VRSession.GetEyeProjection(EVREye.Eye_Right, 0.05f, 100f);
            VRSession.GetEyeToHead(EVREye.Eye_Left, out eyePosL, out eyeRotL);
            VRSession.GetEyeToHead(EVREye.Eye_Right, out eyePosR, out eyeRotR);
            seatedHeight = VRSession.SeatedHeightAboveFloor();
            ready = true;
            Plugin.L.LogInfo($"Ambience ready: {w}x{h} per eye, seated height {seatedHeight:0.00} m, " +
                             $"glow {(glowAdditive ? "additive" : "blended")}.");
            return true;
        }

        static Material ColoredMat(Shader s, BlendMode src, BlendMode dst, bool zWrite, CompareFunction zTest)
        {
            var m = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            m.SetInt("_SrcBlend", (int)src);
            m.SetInt("_DstBlend", (int)dst);
            m.SetInt("_Cull", (int)CullMode.Off);
            m.SetInt("_ZWrite", zWrite ? 1 : 0);
            m.SetInt("_ZTest", (int)zTest);
            Object.DontDestroyOnLoad(m);
            return m;
        }

        static Material TexturedMat(Shader s, CompareFunction zTest)
        {
            var m = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            m.SetInt("unity_GUIZTestMode", (int)zTest);
            Object.DontDestroyOnLoad(m);
            return m;
        }

        static RenderTexture MakeRT(string name, int w, int h, bool mips, int depth, int msaa = 1)
        {
            var rt = new RenderTexture(w, h, depth, RenderTextureFormat.ARGB32)
            {
                name = name, hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp,
                useMipMap = mips, autoGenerateMips = mips, filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
                antiAliasing = msaa,
            };
            rt.Create();
            Object.DontDestroyOnLoad(rt);
            return rt;
        }

        static void Clear(RenderTexture rt, Color c)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, c);
            RenderTexture.active = prev;
        }

        /// <summary>Reduces the finished screen image to the small, smoothed glow texture.</summary>
        public void UpdateGlow(RenderTexture screenImage)
        {
            if (!ready) return;
            Graphics.Blit(screenImage, mipSource);   // mips are generated on resolve
            Graphics.Blit(mipSource, glowRaw);       // minified blit samples a low mip: a soft average
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 10f);
            DrawFullscreen(glowRaw, glowSmooth, k);
        }

        void DrawFullscreen(Texture src, RenderTexture dst, float alpha)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = dst;
            GL.PushMatrix();
            GL.LoadOrtho();
            texMat.mainTexture = src;
            texMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(new Color(1f, 1f, 1f, alpha));
            GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0);
            GL.TexCoord2(0, 1); GL.Vertex3(0, 1, 0);
            GL.TexCoord2(1, 1); GL.Vertex3(1, 1, 0);
            GL.TexCoord2(1, 0); GL.Vertex3(1, 0, 0);
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        // ---- per frame ----------------------------------------------------------------------------------

        Vector4 roomKey = new(float.NaN, 0, 0, 0);
        float roomKey2 = float.NaN, roomKey3 = float.NaN;
        Mesh glowMesh;
        float fireIntensity = 1f, fireTarget = 1f, nextFireChange;

        /// <summary>Renders both eyes of the room for the given head pose and screen placement.</summary>
        public void Render(Vector3 hmdPos, Quaternion hmdRot, Vector3 originPos, Quaternion originRot,
                           ScreenOverlays screen)
        {
            if (!ready) return;
            float glow = Mathf.Max(0f, Plugin.Cfg.AmbientGlow.Value);
            float reflection = Mathf.Clamp01(Plugin.Cfg.FloorReflection.Value);

            // Room frame: origin at the recentered head, axes of the recentered yaw.
            var roomM = Matrix4x4.TRS(originPos, originRot, Vector3.one);
            float floorY = (seatedHeight > 0.3f ? -seatedHeight : -1.2f) - originPos.y;
            float d = Mathf.Max(0.3f, Plugin.Cfg.ScreenDistance.Value);
            var c = new Vector3(0, Plugin.Cfg.ScreenHeightOffset.Value, d);
            float sw = screen.WidthMeters, sh = screen.HeightMeters;
            if (sw <= 0f || sh <= 0f) return;

            var key = new Vector4(c.y, c.z, sw, sh);
            if (glowMesh == null || key != roomKey || !Mathf.Approximately(floorY, roomKey2) || !Mathf.Approximately(glow, roomKey3))
            {
                roomKey = key; roomKey2 = floorY; roomKey3 = glow;
                room.Build(d, c.y, sw, sh, floorY);
                BuildGlowMesh(c, sw, sh, glow);
            }

            lastOriginPos = originPos; lastOriginRot = originRot;
            lastC = c; lastSw = sw; lastSh = sh; lastFloorY = floorY; lastGlow = glow; lastReflection = reflection;
            AnimateFire();
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            snowOffset1 = Mathf.Repeat(snowOffset1 + dt * 0.10f, 1f);
            snowOffset2 = Mathf.Repeat(snowOffset2 + dt * 0.06f, 1f);

            RenderEye(msaaL, hmdPos + hmdRot * eyePosL, hmdRot * eyeRotL, projL, roomM, c, sw, sh, floorY, glow, reflection);
            msaaL.ResolveAntiAliasedSurface(Left);
            RenderEye(msaaR, hmdPos + hmdRot * eyePosR, hmdRot * eyeRotR, projR, roomM, c, sw, sh, floorY, glow, reflection);
            msaaR.ResolveAntiAliasedSurface(Right);
        }

        Vector3 lastOriginPos; Quaternion lastOriginRot = Quaternion.identity;
        Vector3 lastC; float lastSw, lastSh, lastFloorY, lastGlow, lastReflection;

        /// <summary>Debug: renders the room from the recentered head position looking left, ahead and right.</summary>
        public void CaptureViews(string dir, System.Action<RenderTexture, string> save, Texture screenImage)
        {
            if (!ready || room.Opaque.Count == 0) return;
            var rt = MakeRT("OlliVR_RoomCapture", 1600, 1000, false, 24);
            var proj = Matrix4x4.Perspective(90f, 1.6f, 0.05f, 100f);
            var roomM = Matrix4x4.TRS(lastOriginPos, lastOriginRot, Vector3.one);
            foreach (var (name, yaw) in new[] { ("Left", -75f), ("Ahead", 0f), ("Right", 75f) })
            {
                var rot = lastOriginRot * Quaternion.Euler(12f, yaw, 0f);
                RenderEye(rt, lastOriginPos, rot, proj, roomM, lastC, lastSw, lastSh, lastFloorY, lastGlow, lastReflection);
                DrawScreenForCapture(rt, lastOriginPos, rot, proj, roomM, screenImage);
                save(rt, System.IO.Path.Combine(dir, $"OlliVR_capture_Room{name}.png"));
            }
            rt.Release();
            Object.Destroy(rt);
        }

        /// <summary>Debug capture only: the screen overlay is composited by SteamVR, so draw it in for the picture.</summary>
        void DrawScreenForCapture(RenderTexture rt, Vector3 eyePos, Quaternion eyeRot, Matrix4x4 proj, Matrix4x4 roomM, Texture img)
        {
            if (img == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.PushMatrix();
            GL.LoadProjectionMatrix(proj);
            GL.modelview = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(eyePos, eyeRot, Vector3.one).inverse * roomM;
            texMat.mainTexture = img;
            texMat.SetPass(0);
            float x0 = lastC.x - lastSw / 2, x1 = lastC.x + lastSw / 2, y0 = lastC.y - lastSh / 2, y1 = lastC.y + lastSh / 2;
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            GL.TexCoord2(0, 0); GL.Vertex3(x0, y0, lastC.z);
            GL.TexCoord2(0, 1); GL.Vertex3(x0, y1, lastC.z);
            GL.TexCoord2(0.5f, 1); GL.Vertex3(x1, y1, lastC.z);
            GL.TexCoord2(0.5f, 0); GL.Vertex3(x1, y0, lastC.z);
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        void AnimateFire()
        {
            // Smoothly wandering intensity with quick small flickers on top.
            if (Time.unscaledTime >= nextFireChange)
            {
                fireTarget = Random.Range(0.86f, 1.06f);
                nextFireChange = Time.unscaledTime + Random.Range(0.12f, 0.35f);
            }
            fireIntensity = Mathf.Lerp(fireIntensity, fireTarget, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 6f));
        }

        void RenderEye(RenderTexture rt, Vector3 eyePos, Quaternion eyeRot, Matrix4x4 proj, Matrix4x4 roomM,
                       Vector3 c, float sw, float sh, float floorY, float glow, float reflection)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, ClearColor);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(proj);
            var view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(eyePos, eyeRot, Vector3.one).inverse;
            GL.modelview = view;

            // Opaque room: depth first, then textures where the depth matches.
            // The floor reflection goes right after the shell (first part), before furniture can cover it.
            for (int i = 0; i < room.Opaque.Count; i++)
            {
                var part = room.Opaque[i];
                depthMat.SetPass(0);
                Graphics.DrawMeshNow(part.Mesh, roomM);
                texMatZ.mainTexture = part.Tex;
                texMatZ.SetPass(0);
                Graphics.DrawMeshNow(part.Mesh, roomM);
                if (i == 0 && reflection > 0f)
                {
                    // Wood floor (first part) is glossy: a blurred reflection of the screen.
                    GL.modelview = view * roomM;
                    texMat.mainTexture = mipSource;
                    texMat.SetPass(0);
                    DrawReflection(c, sw, sh, floorY, reflection);
                    GL.modelview = view;
                }
            }

            // Fire light flickering over the room.
            if (room.FireLight != null)
            {
                addMat.color = new Color(fireIntensity, fireIntensity, fireIntensity, 1f);
                addMat.SetPass(0);
                Graphics.DrawMeshNow(room.FireLight, roomM);
            }

            // Screen glow on the wall behind it and the floor.
            if (glow > 0f && glowMesh != null)
            {
                glowMat.mainTexture = glowSmooth;
                glowMat.SetPass(0);
                Graphics.DrawMeshNow(glowMesh, roomM);
            }

            // Animated bits: flames and snow (a few quads each).
            GL.modelview = view * roomM;
            DrawFlames();
            DrawSnow();

            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        void DrawFlames()
        {
            var m = glowAdditive ? glowMat : texMatZ;
            m.mainTexture = RoomTextures.Flames;
            m.SetPass(0);
            float t = Time.unscaledTime;
            var basePos = new Vector3(room.FireOpeningX - 0.2f, room.FireOpeningY0 + 0.03f, room.FireOpeningZ);
            GL.Begin(GL.QUADS);
            for (int i = 0; i < 3; i++)
            {
                int frame = (int)(t * 12f + i * 3) % RoomTextures.FlameFrames;
                float u0 = frame / (float)RoomTextures.FlameFrames, u1 = (frame + 1) / (float)RoomTextures.FlameFrames;
                float zc = basePos.z + (i - 1) * 0.16f, x = basePos.x - i % 2 * 0.05f;
                float w = i == 1 ? 0.34f : 0.26f, h = (i == 1 ? 0.46f : 0.34f) * (0.85f + 0.15f * fireIntensity);
                float a = glowAdditive ? 1f : 0.95f;
                GL.Color(new Color(1f, 1f, 1f, a));
                GL.TexCoord2(u0, 0); GL.Vertex3(x, basePos.y, zc - w / 2);
                GL.TexCoord2(u0, 1); GL.Vertex3(x, basePos.y + h, zc - w / 2);
                GL.TexCoord2(u1, 1); GL.Vertex3(x, basePos.y + h, zc + w / 2);
                GL.TexCoord2(u1, 0); GL.Vertex3(x, basePos.y, zc + w / 2);
            }
            GL.End();
        }

        void DrawSnow()
        {
            texMatZ.mainTexture = RoomTextures.Snow;
            texMatZ.SetPass(0);
            var wc = room.WindowCenter;
            GL.Begin(GL.QUADS);
            // Two layers outside the window at different distances, scale and speed (parallax).
            SnowLayer(wc.x + 0.6f, wc, 2.2f, 3.2f, snowOffset1, 0.75f);
            SnowLayer(wc.x + 1.8f, wc, 4.0f, 6.5f, snowOffset2, 0.45f);
            GL.End();
        }

        static void SnowLayer(float x, Vector3 wc, float halfSize, float tiles, float offset, float alpha)
        {
            float y0 = wc.y - halfSize, y1 = wc.y + halfSize, z0 = wc.z - halfSize, z1 = wc.z + halfSize;
            float sway = 0.03f * Mathf.Sin(Time.unscaledTime * 0.7f + x);
            GL.Color(new Color(1f, 1f, 1f, alpha));
            GL.TexCoord2(sway, offset); GL.Vertex3(x, y0, z0);
            GL.TexCoord2(sway, offset + tiles); GL.Vertex3(x, y1, z0);
            GL.TexCoord2(sway + tiles, offset + tiles); GL.Vertex3(x, y1, z1);
            GL.TexCoord2(sway + tiles, offset); GL.Vertex3(x, y0, z1);
        }

        // ---- screen glow mesh ---------------------------------------------------------------------------

        static float Falloff(float dist) => Mathf.Exp(-dist / GlowFalloff);

        /// <summary>
        /// The wall behind the screen and the floor in front of it, with UVs that map the screen rectangle to
        /// [0,1] (clamped outside, so edge colours extend outwards) and alpha falling off with distance.
        /// Slightly in front of the real surfaces so the depth test keeps it under furniture.
        /// </summary>
        void BuildGlowMesh(Vector3 c, float sw, float sh, float glow)
        {
            var verts = new System.Collections.Generic.List<Vector3>();
            var uvs = new System.Collections.Generic.List<Vector2>();
            var cols = new System.Collections.Generic.List<Color>();
            var tris = new System.Collections.Generic.List<int>();
            float strength = glowAdditive ? 1.0f : 1.0f;
            const int NX = 64, NY = 40;
            var occ = OccluderBoxes();
            float tvBackZ = c.z + 0.1f;

            void Grid(System.Func<float, float, Vector3> posAt, System.Func<Vector3, Vector2> uvAt, System.Func<Vector3, float> alphaAt)
            {
                int b = verts.Count;
                for (int j = 0; j <= NY; j++)
                    for (int i = 0; i <= NX; i++)
                    {
                        var p = posAt(i / (float)NX, j / (float)NY);
                        verts.Add(p); uvs.Add(uvAt(p));
                        cols.Add(new Color(1f, 1f, 1f, Mathf.Clamp01(strength * glow * alphaAt(p))));
                    }
                for (int j = 0; j < NY; j++)
                    for (int i = 0; i < NX; i++)
                    {
                        int i0 = b + j * (NX + 1) + i, i1 = i0 + NX + 1;
                        tris.Add(i0); tris.Add(i1); tris.Add(i1 + 1);
                        tris.Add(i0); tris.Add(i1 + 1); tris.Add(i0 + 1);
                    }
            }

            float W = room.RoomHalfW, F = room.Floor, zw = room.ZFront - 0.004f;
            Grid((u, v) => new Vector3(Mathf.Lerp(-W, W, u), Mathf.Lerp(F, room.Ceil, v), zw),
                 p => new Vector2((p.x - c.x) / sw + 0.5f, (p.y - c.y) / sh + 0.5f),
                 p =>
                 {
                     float dx = Mathf.Max(Mathf.Abs(p.x - c.x) - sw * 0.5f, 0f);
                     float dy = Mathf.Max(Mathf.Abs(p.y - c.y) - sh * 0.5f, 0f);
                     // Light comes from behind the TV; furniture between the TV and the wall casts shadow.
                     return 0.95f * Falloff(Mathf.Sqrt(dx * dx + dy * dy)) *
                            Visibility(p, occ, c.x - sw * 0.45f, c.x + sw * 0.45f, c.y - sh * 0.45f, c.y + sh * 0.45f, tvBackZ);
                 });
            float drop = c.y - sh * 0.5f - F;
            float floorStrength = 0.45f * Falloff(Mathf.Max(0f, drop) * 0.25f);
            Grid((u, v) => new Vector3(Mathf.Lerp(-W, W, u), F + 0.003f, Mathf.Lerp(-1.5f, room.ZFront, v)),
                 p => new Vector2((p.x - c.x) / sw + 0.5f, 0.04f),
                 p =>
                 {
                     float dx = Mathf.Max(Mathf.Abs(p.x - c.x) - sw * 0.5f, 0f);
                     float dz = Mathf.Max(c.z - p.z, 0f);
                     // The floor is lit by the glowing wall around the TV; the cabinet and the pot shade it.
                     return floorStrength * Falloff(Mathf.Sqrt(dx * dx + dz * dz) * 0.8f) *
                            Visibility(p, occ, c.x - sw * 0.5f, c.x + sw * 0.5f, c.y - sh * 0.5f - 0.15f, c.y + sh * 0.5f, room.ZFront - 0.01f);
                 });

            if (glowMesh == null)
            {
                glowMesh = new Mesh { name = "OlliVR_Glow", hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(glowMesh);
            }
            glowMesh.Clear();
            glowMesh.vertices = verts.ToArray();
            glowMesh.uv = uvs.ToArray();
            glowMesh.colors = cols.ToArray();
            glowMesh.triangles = tris.ToArray();
            glowMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            glowMesh.UploadMeshData(false);
        }

        (Vector3 min, Vector3 max)[] OccluderBoxes()
        {
            var list = new (Vector3, Vector3)[room.Occluders.Count];
            for (int i = 0; i < list.Length; i++) { var b = room.Occluders[i]; list[i] = (b.min, b.max); }
            return list;
        }

        /// <summary>
        /// Fraction of a 4x3 grid of light-source points (a rectangle in the plane z = srcZ) that can see p
        /// without the segment passing through an occluder box. Soft shadows from an area light, baked per vertex.
        /// </summary>
        static float Visibility(Vector3 p, (Vector3 min, Vector3 max)[] occ, float x0, float x1, float y0, float y1, float srcZ)
        {
            if (occ.Length == 0) return 1f;
            int seen = 0, total = 0;
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 4; i++)
                {
                    var s = new Vector3(Mathf.Lerp(x0, x1, i / 3f), Mathf.Lerp(y0, y1, j / 2f), srcZ);
                    total++;
                    bool blocked = false;
                    foreach (var (mn, mx) in occ)
                        if (SegmentHitsBox(s, p, mn, mx)) { blocked = true; break; }
                    if (!blocked) seen++;
                }
            return seen / (float)total;
        }

        /// <summary>Slab test: does the segment a-b pass through the box (excluding its very ends)?</summary>
        static bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 mn, Vector3 mx)
        {
            float t0 = 0.001f, t1 = 0.999f;
            for (int k = 0; k < 3; k++)
            {
                float o = a[k], d = b[k] - a[k];
                if (Mathf.Abs(d) < 1e-6f) { if (o < mn[k] || o > mx[k]) return false; continue; }
                float ta = (mn[k] - o) / d, tb = (mx[k] - o) / d;
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = Mathf.Max(t0, ta); t1 = Mathf.Min(t1, tb);
                if (t0 > t1) return false;
            }
            return true;
        }

        /// <summary>The screen mirrored in the floor plane, fading out away from the floor line.</summary>
        static void DrawReflection(Vector3 c, float sw, float sh, float floorY, float strength)
        {
            float x0 = c.x - sw * 0.5f, x1 = c.x + sw * 0.5f;
            float yb = 2f * floorY - (c.y - sh * 0.5f);   // mirrored bottom edge (nearest the floor)
            float yt = 2f * floorY - (c.y + sh * 0.5f);   // mirrored top edge
            float yMid = Mathf.Lerp(yb, yt, 0.45f);
            var near = new Color(1f, 1f, 1f, strength);
            var far = new Color(1f, 1f, 1f, 0f);
            GL.Begin(GL.QUADS);
            GL.Color(near); GL.TexCoord2(0, 0); GL.Vertex3(x0, yb, c.z);
            GL.Color(far); GL.TexCoord2(0, 0.45f); GL.Vertex3(x0, yMid, c.z);
            GL.Color(far); GL.TexCoord2(1, 0.45f); GL.Vertex3(x1, yMid, c.z);
            GL.Color(near); GL.TexCoord2(1, 0); GL.Vertex3(x1, yb, c.z);
            GL.End();
        }
    }
}
