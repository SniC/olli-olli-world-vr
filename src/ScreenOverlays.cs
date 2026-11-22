using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using Valve.VR;

namespace OlliVR
{
    /// <summary>
    /// The virtual screen: a SteamVR overlay in front of you showing a side-by-side stereo texture
    /// (left half to the left eye, right half to the right eye). The game's screen-space UI is rendered into
    /// both halves identically by <see cref="UICamera"/>, so it sits on the screen surface like UI on a 3D TV.
    /// </summary>
    internal class ScreenOverlays
    {
        public RenderTexture StereoTexture { get; private set; }   // 2W x H, side by side
        public RenderTexture EyeLeft { get; private set; }        // W x H
        public RenderTexture EyeRight { get; private set; }       // W x H
        public RenderTexture UITexture { get; private set; }      // W x H, the game's screen-space UI on transparent
        public Camera UICamera { get; private set; }
        public int EyeWidth { get; private set; }
        public int EyeHeight { get; private set; }

        /// <summary>Screen centre and orientation in (seated) tracking space, Unity convention.</summary>
        public Vector3 Center { get; private set; }
        public Quaternion Rotation { get; private set; } = Quaternion.identity;
        public float WidthMeters { get; private set; }
        public float HeightMeters { get; private set; }

        /// <summary>Canvases the mod took over from ScreenSpaceOverlay (or from the game's UI-only camera).</summary>
        readonly List<Canvas> converted = new();
        readonly HashSet<int> convertedIds = new();
        /// <summary>
        /// Layer the converted canvases live on: one the game's pipeline draws after post-processing
        /// (NoComplyRendererAsset.UiPostPostProcessLayer), like the game's own camera-attached UI (map level card).
        /// </summary>
        public int UILayer { get; private set; } = 5;
        Camera boundCamera;
        bool placed;

        public bool Init(float aspect)
        {
            try
            {
                var ov = VRSession.Overlay;
                if (ov == null) { Plugin.L.LogError("IVROverlay unavailable; cannot show the virtual screen."); return false; }

                // Eye images are copied from the backbuffer, so they match the game window's size.
                CreateTextures(Screen.width, Screen.height);

                UILayer = FindPostPostProcessUILayer();
                Plugin.L.LogInfo($"UI layer for converted canvases: {UILayer} ({LayerMask.LayerToName(UILayer)})");

                // Hosts the game's screen-space UI on screens without any game camera (title, loading).
                // Rendered manually (disabled camera).
                var go = new GameObject("OlliVR_UICamera");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                UICamera = go.AddComponent<Camera>();
                UICamera.enabled = false;
                // Orthographic with size = half the image height: converted canvases then get a world scale of 1
                // unit per pixel, exactly as in ScreenSpaceOverlay mode. Some game UI (the "gloop" blob effect)
                // computes positions from canvas world space and assumes that.
                UICamera.orthographic = true;
                PlaceUICameraLikeOverlay();
                UICamera.clearFlags = CameraClearFlags.SolidColor;
                UICamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                // Only the UI layer, and a thin depth slice around the canvas plane: the camera sits where
                // overlay canvases live (near the world origin), so it must not pick up level geometry.
                UICamera.cullingMask = 1 << UILayer;
                UICamera.nearClipPlane = 1f;
                UICamera.farClipPlane = UIPlaneDistance * 2f;
                UICamera.depth = 100;
                UICamera.targetTexture = EyeLeft;
                UICamera.allowHDR = false;
                UICamera.allowMSAA = false;

                CreateBackground();

                bool flip = Plugin.Cfg.FlipVertical.Value;
                var bounds = new VRTextureBounds_t { uMin = 0, uMax = 1, vMin = flip ? 1 : 0, vMax = flip ? 0 : 1 };

                ulong screen = 0;
                var err = ov.CreateOverlay("olliolliworld.vr.screen", "OlliOlli World 3D Screen", ref screen);
                if (err != EVROverlayError.None) { Plugin.L.LogError("CreateOverlay (screen) failed: " + err); return false; }
                ov.SetOverlayFlag(screen, VROverlayFlags.SideBySide_Parallel, true);
                ov.SetOverlayTextureBounds(screen, ref bounds);

                VRSession.ScreenHandle = screen;

                Plugin.L.LogInfo($"Virtual screen created: {EyeWidth}x{EyeHeight} per eye (side by side).");
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogError("Virtual screen init failed: " + e);
                return false;
            }
        }

        /// <summary>
        /// Puts converted canvases exactly where ScreenSpaceOverlay would: canvas centre at
        /// (Screen.width/2, Screen.height/2, 0), one world unit per screen pixel.
        /// </summary>
        public void PlaceUICameraLikeOverlay()
        {
            float sw = Screen.width, sh = Screen.height;
            UICamera.orthographicSize = sh * 0.5f;
            // Canvases sit at plane distance UIPlaneDistance, i.e. at z = 0 like overlay canvases. The deep view
            // volume matters: some UI elements are offset in z (dialogue bubbles sit at z = 26), which overlay mode
            // ignores but a camera clips.
            UICamera.transform.SetPositionAndRotation(new Vector3(sw * 0.5f, sh * 0.5f, -UIPlaneDistance), Quaternion.identity);
        }

        UnityEngine.UI.RawImage background;
        const float UIPlaneDistance = 500f;

        /// <summary>
        /// A full-screen image behind all game UI on the UI camera. Each eye image is shown there, so one UI
        /// camera render produces "eye image + game UI" composited by the game's own UI rendering (a camera render
        /// in the game's pipeline replaces its target, and loses transparency, so the UI can't be rendered on its
        /// own and blended on afterwards).
        /// </summary>
        void CreateBackground()
        {
            var go = new GameObject("OlliVR_UIBackground");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.layer = UILayer;
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = UICamera;
            canvas.planeDistance = UIPlaneDistance * 1.9f; // behind all game UI
            canvas.sortingOrder = -32000;

            var imgGo = new GameObject("Eye");
            imgGo.layer = UILayer;
            imgGo.transform.SetParent(go.transform, false);
            background = imgGo.AddComponent<UnityEngine.UI.RawImage>();
            background.raycastTarget = false;
            var rt = imgGo.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>Renders the game UI over <paramref name="eye"/> (in place).</summary>
        public void CompositeUIOnto(RenderTexture eye)
        {
            background.texture = eye;
            UICamera.targetTexture = UITexture;
            UICamera.Render();
            Graphics.CopyTexture(UITexture, eye);
        }

        void CreateTextures(int w, int h)
        {
            EyeWidth = Mathf.Max(64, w);
            EyeHeight = Mathf.Max(64, h);
            StereoTexture = MakeRT(EyeWidth * 2, EyeHeight, "OlliVR_Stereo");
            EyeLeft = MakeRT(EyeWidth, EyeHeight, "OlliVR_EyeL");
            EyeRight = MakeRT(EyeWidth, EyeHeight, "OlliVR_EyeR");
            UITexture = MakeRT(EyeWidth, EyeHeight, "OlliVR_UI");
            VRSession.SetScreenTexture(StereoTexture.GetNativeTexturePtr());
        }

        /// <summary>Recreates the textures after the game window changed size.</summary>
        public void Resize(int w, int h)
        {
            var old = new[] { StereoTexture, EyeLeft, EyeRight, UITexture };
            CreateTextures(w, h);
            foreach (var t in old) if (t != null) { t.Release(); UnityEngine.Object.Destroy(t); }
            Plugin.L.LogInfo($"Screen textures resized to {EyeWidth}x{EyeHeight} per eye.");
        }

        static RenderTexture MakeRT(int w, int h, string name)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            UnityEngine.Object.DontDestroyOnLoad(rt);
            return rt;
        }

        /// <summary>
        /// Places the screen straight ahead of the recentered head pose. Its size is chosen so that, from the
        /// nominal viewing position, it covers the game camera's vertical field of view (times ScreenScale).
        /// </summary>
        public void Place(Vector3 originPos, Quaternion originRot, float gameFovDeg, float aspect)
        {
            float d = Mathf.Max(0.3f, Plugin.Cfg.ScreenDistance.Value);
            float h = 2f * d * Mathf.Tan(gameFovDeg * 0.5f * Mathf.Deg2Rad) * Mathf.Max(0.1f, Plugin.Cfg.ScreenScale.Value);
            float w = h * aspect;
            var center = originPos + originRot * new Vector3(0, Plugin.Cfg.ScreenHeightOffset.Value, d);

            bool sizeChanged = Mathf.Abs(w - WidthMeters) > 0.005f;
            bool poseChanged = !placed || (center - Center).sqrMagnitude > 1e-8f || Quaternion.Angle(originRot, Rotation) > 0.01f;
            if (!sizeChanged && !poseChanged) return;

            Center = center; Rotation = originRot; WidthMeters = w; HeightMeters = h;
            var ov = VRSession.Overlay;
            if (sizeChanged) ov.SetOverlayWidthInMeters(VRSession.ScreenHandle, w);
            var m = VRSession.ToOpenVR(center, originRot);
            var e1 = ov.SetOverlayTransformAbsolute(VRSession.ScreenHandle, VRSession.Universe, ref m);
            if (!placed)
            {
                var e2 = ov.ShowOverlay(VRSession.ScreenHandle);
                placed = true;
                Plugin.L.LogInfo($"Screen shown: {w:0.00} x {h:0.00} m at {center.ToString("F2")} " +
                                 $"(transform {e1}, show {e2}, visible {ov.IsOverlayVisible(VRSession.ScreenHandle)})");
            }
            if (Plugin.Cfg.VerboseLogging.Value)
                Plugin.L.LogInfo($"Screen placed: {w:0.00} x {h:0.00} m at {d:0.0} m (game fov {gameFovDeg:0.0})");
        }

        /// <summary>Finds new screen-space canvases to take over. Called about once per second.</summary>
        public void ScanCanvases()
        {
            try
            {
                PlaceUICameraLikeOverlay(); // follows window resizes
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>()))
                {
                    var c = o.Cast<Canvas>();
                    if (c == null || !c.isRootCanvas) continue;
                    int id = c.GetInstanceID();
                    if (convertedIds.Contains(id) || !ShouldConvert(c)) continue;

                    convertedIds.Add(id);
                    converted.Add(c);
                    c.renderMode = RenderMode.ScreenSpaceCamera;
                    Bind(c, boundCamera != null ? boundCamera : UICamera, boundDistance);
                    Plugin.L.LogInfo($"UI canvas '{c.name}' -> drawn on the screen (order {c.sortingOrder})");
                }

                // Canvases are culled by their own layer (nested canvases too): keep every canvas of the
                // converted hierarchies on the UI layer (the game spawns UI at runtime).
                for (int i = converted.Count - 1; i >= 0; i--)
                {
                    var root = converted[i];
                    if (root == null) { converted.RemoveAt(i); continue; }
                    foreach (var c in root.GetComponentsInChildren<Canvas>(true))
                        if (c != null && c.gameObject.layer != UILayer) c.gameObject.layer = UILayer;
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Canvas scan error: " + e.Message);
            }
        }

        float boundDistance = UIPlaneDistance;

        /// <summary>
        /// Attaches the converted UI to <paramref name="host"/> (the game's top scene camera) at
        /// <paramref name="distance"/>, the convergence distance, so the UI is drawn inside each eye render and
        /// sits on the screen surface. With no host (title, loading screens) the UI goes to the mod's own
        /// orthographic UI camera, which places canvases exactly like overlay mode.
        /// </summary>
        public void BindUI(Camera host, float distance)
        {
            // The game positions some UI (dialogue bubbles over the speaker, the gloop effect) in screen pixels,
            // which only works with overlay-style layout. So the UI always stays on the mod's orthographic UI
            // camera and is composited onto the eye images separately.
            host = null;
            var target = host != null ? host : UICamera;
            float dist;
            if (host == null) dist = UIPlaneDistance;
            else if (host.orthographic) dist = Mathf.Lerp(host.nearClipPlane, host.farClipPlane, 0.02f);
            else dist = Mathf.Clamp(distance, host.nearClipPlane * 1.5f, host.farClipPlane * 0.9f);

            if (host != null && (host.cullingMask & (1 << UILayer)) == 0)
                host.cullingMask |= 1 << UILayer;

            bool targetChanged = target != boundCamera;
            bool distChanged = Mathf.Abs(dist - boundDistance) > 0.01f;
            boundCamera = target;
            boundDistance = dist;
            if (!targetChanged && !distChanged) return;

            for (int i = converted.Count - 1; i >= 0; i--)
            {
                var c = converted[i];
                if (c == null) { converted.RemoveAt(i); continue; }
                Bind(c, target, dist);
            }
            if (targetChanged)
                Plugin.L.LogInfo($"UI attached to '{target.name}'" + (host != null ? $" at distance {dist:0.0}" : ""));
        }

        static void Bind(Canvas c, Camera cam, float dist)
        {
            if (c.renderMode != RenderMode.ScreenSpaceCamera) c.renderMode = RenderMode.ScreenSpaceCamera;
            if (c.worldCamera != cam) c.worldCamera = cam;
            c.planeDistance = dist;
        }

        /// <summary>
        /// Overlay canvases are taken over. Screen-space-camera canvases only if they were drawn by a UI-only
        /// camera (like the game's own "UI Camera"). Canvases the game attached to a 3D scene camera (the map
        /// level card, the front end's backdrops) already render in that camera.
        /// </summary>
        bool ShouldConvert(Canvas c)
        {
            if (c.renderMode == RenderMode.ScreenSpaceOverlay) return true;
            if (c.renderMode != RenderMode.ScreenSpaceCamera) return false;
            var wc = c.worldCamera;
            if (wc == null) return true;
            if (wc == UICamera) return false;
            return (wc.cullingMask & ~(1 << 5)) == 0;
        }

        /// <summary>The first layer in the render pipeline's "UI after post-processing" mask, or 5 (UI).</summary>
        static int FindPostPostProcessUILayer()
        {
            try
            {
                var asset = UnityEngine.Rendering.GraphicsSettings.renderPipelineAsset;
                var nc = asset != null ? asset.TryCast<Roll7.NoComplyRendererAsset>() : null;
                if (nc != null)
                {
                    int mask = nc.UiPostPostProcessLayer.value;
                    Plugin.L.LogInfo($"Render pipeline UI masks: uiLayer=0x{nc.UiLayer.value:X} uiPostPostProcess=0x{mask:X}");
                    for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) return i;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Could not read render pipeline UI layers: " + e.Message); }
            return 5;
        }
    }
}
