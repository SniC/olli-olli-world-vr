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
        const float GlowBoost = 1.25f;     // colour gain on the glow (the source is an average, so it reads dim)

        readonly CozyRoom room = new();
        RenderTexture mipSource, glowRaw, glowSmooth, msaaL, msaaR, reflL, reflR;
        Material depthMat, texMat, texMatZ, glowMat, addMat;
        bool glowAdditive;
        Matrix4x4 projL, projR;
        Vector3 eyePosL, eyePosR;
        Quaternion eyeRotL = Quaternion.identity, eyeRotR = Quaternion.identity;
        float seatedHeight;
        bool ready;
        float snowOffset1, snowOffset2;
        System.Threading.Tasks.Task texTask;
        System.Diagnostics.Stopwatch texTimer;
        bool texturesReady;

        public bool Init()
        {
            int w = Mathf.Max(256, (int)(VRSession.EyeWidth * 0.75f));
            int h = Mathf.Max(256, (int)(VRSession.EyeHeight * 0.75f));
            // Eyes render into 4x MSAA targets, resolved into the plain textures SteamVR receives.
            msaaL = MakeRT("OlliVR_RoomL_MSAA", w, h, false, 24, 4);
            msaaR = MakeRT("OlliVR_RoomR_MSAA", w, h, false, 24, 4);
            Left = MakeRT("OlliVR_RoomL", w, h, false, 0);
            Right = MakeRT("OlliVR_RoomR", w, h, false, 0);
            // Half-size mirrored room for the glossy floor; its mips give the blur.
            reflL = MakeRT("OlliVR_ReflL", w / 2, h / 2, true, 24);
            reflR = MakeRT("OlliVR_ReflR", w / 2, h / 2, true, 24);
            reflL.mipMapBias = 1.0f; reflR.mipMapBias = 1.0f;
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

            // Depth only: blend Zero/One leaves the colour untouched (the floor reflection lies underneath).
            depthMat = ColoredMat(colored, BlendMode.Zero, BlendMode.One, zWrite: true, CompareFunction.LessEqual);
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

            // Textures are generated on a worker thread (pure managed maths) and uploaded when ready.
            texTimer = System.Diagnostics.Stopwatch.StartNew();
            texTask = System.Threading.Tasks.Task.Run(RoomTextures.Generate);


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
            if (!texturesReady)
            {
                if (!texTask.IsCompleted) return;
                if (texTask.IsFaulted) { Plugin.L.LogError("Room texture generation failed: " + texTask.Exception); ready = false; return; }
                RoomTextures.UploadAll();
                texturesReady = true;
                Plugin.L.LogInfo($"Room textures ready in {texTimer.ElapsedMilliseconds} ms (generated off the main thread).");
            }
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

            room.TryFinish();
            lastOriginPos = originPos; lastOriginRot = originRot;
            lastC = c; lastSw = sw; lastSh = sh; lastFloorY = floorY; lastGlow = glow; lastReflection = reflection;
            AnimateFire();
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            snowOffset1 = Mathf.Repeat(snowOffset1 + dt * 0.10f, 1f);
            snowOffset2 = Mathf.Repeat(snowOffset2 + dt * 0.06f, 1f);

            UpdateSparks(dt);
            lastScreenImg = screen.StereoTexture;
            RenderEye(msaaL, reflL, hmdPos + hmdRot * eyePosL, hmdRot * eyeRotL, projL, roomM, originRot, floorY, glow, reflection, screen.StereoTexture, 0f, 0.5f);
            msaaL.ResolveAntiAliasedSurface(Left);
            RenderEye(msaaR, reflR, hmdPos + hmdRot * eyePosR, hmdRot * eyeRotR, projR, roomM, originRot, floorY, glow, reflection, screen.StereoTexture, 0.5f, 1f);
            msaaR.ResolveAntiAliasedSurface(Right);
        }

        Vector3 lastOriginPos; Quaternion lastOriginRot = Quaternion.identity;
        Vector3 lastC; float lastSw, lastSh, lastFloorY, lastGlow, lastReflection;
        Texture lastScreenImg;

        /// <summary>Debug: renders the room from the recentered head position looking left, ahead and right.</summary>
        public void CaptureViews(string dir, System.Action<RenderTexture, string> save, Texture screenImage)
        {
            if (!ready || room.Opaque.Count == 0) return;
            var rt = MakeRT("OlliVR_RoomCapture", 1600, 1000, false, 24);
            var reflCap = MakeRT("OlliVR_RoomCaptureRefl", 800, 500, true, 24);
            reflCap.mipMapBias = 1.0f;
            var proj = Matrix4x4.Perspective(90f, 1.6f, 0.05f, 100f);
            var roomM = Matrix4x4.TRS(lastOriginPos, lastOriginRot, Vector3.one);
            foreach (var (name, yaw) in new[] { ("Left", -75f), ("Ahead", 0f), ("Right", 75f), ("Behind", 180f) })
            {
                var rot = lastOriginRot * Quaternion.Euler(12f, yaw, 0f);
                RenderEye(rt, reflCap, lastOriginPos, rot, proj, roomM, lastOriginRot, lastFloorY, lastGlow, lastReflection, lastScreenImg, 0f, 0.5f);
                DrawScreenForCapture(rt, lastOriginPos, rot, proj, roomM, screenImage);
                save(rt, System.IO.Path.Combine(dir, $"OlliVR_capture_Room{name}.png"));
            }
            rt.Release();
            reflCap.Release();
            Object.Destroy(reflCap);
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
                fireTarget = Random.Range(0.70f, 1.15f);
                nextFireChange = Time.unscaledTime + Random.Range(0.12f, 0.35f);
            }
            fireIntensity = Mathf.Lerp(fireIntensity, fireTarget, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 6f));
        }

        /// <summary>
        /// One eye. With floor reflections on, the room is first drawn mirrored under the floor into a half-size
        /// texture; that is blurred via its mip chain (a glossy, not mirror-like, finish) and laid down first.
        /// The floor is drawn partly transparent over it, then everything else on top.
        /// </summary>
        void RenderEye(RenderTexture rt, RenderTexture refl, Vector3 eyePos, Quaternion eyeRot, Matrix4x4 proj,
                       Matrix4x4 roomM, Quaternion roomRot, float floorY, float glow, float reflection,
                       Texture screenImg, float su0, float su1)
        {
            var prev = RenderTexture.active;
            var view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(eyePos, eyeRot, Vector3.one).inverse;
            var eyeLocal = roomM.inverse.MultiplyPoint3x4(eyePos);
            var localRot = Quaternion.Inverse(roomRot) * eyeRot;
            var camRight = localRot * Vector3.right;
            var camUp = localRot * Vector3.up;
            bool doRefl = refl != null && reflection > 0f && room.Opaque.Count > 0;

            if (doRefl)
            {
                RenderTexture.active = refl;
                GL.Clear(true, true, ClearColor);
                GL.PushMatrix();
                GL.LoadProjectionMatrix(proj);
                var mirror = roomM * Matrix4x4.TRS(new Vector3(0, 2f * floorY, 0), Quaternion.identity, new Vector3(1f, -1f, 1f));
                DrawScene(view, mirror, 1, glow, eyeLocal, camRight, camUp, screenImg, su0, su1, false);
                GL.PopMatrix();
            }

            RenderTexture.active = rt;
            GL.Clear(true, true, ClearColor);
            if (doRefl)
            {
                // The mirrored room, softly blurred, as the backdrop that shows through the varnished floor.
                GL.PushMatrix();
                GL.LoadOrtho();
                texMat.mainTexture = refl;
                texMat.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.Color(Color.white);
                GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0);
                GL.TexCoord2(0, 1); GL.Vertex3(0, 1, 0);
                GL.TexCoord2(1, 1); GL.Vertex3(1, 1, 0);
                GL.TexCoord2(1, 0); GL.Vertex3(1, 0, 0);
                GL.End();
                GL.PopMatrix();
            }
            GL.PushMatrix();
            GL.LoadProjectionMatrix(proj);
            DrawScene(view, roomM, 0, glow, eyeLocal, camRight, camUp, null, 0, 0, true);
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>Draws the room with the given model matrix (normal or mirrored).</summary>
        void DrawScene(Matrix4x4 view, Matrix4x4 model, int firstPart, float glow, Vector3 eyeLocal, Vector3 camRight, Vector3 camUp,
                       Texture screenImg, float su0, float su1, bool main)
        {
            GL.modelview = view;
            // Opaque room: depth first (no colour), then textures where the depth matches.
            for (int i = firstPart; i < room.Opaque.Count; i++)
            {
                var part = room.Opaque[i];
                depthMat.SetPass(0);
                Graphics.DrawMeshNow(part.Mesh, model);
                texMatZ.mainTexture = part.Tex;
                texMatZ.SetPass(0);
                Graphics.DrawMeshNow(part.Mesh, model);
            }

            // Fire light flickering over the room.
            if (room.FireLight != null)
            {
                addMat.color = new Color(fireIntensity, fireIntensity, fireIntensity, 1f);
                addMat.SetPass(0);
                Graphics.DrawMeshNow(room.FireLight, model);
            }

            // Scenery outside the window, far to near (seen only through the window thanks to the depth test).
            for (int i = 0; i < room.Outside.Count; i++)
            {
                texMatZ.mainTexture = room.Outside[i].Tex;
                texMatZ.SetPass(0);
                Graphics.DrawMeshNow(room.Outside[i].Mesh, model);
            }

            // Screen glow on the wall behind it and the floor.
            if (glow > 0f && glowMesh != null)
            {
                glowMat.mainTexture = glowSmooth;
                glowMat.SetPass(0);
                Graphics.DrawMeshNow(glowMesh, model);
            }

            GL.modelview = view * model;
            DrawFlames();
            DrawCandleFlames(eyeLocal);
            DrawSnow();
            if (screenImg != null) DrawScreenQuad(screenImg, su0, su1);
            if (main)
            {
                DrawSparks(camRight, camUp);
                DrawHalos(view, model, eyeLocal);
            }
        }

        /// <summary>The game picture on the TV (for the floor reflection; the real one is the SteamVR overlay).</summary>
        void DrawScreenQuad(Texture img, float u0, float u1)
        {
            texMatZ.mainTexture = img;
            texMatZ.SetPass(0);
            float x0 = lastC.x - lastSw / 2, x1 = lastC.x + lastSw / 2, y0 = lastC.y - lastSh / 2, y1 = lastC.y + lastSh / 2;
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            GL.TexCoord2(u0, 0); GL.Vertex3(x0, y0, lastC.z);
            GL.TexCoord2(u0, 1); GL.Vertex3(x0, y1, lastC.z);
            GL.TexCoord2(u1, 1); GL.Vertex3(x1, y1, lastC.z);
            GL.TexCoord2(u1, 0); GL.Vertex3(x1, y0, lastC.z);
            GL.End();
        }

        // ---- glow halos and embers ------------------------------------------------------------------------

        // Per-vertex maths for the effects below uses System.Numerics: Unity's Vector3 operators go through the
        // IL2CPP bridge and add up quickly when done thousands of times per frame.
        static System.Numerics.Vector3 N3(Vector3 v) => new(v.x, v.y, v.z);
        static void Vtx(System.Numerics.Vector3 v) => GL.Vertex3(v.X, v.Y, v.Z);

        Mesh haloMesh;

        /// <summary>A unit disc facing +z: bright centre, soft falloff to a black (no-op when added) rim.</summary>
        Mesh HaloMesh()
        {
            if (haloMesh != null) return haloMesh;
            const int Seg = 20;
            var v = new Vector3[1 + Seg * 2];
            var c = new Color[v.Length];
            var t = new int[Seg * 9];
            v[0] = Vector3.zero; c[0] = Color.white;
            for (int i = 0; i < Seg; i++)
            {
                float a = i / (float)Seg * Mathf.PI * 2f;
                float x = Mathf.Cos(a), y = Mathf.Sin(a);
                v[1 + i] = new Vector3(x * 0.3f, y * 0.3f, 0); c[1 + i] = new Color(0.28f, 0.28f, 0.28f, 1f);
                v[1 + Seg + i] = new Vector3(x, y, 0); c[1 + Seg + i] = Color.black;
            }
            for (int i = 0; i < Seg; i++)
            {
                int i0 = 1 + i, i1 = 1 + (i + 1) % Seg, o0 = 1 + Seg + i, o1 = 1 + Seg + (i + 1) % Seg, k = i * 9;
                t[k] = 0; t[k + 1] = i0; t[k + 2] = i1;
                t[k + 3] = i0; t[k + 4] = o0; t[k + 5] = o1;
                t[k + 6] = i0; t[k + 7] = o1; t[k + 8] = i1;
            }
            haloMesh = new Mesh { name = "OlliVR_Halo", hideFlags = HideFlags.HideAndDontSave };
            haloMesh.vertices = v; haloMesh.colors = c; haloMesh.triangles = t;
            haloMesh.UploadMeshData(true);
            Object.DontDestroyOnLoad(haloMesh);
            return haloMesh;
        }

        /// <summary>
        /// Soft additive halos around light sources (a cheap stand-in for bloom): one prebuilt disc per light,
        /// facing the eye and pulled slightly towards it so it does not clip into the surface behind.
        /// </summary>
        void DrawHalos(Matrix4x4 view, Matrix4x4 model, Vector3 eyeLocal)
        {
            if (room.Glows.Count == 0) return;
            var mesh = HaloMesh();
            GL.modelview = view;
            var eye = N3(eyeLocal);
            foreach (var g in room.Glows)
            {
                float k = g.fire ? fireIntensity : 1f;
                var p = N3(g.pos);
                var toEye = System.Numerics.Vector3.Normalize(eye - p);
                var c = p + toEye * System.MathF.Min(g.radius * 0.8f, 0.25f);
                addMat.color = g.colour * k;
                addMat.SetPass(0);
                var rot = Quaternion.LookRotation(new Vector3(toEye.X, toEye.Y, toEye.Z));
                Graphics.DrawMeshNow(mesh, model * Matrix4x4.TRS(new Vector3(c.X, c.Y, c.Z), rot, new Vector3(g.radius, g.radius, g.radius)));
            }
        }

        struct Spark { public System.Numerics.Vector3 P, V; public float Age, Life; }
        readonly Spark[] sparks = new Spark[36];
        readonly System.Random fxRng = new(5);
        float Rnd(float a, float b) => a + (float)fxRng.NextDouble() * (b - a);

        /// <summary>Embers drifting up from the fire and fading out, mostly up the flue.</summary>
        void UpdateSparks(float dt)
        {
            for (int i = 0; i < sparks.Length; i++)
            {
                ref var s = ref sparks[i];
                s.Age += dt;
                if (s.Age >= s.Life || s.Life <= 0f)
                {
                    s.P = new(room.FireOpeningX - 0.24f + Rnd(-0.06f, 0.1f), room.FireOpeningY0 + 0.1f + Rnd(0f, 0.08f), room.FireOpeningZ + Rnd(-0.22f, 0.22f));
                    s.V = new(Rnd(0.0f, 0.08f), Rnd(0.25f, 0.6f), Rnd(-0.05f, 0.05f));
                    s.Life = Rnd(0.5f, 1.5f);
                    s.Age = Rnd(0f, 0.2f);
                    continue;
                }
                s.V += new System.Numerics.Vector3(Rnd(-0.4f, 0.4f), 0.1f, Rnd(-0.4f, 0.4f)) * dt;
                s.P += s.V * dt;
            }
        }

        void DrawSparks(Vector3 rightU, Vector3 upU)
        {
            addMat.color = Color.white;
            addMat.SetPass(0);
            var right = N3(rightU) * 0.005f;
            var up = N3(upU) * 0.005f;
            GL.Begin(GL.QUADS);
            foreach (var s in sparks)
            {
                if (s.Life <= 0f) continue;
                float t = s.Age / s.Life;
                float b = (1f - t) * (1f - t) * (0.6f + 0.4f * System.MathF.Sin(s.Age * 40f + s.Life * 13f)) * 1.4f;
                GL.Color(new Color(b, 0.55f * b, 0.15f * b, 1f));
                Vtx(s.P - right - up);
                Vtx(s.P - right + up * 2.5f);
                Vtx(s.P + right + up * 2.5f);
                Vtx(s.P + right - up);
            }
            GL.End();
        }

        /// <summary>Small animated candle flames, each turned to face the eye (rotating about the vertical).</summary>
        void DrawCandleFlames(Vector3 eyeLocalU)
        {
            if (room.Candles.Count == 0) return;
            var m = glowAdditive ? glowMat : texMatZ;
            m.mainTexture = RoomTextures.Flames;
            m.SetPass(0);
            float t = Time.unscaledTime;
            var eye = N3(eyeLocalU);
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            for (int i = 0; i < room.Candles.Count; i++)
            {
                var (pu, s) = room.Candles[i];
                var p = N3(pu);
                var toEye = eye - p; toEye.Y = 0f;
                var right = toEye.LengthSquared() > 1e-6f
                    ? System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(toEye.Z, 0, -toEye.X))
                    : System.Numerics.Vector3.UnitX;
                int frame = (int)(t * 14f + i * 3.7f) % RoomTextures.FlameFrames;
                float u0 = frame / (float)RoomTextures.FlameFrames, u1 = (frame + 1) / (float)RoomTextures.FlameFrames;
                float flicker = 0.9f + 0.1f * System.MathF.Sin(t * 11f + i * 2.3f) * System.MathF.Sin(t * 7.3f + i);
                float w = 0.026f * s, h = 0.06f * s * flicker;
                var a = p - right * (w / 2);
                var b = p + right * (w / 2);
                var top = new System.Numerics.Vector3(0, h, 0);
                var down = new System.Numerics.Vector3(0, 0.006f, 0);
                GL.TexCoord2(u0, 0); Vtx(a - down);
                GL.TexCoord2(u0, 1); Vtx(a + top);
                GL.TexCoord2(u1, 1); Vtx(b + top);
                GL.TexCoord2(u1, 0); Vtx(b - down);
            }
            GL.End();
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
                     return 0.7f * Falloff(Mathf.Sqrt(dx * dx + dy * dy)) *
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
