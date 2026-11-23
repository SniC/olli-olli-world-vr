using UnityEngine;
using Valve.VR;

namespace OlliVR
{
    /// <summary>
    /// The room around the virtual screen, rendered per eye as SteamVR's scene layer (the screen overlay is
    /// composited on top of it).
    ///
    /// "Ambilight" style: a dark wall just behind the screen and a dark floor are lit by the colours at the
    /// screen's edges. The game image is reduced to a tiny, temporally smoothed texture; the wall and floor sample
    /// it with clamped UVs that extend past the screen rectangle, so each edge's colour spills outwards and fades
    /// with distance. The floor also shows a faint reflection of the screen.
    /// </summary>
    internal class Ambience
    {
        public RenderTexture Left { get; private set; }
        public RenderTexture Right { get; private set; }

        static readonly Color BaseColor = new(0.02f, 0.02f, 0.026f, 1f);
        const float WallGap = 0.35f;       // wall distance behind the screen, metres
        const float GlowReach = 2.6f;      // how far the wall/floor extend beyond the screen
        const float GlowFalloff = 0.5f;    // metres for the glow to drop to ~37%
        const int GridX = 36, GridY = 24;

        RenderTexture mipSource, glowRaw, glowSmooth;
        Material colorMat, texMat;
        Matrix4x4 projL, projR;
        Vector3 eyePosL, eyePosR;
        Quaternion eyeRotL = Quaternion.identity, eyeRotR = Quaternion.identity;
        float seatedHeight;
        bool ready, loggedFloor;

        public bool Init()
        {
            int w = Mathf.Max(256, (int)(VRSession.EyeWidth * 0.6f));
            int h = Mathf.Max(256, (int)(VRSession.EyeHeight * 0.6f));
            Left = MakeRT("OlliVR_RoomL", w, h, false);
            Right = MakeRT("OlliVR_RoomR", w, h, false);
            Clear(Left, BaseColor); Clear(Right, BaseColor);

            mipSource = MakeRT("OlliVR_GlowMips", 256, 144, true);
            glowRaw = MakeRT("OlliVR_GlowRaw", 24, 14, false);
            glowSmooth = MakeRT("OlliVR_Glow", 24, 14, false);
            glowRaw.filterMode = FilterMode.Bilinear;
            glowSmooth.filterMode = FilterMode.Bilinear;
            Clear(glowSmooth, Color.black);
            mipSource.mipMapBias = 1.5f;

            var colored = Shader.Find("Hidden/Internal-Colored");
            var textured = Shader.Find("UI/Default");
            if (colored == null || textured == null)
            {
                Plugin.L.LogWarning("Ambience: shaders not found; plain backdrop.");
                return true; // textures still valid as a plain backdrop
            }
            colorMat = new Material(colored) { hideFlags = HideFlags.HideAndDontSave };
            colorMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            colorMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            colorMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            colorMat.SetInt("_ZWrite", 0);
            colorMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            texMat = new Material(textured) { hideFlags = HideFlags.HideAndDontSave };
            texMat.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            Object.DontDestroyOnLoad(colorMat);
            Object.DontDestroyOnLoad(texMat);

            projL = VRSession.GetEyeProjection(EVREye.Eye_Left, 0.05f, 100f);
            projR = VRSession.GetEyeProjection(EVREye.Eye_Right, 0.05f, 100f);
            VRSession.GetEyeToHead(EVREye.Eye_Left, out eyePosL, out eyeRotL);
            VRSession.GetEyeToHead(EVREye.Eye_Right, out eyePosR, out eyeRotR);
            seatedHeight = VRSession.SeatedHeightAboveFloor();
            ready = true;
            Plugin.L.LogInfo($"Ambience ready: {w}x{h} per eye, seated height {seatedHeight:0.00} m.");
            return true;
        }

        static RenderTexture MakeRT(string name, int w, int h, bool mips)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = name, hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp,
                useMipMap = mips, autoGenerateMips = mips, filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
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

        /// <summary>Renders both eyes of the room for the given head pose and screen placement.</summary>
        public void Render(Vector3 hmdPos, Quaternion hmdRot, Vector3 originPos, Quaternion originRot,
                           ScreenOverlays screen)
        {
            if (!ready) return;
            float glow = Mathf.Max(0f, Plugin.Cfg.AmbientGlow.Value);
            float reflection = Mathf.Clamp01(Plugin.Cfg.FloorReflection.Value);

            // Room frame: origin at the recentered head, axes of the recentered yaw.
            var room = Matrix4x4.TRS(originPos, originRot, Vector3.one);
            float floorY = (seatedHeight > 0.3f ? -seatedHeight : -1.2f) - originPos.y;
            if (!loggedFloor) { loggedFloor = true; Plugin.L.LogInfo($"Ambience floor at {floorY:0.00} m below eye level."); }
            float d = Mathf.Max(0.3f, Plugin.Cfg.ScreenDistance.Value);
            var c = new Vector3(0, Plugin.Cfg.ScreenHeightOffset.Value, d);
            float sw = screen.WidthMeters, sh = screen.HeightMeters;
            if (sw <= 0f || sh <= 0f) return;

            if (glow > 0f) EnsureRoomMesh(c, sw, sh, floorY, glow);

            RenderEye(Left, hmdPos + hmdRot * eyePosL, hmdRot * eyeRotL, projL, room, c, sw, sh, floorY, glow, reflection);
            RenderEye(Right, hmdPos + hmdRot * eyePosR, hmdRot * eyeRotR, projR, room, c, sw, sh, floorY, glow, reflection);
        }

        // Wall and floor geometry only changes with the screen size/placement or the glow setting, so it is built
        // once into a mesh and drawn with one call per eye (immediate-mode GL per vertex is costly through interop).
        readonly System.Collections.Generic.List<Vector3> meshVerts = new();
        readonly System.Collections.Generic.List<Vector2> meshUVs = new();
        readonly System.Collections.Generic.List<Color> meshColors = new();
        Mesh roomMesh;
        Vector4 roomMeshKey = new(float.NaN, 0, 0, 0);
        float roomMeshKey2 = float.NaN, roomMeshKey3 = float.NaN;

        void EnsureRoomMesh(Vector3 c, float sw, float sh, float floorY, float glow)
        {
            var key = new Vector4(c.y, c.z, sw, sh);
            if (roomMesh != null && key == roomMeshKey && Mathf.Approximately(floorY, roomMeshKey2) && Mathf.Approximately(glow, roomMeshKey3))
                return;
            roomMeshKey = key; roomMeshKey2 = floorY; roomMeshKey3 = glow;

            meshVerts.Clear(); meshUVs.Clear(); meshColors.Clear();
            DrawWall(c, sw, sh, floorY, glow);
            DrawFloor(c, sw, sh, floorY, glow);

            int n = meshVerts.Count;
            var verts = new Vector3[n]; var uvs = new Vector2[n]; var cols = new Color[n]; var idx = new int[n];
            for (int i = 0; i < n; i++) { verts[i] = meshVerts[i]; uvs[i] = meshUVs[i]; cols[i] = meshColors[i]; idx[i] = i; }

            if (roomMesh == null)
            {
                roomMesh = new Mesh { name = "OlliVR_Room", hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(roomMesh);
            }
            roomMesh.Clear();
            roomMesh.vertices = verts;
            roomMesh.uv = uvs;
            roomMesh.colors = cols;
            roomMesh.SetIndices(idx, MeshTopology.Quads, 0);
            roomMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            roomMesh.UploadMeshData(false);
            Plugin.L.LogInfo($"Ambience room mesh built: {n} vertices.");
        }

        void RenderEye(RenderTexture rt, Vector3 eyePos, Quaternion eyeRot, Matrix4x4 proj, Matrix4x4 room,
                       Vector3 c, float sw, float sh, float floorY, float glow, float reflection)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, BaseColor);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(proj);
            var view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(eyePos, eyeRot, Vector3.one).inverse;
            GL.modelview = view;

            if (glow > 0f && roomMesh != null)
            {
                texMat.mainTexture = glowSmooth;
                texMat.SetPass(0);
                Graphics.DrawMeshNow(roomMesh, room);
            }
            if (reflection > 0f)
            {
                GL.modelview = view * room;
                texMat.mainTexture = mipSource; // blurred: a glossy, not mirror, floor
                texMat.SetPass(0);
                DrawReflection(c, sw, sh, floorY, reflection);
            }

            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        static float Falloff(float dist) => Mathf.Exp(-dist / GlowFalloff);

        /// <summary>The wall behind the screen; the UV maps the screen rectangle to [0,1] and clamps outside.</summary>
        void DrawWall(Vector3 c, float sw, float sh, float floorY, float glow)
        {
            float z = c.z + WallGap;
            float x0 = -sw * 0.5f - GlowReach, x1 = sw * 0.5f + GlowReach;
            float y0 = floorY, y1 = c.y + sh * 0.5f + GlowReach;

            for (int j = 0; j < GridY; j++)
            {
                float ya = Mathf.Lerp(y0, y1, (float)j / GridY), yb = Mathf.Lerp(y0, y1, (float)(j + 1) / GridY);
                for (int i = 0; i < GridX; i++)
                {
                    float xa = Mathf.Lerp(x0, x1, (float)i / GridX), xb = Mathf.Lerp(x0, x1, (float)(i + 1) / GridX);
                    WallVertex(xa, ya, z, c, sw, sh, glow);
                    WallVertex(xa, yb, z, c, sw, sh, glow);
                    WallVertex(xb, yb, z, c, sw, sh, glow);
                    WallVertex(xb, ya, z, c, sw, sh, glow);
                }
            }

        }

        void WallVertex(float x, float y, float z, Vector3 c, float sw, float sh, float glow)
        {
            float dx = Mathf.Max(Mathf.Abs(x - c.x) - sw * 0.5f, 0f);
            float dy = Mathf.Max(Mathf.Abs(y - c.y) - sh * 0.5f, 0f);
            float a = Mathf.Clamp01(0.6f * glow * Falloff(Mathf.Sqrt(dx * dx + dy * dy)));
            meshColors.Add(new Color(1f, 1f, 1f, a));
            meshUVs.Add(new Vector2((x - c.x) / sw + 0.5f, (y - c.y) / sh + 0.5f));
            meshVerts.Add(new Vector3(x, y, z));
        }

        /// <summary>The floor in front of the wall, lit by the screen's bottom edge colours.</summary>
        void DrawFloor(Vector3 c, float sw, float sh, float floorY, float glow)
        {
            float x0 = -sw * 0.5f - GlowReach, x1 = sw * 0.5f + GlowReach;
            float z0 = -1.5f, z1 = c.z + WallGap;
            // The floor is further from the screen than the wall, so it gets less light.
            float drop = c.y - sh * 0.5f - floorY;
            float strength = 0.6f * glow * Falloff(Mathf.Max(0f, drop) * 0.5f);

            for (int j = 0; j < GridY; j++)
            {
                float za = Mathf.Lerp(z0, z1, (float)j / GridY), zb = Mathf.Lerp(z0, z1, (float)(j + 1) / GridY);
                for (int i = 0; i < GridX; i++)
                {
                    float xa = Mathf.Lerp(x0, x1, (float)i / GridX), xb = Mathf.Lerp(x0, x1, (float)(i + 1) / GridX);
                    FloorVertex(xa, za, floorY, c, sw, strength);
                    FloorVertex(xa, zb, floorY, c, sw, strength);
                    FloorVertex(xb, zb, floorY, c, sw, strength);
                    FloorVertex(xb, za, floorY, c, sw, strength);
                }
            }

        }

        void FloorVertex(float x, float z, float y, Vector3 c, float sw, float strength)
        {
            float dx = Mathf.Max(Mathf.Abs(x - c.x) - sw * 0.5f, 0f);
            float dz = Mathf.Max(c.z - z, 0f);
            float a = Mathf.Clamp01(strength * Falloff(Mathf.Sqrt(dx * dx + dz * dz) * 0.8f));
            meshColors.Add(new Color(1f, 1f, 1f, a));
            meshUVs.Add(new Vector2((x - c.x) / sw + 0.5f, 0.04f));
            meshVerts.Add(new Vector3(x, y, z));
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
            // Bottom part, fading from the floor line, then fully faded to the mirrored top.
            GL.Color(near); GL.TexCoord2(0, 0); GL.Vertex3(x0, yb, c.z);
            GL.Color(far); GL.TexCoord2(0, 0.45f); GL.Vertex3(x0, yMid, c.z);
            GL.Color(far); GL.TexCoord2(1, 0.45f); GL.Vertex3(x1, yMid, c.z);
            GL.Color(near); GL.TexCoord2(1, 0); GL.Vertex3(x1, yb, c.z);
            GL.End();
        }
    }
}
