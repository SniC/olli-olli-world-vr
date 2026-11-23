using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;
using Valve.VR;

namespace OlliVR
{
    /// <summary>
    /// Stereoscopic virtual screen with head-coupled ("fish tank") perspective.
    ///
    /// Each frame, after the game has rendered its own cameras, every camera it rendered to the monitor is
    /// rendered again twice into a side-by-side texture shown on a floating SteamVR overlay screen. Each half
    /// uses an off-axis projection from that eye's real position through the screen rectangle, so the screen
    /// behaves like a window into the game world: correct stereo depth, plus parallax when you move your head.
    /// From the nominal viewing position the image matches the game's own framing exactly.
    /// </summary>
    public class VRRig : MonoBehaviour
    {
        public VRRig(IntPtr ptr) : base(ptr) { }

        ScreenOverlays screen;
        Ambience ambience;
        long ambienceTicks, ambienceMaxTicks, vrTicks, vrMaxTicks; int ambienceFrames; float nextAmbienceLog, maxDt;
        int gc0, gc1, gc2;
        bool initialized, failed, captureRequested;
        float nextInitAttempt;
        const int UILayerMask = 1 << 5;

        // Recenter origin in tracking space (yaw only + position).
        Vector3 originPos;
        Quaternion originRot = Quaternion.identity;
        bool haveOrigin;
        float stickHoldTime;

        Vector3 eyeOffsetL, eyeOffsetR;
        float lastFov = 33.4f;
        float convergence;          // smoothed, game units
        float nextSceneFixup, nextDump, nextConvergenceLog;
        readonly List<Camera> screenCams = new();
        readonly List<Camera> gameUICams = new();
        readonly HashSet<string> loggedCams = new();
        string[] disabledEffects = Array.Empty<string>();

        void Start()
        {
            disabledEffects = Plugin.Cfg.DisabledEffects.Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
            Application.add_quitting((Action)VRSession.Shutdown);
            Camera.onPreCull = Il2CppSystem.Delegate.Combine(Camera.onPreCull, (Camera.CameraCallback)OnAnyPreCull).Cast<Camera.CameraCallback>();
            this.StartCoroutine(EndOfFrameLoop());
        }

        void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            Plugin.L.LogInfo($"Scene loaded: {s.name}");
            loggedCams.Clear();
            nextSceneFixup = 0; // re-apply fixups right away
        }

        bool EnsureInit()
        {
            if (initialized) return true;
            // Retry so the headset can be connected after the game starts.
            if (Time.unscaledTime < nextInitAttempt) return false;
            nextInitAttempt = Time.unscaledTime + 5f;
            if (!VRSession.Init(logFailure: !failed)) { failed = true; return false; }
            if (failed) Plugin.L.LogInfo("OpenVR connected after retry.");
            VRSession.TraceBudget = 0;

            // The scene layer is the room around the screen, lit by the screen's colours.
            ambience = new Ambience();
            ambience.Init();
            VRSession.SetEyeTextures(ambience.Left.GetNativeTexturePtr(), ambience.Right.GetNativeTexturePtr(), Plugin.Cfg.FlipVertical.Value);

            eyeOffsetL = VRSession.GetEyeOffset(EVREye.Eye_Left);
            eyeOffsetR = VRSession.GetEyeOffset(EVREye.Eye_Right);
            Plugin.L.LogInfo($"IPD {(eyeOffsetR - eyeOffsetL).magnitude * 1000f:0.0} mm");

            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
            screen = new ScreenOverlays();
            if (!screen.Init(aspect)) { failed = true; VRSession.Shutdown(); return false; }

            initialized = true;
            return true;
        }

        void Update()
        {
            if (!EnsureInit()) return;

            VRSession.PollEvents();
            if (VRSession.RecenterRequested) { VRSession.RecenterRequested = false; haveOrigin = false; }
            HandleRecenterInput();

            if (Time.unscaledTime >= nextSceneFixup)
            {
                nextSceneFixup = Time.unscaledTime + 1f;
                if (Plugin.Cfg.VerboseLogging.Value && Time.unscaledTime >= nextDump) { nextDump = Time.unscaledTime + 15f; DumpCameras(); LogPreCullStats(); }
                var trigger = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "OlliVR_capture.trigger");
                if (System.IO.File.Exists(trigger)) { System.IO.File.Delete(trigger); captureRequested = true; }
                ApplySceneFixups();
                screen.ScanCanvases();
            }

        }

        void HandleRecenterInput()
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null && Enum.TryParse<Key>(Plugin.Cfg.RecenterKey.Value, true, out var key))
                {
                    if (kb[key].wasPressedThisFrame) haveOrigin = false;
                }
                var gp = Gamepad.current;
                if (gp != null && gp.leftStickButton.isPressed && gp.rightStickButton.isPressed)
                {
                    stickHoldTime += Time.unscaledDeltaTime;
                    if (stickHoldTime > 1f) { haveOrigin = false; stickHoldTime = float.MinValue; }
                }
                else stickHoldTime = 0;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Recenter input error: " + e.Message);
            }
        }

        /// <summary>Settings the game may reset on scene changes, re-applied every second.</summary>
        void ApplySceneFixups()
        {
            if (Plugin.Cfg.UncapFramerate.Value)
            {
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
                if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
            }

            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<PostProcessLayer>()))
                {
                    var layer = o.Cast<PostProcessLayer>();
                    if (Plugin.Cfg.ReplaceTAA.Value && layer.antialiasingMode == PostProcessLayer.Antialiasing.TemporalAntialiasing)
                    {
                        layer.antialiasingMode = PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
                        Plugin.L.LogInfo($"Replaced TAA with FXAA on {layer.name}");
                    }
                }

                if (disabledEffects.Length > 0)
                {
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<PostProcessVolume>()))
                    {
                        var vol = o.Cast<PostProcessVolume>();
                        var prof = vol.sharedProfile;
                        if (prof == null) continue;
                        var settings = prof.settings;
                        for (int i = 0; i < settings.Count; i++)
                        {
                            var s = settings[i];
                            if (s == null) continue;
                            var typeName = s.GetIl2CppType().Name;
                            if (Array.IndexOf(disabledEffects, typeName) < 0) continue;
                            if (s.enabled.value || s.active)
                            {
                                s.enabled.value = false;
                                s.enabled.overrideState = true;
                                s.active = false;
                                Plugin.L.LogInfo($"Disabled post effect {typeName} on volume {vol.name}");
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Post-processing fixup error: " + e.Message);
            }
        }

        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        IEnumerator EndOfFrameLoop()
        {
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return eof;
                if (!initialized) continue;
                long vr0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { RenderAndSubmit(); }
                catch (Exception e) { Plugin.L.LogError("VR frame error: " + e); }
                long vrDt = System.Diagnostics.Stopwatch.GetTimestamp() - vr0;
                vrTicks += vrDt;
                if (vrDt > vrMaxTicks) vrMaxTicks = vrDt;
            }
        }

        // ---- Per-frame ------------------------------------------------------------------------------

        /// <summary>
        /// Runs at end of frame, after the game rendered its own frame (Cinemachine has placed the cameras).
        ///
        /// Important property of the game's render pipeline (Roll7.NoComplyRenderer): a camera render does not
        /// draw on top of what is in its target, it renders into internal textures and then replaces the whole
        /// target. So nothing may be rendered "on top" of an eye image afterwards. The game's screen-space UI is
        /// therefore attached to the game's main camera (see ScreenOverlays.ScanCanvases) and drawn as part of
        /// each eye render, at the convergence distance so it sits on the screen surface.
        /// </summary>
        void RenderAndSubmit()
        {
            if (!VRSession.GetPredictedHmdPose(out var hmdPos, out var hmdRot)) return;

            if (!haveOrigin)
            {
                // Yaw-only origin so the screen stands upright.
                var fwd = hmdRot * Vector3.forward;
                fwd.y = 0;
                originRot = fwd.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fwd.normalized, Vector3.up) : Quaternion.identity;
                originPos = hmdPos;
                haveOrigin = true;
                Plugin.L.LogInfo("Screen recentered.");
            }

            CollectCameras();
            if (screen.EyeLeft.width != Screen.width || screen.EyeLeft.height != Screen.height)
                screen.Resize(Screen.width, Screen.height);

            // Screen size follows the game's (perspective) field of view so the default view matches the game.
            Camera perspCam = null;
            for (int i = screenCams.Count - 1; i >= 0; i--)
                if (!screenCams[i].orthographic) { perspCam = screenCams[i]; break; }
            if (perspCam != null)
            {
                // The game zooms and shifts with its own projection matrix, so use the field of view it actually
                // shows (not Camera.fieldOfView). Captured before any eye render this frame.
                var gp = OriginalProjection(perspCam).Matrix;
                lastFov = gp.m11 > 0f ? 2f * Mathf.Atan(1f / gp.m11) * Mathf.Rad2Deg : perspCam.fieldOfView;
            }
            float aspect = (float)screen.EyeWidth / screen.EyeHeight;
            screen.Place(originPos, originRot, lastFov, aspect);

            // The camera that carries the game's screen-space UI: the top-most scene camera.
            Camera uiHost = screenCams.Count > 0 ? screenCams[screenCams.Count - 1] : null;
            if (perspCam != null) UpdateConvergence(perspCam);
            screen.BindUI(uiHost, convergence > 0f ? convergence : 15f);

            insideVRRender = true;
            try
            {
                if (screenCams.Count > 0)
                {
                    // Eye positions in screen space (metres; x right, y up, z towards the screen).
                    var eyeL = EyeInScreenSpace(hmdPos, hmdRot, eyeOffsetL);
                    var eyeR = EyeInScreenSpace(hmdPos, hmdRot, eyeOffsetR);
                    RenderEyeImage(screen.EyeLeft, eyeL);
                    RenderEyeImage(screen.EyeRight, eyeR);
                }
                else
                {
                    // UI-only screens (title, loading): black behind the UI.
                    var prevActive = RenderTexture.active;
                    RenderTexture.active = screen.EyeLeft; GL.Clear(true, true, Color.black);
                    RenderTexture.active = screen.EyeRight; GL.Clear(true, true, Color.black);
                    RenderTexture.active = prevActive;
                }

                // The game's screen-space UI over each eye image (identical in both, so it sits on the screen
                // surface). The eye image becomes the UI camera's background; see ScreenOverlays.CompositeUIOnto.
                ForceOpaque(screen.EyeLeft);
                ForceOpaque(screen.EyeRight);
                screen.CompositeUIOnto(screen.EyeLeft);
                screen.CompositeUIOnto(screen.EyeRight);
            }
            finally { insideVRRender = false; }

            int ew = screen.EyeWidth, eh = screen.EyeHeight;
            Graphics.CopyTexture(screen.EyeLeft, 0, 0, 0, 0, ew, eh, screen.StereoTexture, 0, 0, 0, 0);
            Graphics.CopyTexture(screen.EyeRight, 0, 0, 0, 0, ew, eh, screen.StereoTexture, 0, 0, ew, 0);
            ForceOpaque(screen.StereoTexture);
            GL.IssuePluginEvent(VRSession.RenderEventFunc, VRSession.EventSubmitScreen);

            // The room around the screen, lit by this frame's image.
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ambience.UpdateGlow(screen.EyeLeft);
            ambience.Render(hmdPos, hmdRot, originPos, originRot, screen);
            long dtA = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            ambienceTicks += dtA;
            if (dtA > ambienceMaxTicks) ambienceMaxTicks = dtA;
            if (Time.unscaledDeltaTime > maxDt) maxDt = Time.unscaledDeltaTime;
            ambienceFrames++;
            if (Time.unscaledTime >= nextAmbienceLog)
            {
                nextAmbienceLog = Time.unscaledTime + 10f;
                double ms = ambienceTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, ambienceFrames);
                double tk = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                Plugin.L.LogInfo($"Ambience CPU: {ms:0.000} ms/frame (max {ambienceMaxTicks * tk:0.0}) over {ambienceFrames} frames; VR frame {vrTicks * tk / Math.Max(1, ambienceFrames):0.00} ms (max {vrMaxTicks * tk:0.0}); " +
                                 $"worst frame interval {maxDt * 1000f:0.0} ms; GC gen0/1/2 +{g0 - gc0}/+{g1 - gc1}/+{g2 - gc2}");
                gc0 = g0; gc1 = g1; gc2 = g2; ambienceMaxTicks = 0; vrTicks = 0; vrMaxTicks = 0; maxDt = 0;
                ambienceTicks = 0; ambienceFrames = 0;
            }

            if (captureRequested) { captureRequested = false; Capture(hmdPos, hmdRot); }

            // The room eye frames are SteamVR's scene layer (and drive frame pacing) behind the screen.
            GL.IssuePluginEvent(VRSession.RenderEventFunc, VRSession.PrepareSubmit());
        }

        /// <summary>
        /// One eye: the game's scene cameras in depth order, then its world-space UI camera(s), the same sequence
        /// the game renders to the monitor (each later camera takes over from the earlier one the way the game
        /// relies on).
        /// </summary>
        void RenderEyeImage(RenderTexture rt, Vector3 eye)
        {
            for (int i = 0; i < screenCams.Count; i++)
                RenderEye(screenCams[i], rt, eye, OriginalProjection(screenCams[i]));
            for (int i = 0; i < gameUICams.Count; i++)
                RenderOnTop(gameUICams[i], rt);
        }

        /// <summary>The game's projection for a camera this frame (captured before any eye render).</summary>
        struct GameProjection { public Matrix4x4 Matrix; public int Frame; }
        readonly Dictionary<IntPtr, GameProjection> gameProjections = new();

        /// <summary>
        /// Unity's automatic projection for the camera (includes any physical-camera lens shift Cinemachine
        /// applies). The game never sets a custom projection matrix, so it is always reset, never written back:
        /// writing a matrix back would freeze the camera on it.
        /// </summary>
        GameProjection OriginalProjection(Camera cam)
        {
            if (gameProjections.TryGetValue(cam.Pointer, out var p) && p.Frame == Time.frameCount) return p;
            cam.ResetProjectionMatrix();
            p = new GameProjection { Matrix = cam.projectionMatrix, Frame = Time.frameCount };
            gameProjections[cam.Pointer] = p;
            return p;
        }

        /// <summary>Renders a game camera into an eye texture from <paramref name="eye"/>, then restores it.</summary>
        void RenderEye(Camera cam, RenderTexture rt, Vector3 eye, GameProjection original)
        {
            var tr = cam.transform;
            var savedPos = tr.position;
            var savedRot = tr.rotation;
            var savedTarget = cam.targetTexture;
            try
            {
                cam.targetTexture = rt;
                if (!cam.orthographic)
                {
                    EyeView(cam, original.Matrix, eye, out var pos, out var proj);
                    // The eye viewpoint goes in through the view matrix only; the camera's transform stays where
                    // the game put it. The game's shadow code fits its shadow maps to the camera transform, so
                    // moving the transform per eye (and with every head movement) made shadows shimmer and differ
                    // between the eyes.
                    cam.worldToCameraMatrix = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) *
                                              Matrix4x4.TRS(pos, savedRot, Vector3.one).inverse;
                    cam.projectionMatrix = proj;
                }
                // Orthographic views (world map) have no depth: rendered unchanged in both eyes.
                cam.Render();
            }
            finally
            {
                cam.targetTexture = savedTarget;
                cam.ResetWorldToCameraMatrix();
                cam.ResetProjectionMatrix();
                tr.SetPositionAndRotation(savedPos, savedRot);
            }
        }

        /// <summary>
        /// Off-axis view of <paramref name="cam"/>'s scene from <paramref name="eye"/> (screen space, metres)
        /// through the screen rectangle. The screen maps to the game's image plane at the convergence distance,
        /// so objects at that distance sit on the screen surface.
        /// </summary>
        void EyeView(Camera cam, Matrix4x4 gp, Vector3 eye, out Vector3 pos, out Matrix4x4 proj)
        {
            var tr = cam.transform;
            float w = screen.WidthMeters, h = screen.HeightMeters;
            float ez = Mathf.Max(0.05f, -eye.z);
            float d = convergence > 0f ? convergence : 15f;

            // The game's camera uses an off-centre (lens-shifted) frustum, so the skater sits left of centre.
            // Read the window the game actually shows from its projection: centre (cx, cy) and half-height
            // as view-space tangents.
            float halfH = gp.m11 != 0f ? 1f / gp.m11 : Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float cx = gp.m00 != 0f ? gp.m02 / gp.m00 : 0f;
            float cy = gp.m11 != 0f ? gp.m12 / gp.m11 : 0f;
            // Cinemachine frames the skater off-centre with Camera.lensShift. The game renders with it even
            // though the camera isn't in physical mode, but Camera.projectionMatrix doesn't include it then,
            // so apply it here: the shift is a fraction of the frame width/height.
            if (!cam.usePhysicalProperties)
            {
                var ls = cam.lensShift;
                float halfW = gp.m00 != 0f ? 1f / gp.m00 : halfH * cam.aspect;
                cx += ls.x * 2f * halfW;
                cy += ls.y * 2f * halfH;
            }

            // Game units per real metre: the screen height covers the game frame height at distance d.
            float s = 2f * d * halfH / h;
            var screenCenter = tr.position + tr.rotation * new Vector3(cx * d, cy * d, d);
            pos = screenCenter + tr.rotation * new Vector3(eye.x * s, eye.y * s, -ez * s);
            proj = VRSession.Frustum(
                (-w * 0.5f - eye.x) / ez, (w * 0.5f - eye.x) / ez,
                (-h * 0.5f - eye.y) / ez, (h * 0.5f - eye.y) / ez, cam.nearClipPlane, cam.farClipPlane);
        }

        Material opaqueMat;
        bool opaqueMatFailed;

        /// <summary>
        /// The game's cameras leave alpha at 0, and SteamVR overlays treat alpha as transparency, so the screen
        /// would be invisible. Draws a full-texture quad that adds 1 to alpha and leaves RGB untouched
        /// (additive blend of black with alpha 1; UNORM alpha saturates at 1).
        /// </summary>
        void ForceOpaque(RenderTexture rt)
        {
            if (opaqueMat == null && !opaqueMatFailed)
            {
                var sh = Shader.Find("Hidden/Internal-Colored");
                if (sh == null) { opaqueMatFailed = true; Plugin.L.LogError("Shader Hidden/Internal-Colored not found; screen may be transparent."); return; }
                opaqueMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                opaqueMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                opaqueMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
                opaqueMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                opaqueMat.SetInt("_ZWrite", 0);
                opaqueMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                UnityEngine.Object.DontDestroyOnLoad(opaqueMat);
            }
            if (opaqueMat == null) return;

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.PushMatrix();
            GL.LoadOrtho();
            opaqueMat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(new Color(0f, 0f, 0f, 1f));
            GL.Vertex3(0f, 0f, 0f);
            GL.Vertex3(0f, 1f, 0f);
            GL.Vertex3(1f, 1f, 0f);
            GL.Vertex3(1f, 0f, 0f);
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>
        /// Position of an eye relative to the screen centre, in the screen's frame. Head movement away from the
        /// recentered position is scaled by HeadCoupled; eye separation by DepthStrength.
        /// </summary>
        Vector3 EyeInScreenSpace(Vector3 hmdPos, Quaternion hmdRot, Vector3 eyeOffset)
        {
            var inv = Quaternion.Inverse(screen.Rotation);
            var nominalHead = inv * (originPos - screen.Center);
            var head = inv * (hmdPos - screen.Center);
            float coupled = Mathf.Clamp01(Plugin.Cfg.HeadCoupled.Value);
            head = nominalHead + (head - nominalHead) * coupled;

            // With head coupling the eyes follow head rotation; without it they stay level and square to the screen.
            var tracked = inv * (hmdRot * eyeOffset);
            var level = new Vector3(eyeOffset.x, 0f, 0f);
            var offset = Vector3.Lerp(level, tracked, coupled) * Mathf.Max(0f, Plugin.Cfg.DepthStrength.Value);
            return head + offset;
        }


        // ---- Convergence (what sits on the screen surface) ---------------------------------------

        void UpdateConvergence(Camera cam)
        {
            float d = Plugin.Cfg.ConvergenceDistance.Value;
            float skaterDist = 0, rayDist = 0;
            if (d <= 0f)
            {
                // Put the skater on the screen surface: stable, and the thing you look at. The raycast down the
                // view centre (which jumps between foreground and background) is only a fallback for scenes
                // without the skater.
                skaterDist = GetSkaterDistance(cam);
                if (skaterDist > cam.nearClipPlane) d = skaterDist;
                else
                {
                    rayDist = GetRaycastFocusDistance(cam);
                    d = rayDist > 0f ? rayDist : (convergence > 0 ? convergence : 15f);
                }
                // Smooth so cuts don't make the depth pump.
                convergence = convergence <= 0 ? d : Mathf.Lerp(convergence, d, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 1f));
            }
            else convergence = d;

            if (Plugin.Cfg.VerboseLogging.Value && Time.unscaledTime >= nextConvergenceLog)
            {
                nextConvergenceLog = Time.unscaledTime + 10f;
                Plugin.L.LogInfo($"Convergence: skater {skaterDist:0.0}, raycast {rayDist:0.0} -> {convergence:0.0} (game fov {cam.fieldOfView:0.0})");
            }
        }

        Transform skater;
        float nextSkaterSearch;

        /// <summary>Depth of the player's skater in front of <paramref name="cam"/>, or 0 if not found.</summary>
        float GetSkaterDistance(Camera cam)
        {
            try
            {
                if (skater == null && Time.unscaledTime >= nextSkaterSearch)
                {
                    nextSkaterSearch = Time.unscaledTime + 2f;
                    var root = GameObject.Find("Local Player Controller");
                    var s = root != null ? root.transform.Find("Skater - Local") : null;
                    skater = s != null ? s : (root != null ? root.transform : null);
                }
                if (skater == null || !skater.gameObject.activeInHierarchy) return 0f;
                var t = cam.transform;
                float dist = Vector3.Dot(skater.position - t.position, t.forward);
                // Only meaningful when the skater is actually in view.
                var vp = cam.WorldToViewportPoint(skater.position);
                if (vp.z <= 0f || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) return 0f;
                return dist;
            }
            catch { skater = null; return 0f; }
        }

        static float GetRaycastFocusDistance(Camera cam)
        {
            try
            {
                var t = cam.transform;
                float sum = 0; int hits = 0;
                for (int i = -1; i <= 1; i++)
                {
                    var dir = cam.ViewportPointToRay(new Vector3(0.5f + i * 0.15f, 0.45f, 0f)).direction;
                    if (Physics.Raycast(t.position, dir, out var hit, cam.farClipPlane))
                    {
                        sum += Vector3.Dot(hit.point - t.position, t.forward);
                        hits++;
                    }
                }
                return hits > 0 ? sum / hits : 0f;
            }
            catch { return 0f; }
        }

        static float GetCinemachineFocusDistance(Camera cam)
        {
            try
            {
                var brain = cam.GetComponent<Cinemachine.CinemachineBrain>();
                if (brain == null) return 0f;
                var vcam = brain.ActiveVirtualCamera;
                if (vcam == null) return 0f;
                var target = vcam.Follow ?? vcam.LookAt;
                if (target == null) return 0f;
                var t = cam.transform;
                return Vector3.Dot(target.position - t.position, t.forward);
            }
            catch { return 0f; }
        }

        // ---- Camera discovery ----------------------------------------------------------------------

        // Cameras the game rendered to the screen this frame. The game renders cameras manually
        // (they stay disabled), so Camera.allCameras is empty; onPreCull fires for manual renders too.
        readonly List<Camera> renderedThisFrame = new();
        int renderedFrame = -1;
        bool insideVRRender { get => InsideVRRenderStatic; set => InsideVRRenderStatic = value; }
        internal static bool InsideVRRenderStatic;
        readonly Dictionary<string, int> preCullCounts = new();

        void OnAnyPreCull(Camera c)
        {
            if (insideVRRender || c == null) return;
            if (renderedFrame != Time.frameCount) { renderedFrame = Time.frameCount; renderedThisFrame.Clear(); }
            if (Plugin.Cfg.VerboseLogging.Value)
            {
                var key = c.name + (c.targetTexture == null ? " -> screen" : " -> " + c.targetTexture.name);
                preCullCounts[key] = preCullCounts.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            if (c.targetTexture == null && !renderedThisFrame.Contains(c)) renderedThisFrame.Add(c);
        }

        void CollectCameras()
        {
            screenCams.Clear();
            gameUICams.Clear();
            // Called at end of frame: the cameras the game rendered this frame.
            if (renderedFrame != Time.frameCount) return;
            for (int i = 0; i < renderedThisFrame.Count; i++)
            {
                var c = renderedThisFrame[i];
                if (c == null || c.targetTexture != null) continue;
                if (c == screen.UICamera) continue;
                if ((c.cullingMask & ~UILayerMask) == 0)
                {
                    // The game's own UI-only camera: world-space UI such as map markers, the map cursor and
                    // level signs. Drawn unchanged on top of the scene in both eyes.
                    gameUICams.Add(c);
                    continue;
                }
                screenCams.Add(c);

                if (loggedCams.Add(c.name))
                    Plugin.L.LogInfo($"Screen camera '{c.name}': depth={c.depth} ortho={c.orthographic} fov={c.fieldOfView:0.#} " +
                                     $"near={c.nearClipPlane} far={c.farClipPlane} mask=0x{c.cullingMask:X} clear={c.clearFlags}");
            }
            screenCams.Sort((a, b) => a.depth.CompareTo(b.depth));
            gameUICams.Sort((a, b) => a.depth.CompareTo(b.depth));
        }

        /// <summary>Renders a camera unchanged on top of what is already in <paramref name="rt"/>.</summary>
        static void RenderOnTop(Camera cam, RenderTexture rt)
        {
            var savedTarget = cam.targetTexture;
            var savedClear = cam.clearFlags;
            try
            {
                cam.targetTexture = rt;
                cam.clearFlags = CameraClearFlags.Depth;
                cam.Render();
            }
            finally
            {
                cam.targetTexture = savedTarget;
                cam.clearFlags = savedClear;
            }
        }

        // ---- Diagnostics -----------------------------------------------------------------------------

        void Capture(Vector3 hmdPos, Quaternion hmdRot)
        {
            try
            {
                var dir = BepInEx.Paths.BepInExRootPath;
                SaveRT(screen.StereoTexture, System.IO.Path.Combine(dir, "OlliVR_capture_Stereo.png"));
                SaveRT(ambience.Left, System.IO.Path.Combine(dir, "OlliVR_capture_Room.png"));
                ambience.CaptureViews(dir, SaveRT, screen.StereoTexture);
                {
                    var prev = RenderTexture.active;
                    RenderTexture.active = screen.StereoTexture;
                    var px = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    px.ReadPixels(new Rect(screen.EyeWidth / 2, screen.EyeHeight / 2, 1, 1), 0, 0);
                    px.Apply();
                    RenderTexture.active = prev;
                    Plugin.L.LogInfo($"Stereo texture centre pixel: {px.GetPixel(0, 0)}");
                    UnityEngine.Object.Destroy(px);

                    SaveRT(screen.UITexture, System.IO.Path.Combine(dir, "OlliVR_capture_UI.png"));
                    var ui = screen.UITexture;
                    RenderTexture.active = ui;
                    var full = new Texture2D(ui.width, ui.height, TextureFormat.RGBA32, false);
                    full.ReadPixels(new Rect(0, 0, ui.width, ui.height), 0, 0);
                    full.Apply();
                    RenderTexture.active = prev;
                    float sumA = 0; int opaque = 0, n = 0;
                    for (int y = 0; y < ui.height; y += 40)
                        for (int x = 0; x < ui.width; x += 40)
                        {
                            float a = full.GetPixel(x, y).a;
                            sumA += a; n++;
                            if (a > 0.9f) opaque++;
                        }
                    Plugin.L.LogInfo($"UI texture alpha: mean {sumA / n:0.000}, {100f * opaque / n:0.0}% of samples opaque");
                    UnityEngine.Object.Destroy(full);
                }
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Capture: hmdPos={hmdPos.ToString("F3")} hmdRot={hmdRot.eulerAngles.ToString("F1")} " +
                              $"origin={originPos.ToString("F3")} screen {screen.WidthMeters:0.00}x{screen.HeightMeters:0.00} m " +
                              $"convergence={convergence:0.0} eyeL={EyeInScreenSpace(hmdPos, hmdRot, eyeOffsetL).ToString("F3")} " +
                              $"eyeR={EyeInScreenSpace(hmdPos, hmdRot, eyeOffsetR).ToString("F3")}");
                if (Plugin.Cfg.VerboseLogging.Value) DumpUI(sb);
                foreach (var c in screenCams)
                    sb.AppendLine($"  cam '{c.name}' pos={c.transform.position.ToString("F2")} rot={c.transform.rotation.eulerAngles.ToString("F1")} " +
                                  $"fov={c.fieldOfView} ortho={c.orthographic} physical={c.usePhysicalProperties} lensShift={c.lensShift} " +
                                  $"proj m00={c.projectionMatrix.m00:0.###} m02={c.projectionMatrix.m02:0.###} m11={c.projectionMatrix.m11:0.###} m12={c.projectionMatrix.m12:0.###}");
                Plugin.L.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.L.LogError("Capture failed: " + e); }
        }

        /// <summary>Lists active UI graphics and any renderers living under canvases (diagnostics).</summary>
        static void DumpUI(System.Text.StringBuilder sb)
        {
            try
            {
                sb.AppendLine("  --- UI graphics ---");
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UnityEngine.UI.Graphic>()))
                {
                    var g = o.Cast<UnityEngine.UI.Graphic>();
                    if (g == null || !g.isActiveAndEnabled) continue;
                    var mat = g.materialForRendering;
                    var canvas = g.canvas;
                    sb.AppendLine($"    {g.GetIl2CppType().Name} '{PathOf(g.transform)}' layer={g.gameObject.layer} color={g.color} " +
                                  $"shader={(mat != null && mat.shader != null ? mat.shader.name : "null")} " +
                                  $"canvas={(canvas != null ? canvas.rootCanvas.name + "/" + canvas.rootCanvas.renderMode : "none")}");
                }
                sb.AppendLine("  --- Dialogue detail ---");
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<RectTransform>()))
                {
                    var rt = o.Cast<RectTransform>();
                    if (rt == null) continue;
                    var path = PathOf(rt);
                    if (!path.Contains("View - Dialogue")) continue;
                    var line = $"    '{path}' active={rt.gameObject.activeInHierarchy} size={rt.rect.size} pos={rt.position} scale={rt.lossyScale}";
                    var cr = rt.GetComponent<CanvasRenderer>();
                    if (cr != null) line += $" cull={cr.cull} alpha={cr.GetAlpha()} hasMoved={cr.hasMoved}";
                    var tmp = rt.GetComponent<TMPro.TMP_Text>();
                    if (tmp != null) line += $" text='{tmp.text}' visibleChars={tmp.maxVisibleCharacters} chars={tmp.textInfo?.characterCount} enabled={tmp.enabled}";
                    if (rt.GetComponent<UnityEngine.UI.RectMask2D>() != null) line += " [RectMask2D]";
                    if (rt.GetComponent<UnityEngine.UI.Mask>() != null) line += " [Mask]";
                    var behaviours = rt.GetComponents<Behaviour>();
                    var names = new List<string>();
                    foreach (var b in behaviours) if (b != null) names.Add(b.GetIl2CppType().Name + (b.enabled ? "" : "(off)"));
                    line += " {" + string.Join(",", names) + "}";
                    sb.AppendLine(line);
                }
                sb.AppendLine("  --- Renderers under canvases ---");
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
                {
                    var r = o.Cast<Renderer>();
                    if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    if (r.GetComponentInParent<Canvas>() == null) continue;
                    var m = r.sharedMaterial;
                    sb.AppendLine($"    {r.GetIl2CppType().Name} '{PathOf(r.transform)}' layer={r.gameObject.layer} pos={r.transform.position} " +
                                  $"shader={(m != null && m.shader != null ? m.shader.name : "null")}");
                }
            }
            catch (Exception e) { sb.AppendLine("  UI dump failed: " + e.Message); }
        }

        static string PathOf(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        static void SaveRT(RenderTexture rt, string path)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            UnityEngine.Object.Destroy(tex);
        }

        void DumpCameras()
        {
            var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Camera>(), true);
            Plugin.L.LogInfo($"--- Camera dump ({all.Length} total) ---");
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i].Cast<Camera>();
                if (c == null) continue;
                var tt = c.targetTexture;
                Plugin.L.LogInfo($"  '{c.name}' depth={c.depth} enabled={c.enabled} goActive={c.gameObject.activeInHierarchy} " +
                                 $"target={(tt == null ? "screen" : $"{tt.name} {tt.width}x{tt.height}")} mask=0x{c.cullingMask:X} " +
                                 $"ortho={c.orthographic} fov={c.fieldOfView:0.#}");
            }
        }

        void LogPreCullStats()
        {
            if (preCullCounts.Count == 0) { Plugin.L.LogInfo($"[frame {Time.frameCount}] no camera renders seen in the last interval"); return; }
            foreach (var kv in preCullCounts) Plugin.L.LogInfo($"[frame {Time.frameCount}] rendered: {kv.Key} x{kv.Value}");
            preCullCounts.Clear();
        }

        void OnDestroy()
        {
            VRSession.Shutdown();
        }
    }
}
