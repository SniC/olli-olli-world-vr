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
    /// Drives stereo rendering: each frame, after the game (and Cinemachine) has positioned its cameras,
    /// every on-screen game camera is re-rendered once per eye into per-eye RenderTextures, with the
    /// HMD pose applied relative to the game camera, and the result is submitted to SteamVR.
    /// </summary>
    public class VRRig : MonoBehaviour
    {
        public VRRig(IntPtr ptr) : base(ptr) { }

        RenderTexture leftRT, rightRT;
        bool initialized, failed, tracedFirstCameraFrame, captureRequested;
        float nextInitAttempt;
        int uiLayerMask = 1 << 5;

        // Recenter origin in tracking space (yaw only + position).
        Vector3 originPos;
        Quaternion originRot = Quaternion.identity;
        bool haveOrigin;
        float stickHoldTime;

        UIOverlay ui;
        float nextSceneFixup;
        readonly List<Camera> stereoCams = new();
        readonly List<Camera> flatCams = new();
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
            dumpCameras = true;
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

            int w = Mathf.Max(256, (int)(VRSession.EyeWidth * Plugin.Cfg.RenderScale.Value));
            int h = Mathf.Max(256, (int)(VRSession.EyeHeight * Plugin.Cfg.RenderScale.Value));
            leftRT = MakeEyeRT(w, h, "OlliVR_Left");
            rightRT = MakeEyeRT(w, h, "OlliVR_Right");
            VRSession.SetEyeTextures(leftRT.GetNativeTexturePtr(), rightRT.GetNativeTexturePtr(), Plugin.Cfg.FlipVertical.Value);
            Plugin.L.LogInfo($"Eye RTs {w}x{h}, colour space {QualitySettings.activeColorSpace}");

            // Exercise the per-eye OpenVR queries once at startup (also useful diagnostics).
            VRSession.GetEye(EVREye.Eye_Left, 0.1f, 1000f, out _, out _, out var pl);
            VRSession.GetEye(EVREye.Eye_Right, 0.1f, 1000f, out _, out _, out _);
            Plugin.L.LogInfo("Eye queries OK. Left projection:\n" + pl);
            VRSession.TraceBudget = 0;

            if (Plugin.Cfg.UIOverlay.Value)
            {
                ui = new UIOverlay();
                if (!ui.Init()) ui = null;
            }
            initialized = true;
            return true;
        }

        static RenderTexture MakeEyeRT(int w, int h, string name)
        {
            var rw = QualitySettings.activeColorSpace == ColorSpace.Linear ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Default;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, rw)
            {
                name = name,
                antiAliasing = 1,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            UnityEngine.Object.DontDestroyOnLoad(rt);
            return rt;
        }

        void Update()
        {
            if (!EnsureInit()) return;

            VRSession.PollEvents();
            HandleRecenterInput();

            if (Time.unscaledTime >= nextSceneFixup)
            {
                nextSceneFixup = Time.unscaledTime + 1f;
                if (Plugin.Cfg.VerboseLogging.Value && Time.unscaledTime >= nextDump) { nextDump = Time.unscaledTime + 15f; DumpCameras(); LogPreCullStats(); }
                var trigger = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "OlliVR_capture.trigger");
                if (System.IO.File.Exists(trigger)) { System.IO.File.Delete(trigger); captureRequested = true; }
                ApplySceneFixups();
                ui?.ScanCanvases();
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
                try { RenderAndSubmit(); }
                catch (Exception e) { Plugin.L.LogError("VR frame error: " + e); }
            }
        }

        void RenderAndSubmit()
        {
            if (!VRSession.GetPredictedHmdPose(out var hmdPos, out var hmdRot)) return;

            if (!haveOrigin)
            {
                // Yaw-only origin so the horizon stays level.
                var fwd = hmdRot * Vector3.forward;
                fwd.y = 0;
                originRot = fwd.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fwd.normalized, Vector3.up) : Quaternion.identity;
                originPos = hmdPos;
                haveOrigin = true;
                Plugin.L.LogInfo("View recentered.");
            }

            var inv = Quaternion.Inverse(originRot);
            var headRot = inv * hmdRot;
            var headPos = Plugin.Cfg.PositionalTracking.Value ? inv * (hmdPos - originPos) : Vector3.zero;

            CollectCameras();
            if (ui != null)
            {
                bool showScreen = flatCams.Count > 0;
                if (showScreen)
                {
                    RenderFlat();
                    GL.IssuePluginEvent(VRSession.RenderEventFunc, VRSession.EventSubmitScreen);
                }
                ui.SetScreenVisible(showScreen);
            }
            if (stereoCams.Count == 0)
            {
                // Menu / title screens have no 3D camera: show a dark backdrop behind the UI panel.
                var prev = RenderTexture.active;
                RenderTexture.active = leftRT; GL.Clear(true, true, new Color(0.08f, 0.08f, 0.1f, 1f));
                RenderTexture.active = rightRT; GL.Clear(true, true, new Color(0.08f, 0.08f, 0.1f, 1f));
                RenderTexture.active = prev;
                if (captureRequested) { captureRequested = false; Capture(hmdPos, hmdRot, headPos, headRot); }
                ui?.UpdatePlacement(originPos, originRot);
                GL.IssuePluginEvent(VRSession.RenderEventFunc, VRSession.PrepareSubmit());
                if (ui != null) GL.IssuePluginEvent(VRSession.RenderEventFunc, VRSession.EventSubmitOverlay);
                return;
            }

            if (!tracedFirstCameraFrame) { tracedFirstCameraFrame = true; if (Plugin.Cfg.VerboseLogging.Value) VRSession.TraceBudget = 30; }
            UpdateFraming(stereoCams[stereoCams.Count - 1]);
            insideVRRender = true;
            try
            {
                RenderEye(EVREye.Eye_Left, leftRT, headPos, headRot);
                RenderEye(EVREye.Eye_Right, rightRT, headPos, headRot);
            }
            finally { insideVRRender = false; }

            if (captureRequested)
            {
                captureRequested = false;
                Capture(hmdPos, hmdRot, headPos, headRot);
            }

            ui?.UpdatePlacement(originPos, originRot);
            VRSession.Trace($"IssuePluginEvent fn=0x{VRSession.RenderEventFunc.ToInt64():X}");
            GL.IssuePluginEvent(VRSession.RenderEventFunc, VRSession.PrepareSubmit());
            VRSession.Trace("IssuePluginEvent returned");
            if (ui != null) GL.IssuePluginEvent(VRSession.RenderEventFunc, VRSession.EventSubmitOverlay);
        }

        void Capture(Vector3 hmdPos, Quaternion hmdRot, Vector3 headPos, Quaternion headRot)
        {
            try
            {
                var dir = BepInEx.Paths.BepInExRootPath;
                SaveRT(leftRT, System.IO.Path.Combine(dir, "OlliVR_capture_L.png"));
                SaveRT(rightRT, System.IO.Path.Combine(dir, "OlliVR_capture_R.png"));
                if (ui != null) SaveRT(ui.Texture, System.IO.Path.Combine(dir, "OlliVR_capture_UI.png"));
                if (ui?.ScreenTexture != null && flatCams.Count > 0) SaveRT(ui.ScreenTexture, System.IO.Path.Combine(dir, "OlliVR_capture_Screen.png"));
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Capture: hmdPos={hmdPos.ToString("F3")} hmdRot={hmdRot.eulerAngles.ToString("F1")} " +
                              $"headPos={headPos.ToString("F3")} headRot={headRot.eulerAngles.ToString("F1")} origin={originPos.ToString("F3")}/{originRot.eulerAngles.ToString("F1")}");
                foreach (var c in stereoCams)
                {
                    sb.AppendLine($"  cam '{c.name}' pos={c.transform.position.ToString("F2")} rot={c.transform.rotation.eulerAngles.ToString("F1")} " +
                                  $"fov={c.fieldOfView} aspect={c.aspect} usePhysical={c.usePhysicalProperties} lensShift={c.lensShift}");
                    sb.AppendLine("  game projection:\n" + c.projectionMatrix);
                    sb.AppendLine("  worldToCamera:\n" + c.worldToCameraMatrix);
                    var comps = c.GetComponents(Il2CppType.Of<Component>());
                    var names = new List<string>();
                    foreach (var co in comps) if (co != null) names.Add(co.GetIl2CppType().FullName);
                    sb.AppendLine("  components: " + string.Join(", ", names));
                }
                Plugin.L.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.L.LogError("Capture failed: " + e); }
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

        // ---- Auto framing -------------------------------------------------------------------------
        // The game films the skater with a narrow (~33°) lens from far away. Seen through a ~110° headset
        // from the same spot, everything looks tiny and distant. We move the viewpoint forward along the
        // camera axis so the game's framed area spans FramedFOV degrees of your vision instead.
        float framingPull, focusDistance;
        float nextFramingLog;

        void UpdateFraming(Camera cam)
        {
            if (!Plugin.Cfg.AutoFrame.Value || cam.orthographic) { framingPull = 0; return; }

            float d = Plugin.Cfg.FocusDistance.Value;
            float cmDist = 0, rayDist = 0;
            if (d <= 0f)
            {
                // The Cinemachine target is not always the subject (e.g. level intro screens), so also
                // raycast down the view centre and use whichever is nearer.
                cmDist = GetCinemachineFocusDistance(cam);
                rayDist = GetRaycastFocusDistance(cam);
                if (cmDist > 0 && rayDist > 0) d = Mathf.Min(cmDist, rayDist);
                else d = Mathf.Max(cmDist, rayDist);
            }
            if (d <= 0f) d = focusDistance > 0 ? focusDistance : 15f;
            // Smooth so cuts and passing foreground objects don't snap the view.
            focusDistance = focusDistance <= 0 ? d : Mathf.Lerp(focusDistance, d, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 1.5f));

            float gameHalf = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float vrHalf = Mathf.Clamp(Plugin.Cfg.FramedFOV.Value, 10f, 150f) * 0.5f * Mathf.Deg2Rad;
            float newDist = focusDistance * Mathf.Tan(gameHalf) / Mathf.Tan(vrHalf);
            framingPull = Mathf.Max(0f, focusDistance - newDist);

            if (Plugin.Cfg.VerboseLogging.Value && Time.unscaledTime >= nextFramingLog)
            {
                nextFramingLog = Time.unscaledTime + 10f;
                Plugin.L.LogInfo($"Framing: cinemachine {cmDist:0.0}, raycast {rayDist:0.0}, focus distance {focusDistance:0.0}, game fov {cam.fieldOfView:0.0}, pull-in {framingPull:0.0} -> view distance {newDist:0.0}");
            }
        }

        static float GetRaycastFocusDistance(Camera cam)
        {
            try
            {
                var t = cam.transform;
                // Average a few rays around the centre to avoid snapping on thin objects.
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

        bool dumpCameras = true;
        int lastCamCount = -1;
        float nextDump;

        void DumpCameras()
        {
            var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Camera>(), true);
            Plugin.L.LogInfo($"--- Camera dump ({Camera.allCamerasCount} enabled, {all.Length} total; main={(Camera.main == null ? "null" : Camera.main.name)}) ---");
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i].Cast<Camera>();
                if (c == null) continue;
                var tt = c.targetTexture;
                Plugin.L.LogInfo($"  '{c.name}' path={PathOf(c.transform)} depth={c.depth} enabled={c.enabled} goActive={c.gameObject.activeInHierarchy} " +
                                 $"target={(tt == null ? "screen" : $"{tt.name} {tt.width}x{tt.height}")} mask=0x{c.cullingMask:X} " +
                                 $"clear={c.clearFlags} ortho={c.orthographic} fov={c.fieldOfView:0.#} near={c.nearClipPlane} far={c.farClipPlane}");
            }
        }

        static string PathOf(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        // Cameras the game rendered to the screen this frame. The game renders cameras manually
        // (they stay disabled), so Camera.allCameras is empty; onPreCull fires for manual renders too.
        readonly List<Camera> renderedThisFrame = new();
        int renderedFrame = -1;
        bool insideVRRender;
        readonly Dictionary<string, int> preCullCounts = new();

        void OnAnyPreCull(Camera c)
        {
            if (insideVRRender || c == null) return;
            if (renderedFrame != Time.frameCount) { renderedFrame = Time.frameCount; renderedThisFrame.Clear(); }
            var key = c.name + (c.targetTexture == null ? " -> screen" : " -> " + c.targetTexture.name);
            preCullCounts[key] = preCullCounts.TryGetValue(key, out var n) ? n + 1 : 1;
            if (c.targetTexture == null && !renderedThisFrame.Contains(c)) renderedThisFrame.Add(c);
        }

        void LogPreCullStats()
        {
            if (preCullCounts.Count == 0) { Plugin.L.LogInfo($"[frame {Time.frameCount}] no camera renders seen in the last interval"); return; }
            foreach (var kv in preCullCounts) Plugin.L.LogInfo($"[frame {Time.frameCount}] rendered: {kv.Key} x{kv.Value}");
            preCullCounts.Clear();
        }

        void CollectCameras()
        {
            stereoCams.Clear();
            flatCams.Clear();
            if (renderedFrame != Time.frameCount) return;
            for (int i = 0; i < renderedThisFrame.Count; i++)
            {
                var c = renderedThisFrame[i];
                if (c == null) continue;
                if (c.targetTexture != null) continue;              // off-screen / our own UI cam
                if (ui != null && c == ui.Camera) continue;
                if ((c.cullingMask & ~uiLayerMask) == 0) continue;  // pure UI camera
                // Orthographic views (world map) have no depth to show; they go on the flat virtual screen.
                bool flat = c.orthographic && ui != null && ui.ScreenTexture != null;
                (flat ? flatCams : stereoCams).Add(c);

                if (loggedCams.Add(c.name))
                    Plugin.L.LogInfo($"{(flat ? "Flat" : "Stereo")} camera '{c.name}': depth={c.depth} ortho={c.orthographic} fov={c.fieldOfView:0.#} " +
                                     $"near={c.nearClipPlane} far={c.farClipPlane} mask=0x{c.cullingMask:X} clear={c.clearFlags} pos={c.transform.position}");
            }
            stereoCams.Sort((a, b) => a.depth.CompareTo(b.depth));
            flatCams.Sort((a, b) => a.depth.CompareTo(b.depth));
        }

        /// <summary>Renders orthographic cameras unchanged (own projection, no head tracking) to the virtual screen.</summary>
        void RenderFlat()
        {
            var rt = ui.ScreenTexture;
            insideVRRender = true;
            try
            {
                for (int i = 0; i < flatCams.Count; i++)
                {
                    var cam = flatCams[i];
                    var saved = cam.targetTexture;
                    try
                    {
                        cam.targetTexture = rt;
                        if (i == 0 && cam.clearFlags == CameraClearFlags.Nothing)
                        {
                            var prev = RenderTexture.active;
                            RenderTexture.active = rt; GL.Clear(true, true, Color.black);
                            RenderTexture.active = prev;
                        }
                        cam.Render();
                    }
                    finally { cam.targetTexture = saved; }
                }
            }
            finally { insideVRRender = false; }
        }

        void RenderEye(EVREye eye, RenderTexture rt, Vector3 headPos, Quaternion headRot)
        {
            float scale = Plugin.Cfg.WorldScale.Value;
            float pull = Plugin.Cfg.CameraPullIn.Value + framingPull;

            for (int i = 0; i < stereoCams.Count; i++)
            {
                var cam = stereoCams[i];
                var tr = cam.transform;
                var savedPos = tr.position;
                var savedRot = tr.rotation;
                var savedTarget = cam.targetTexture;
                bool savedOrtho = cam.orthographic;

                VRSession.GetEye(eye, cam.nearClipPlane, cam.farClipPlane, out var eyeOff, out var eyeRotOff, out var proj);

                var basePos = savedPos + savedRot * (Vector3.forward * pull);
                var rot = savedRot * headRot;
                var pos = basePos + savedRot * (headPos * scale) + rot * (eyeOff * scale);

                try
                {
                    tr.SetPositionAndRotation(pos, rot * eyeRotOff);
                    cam.orthographic = false;
                    cam.projectionMatrix = proj;
                    cam.targetTexture = rt;
                    // The first (deepest) camera must clear the eye texture; stacked cameras composite on top.
                    if (i == 0 && cam.clearFlags == CameraClearFlags.Nothing)
                    {
                        var prevActive = RenderTexture.active;
                        RenderTexture.active = rt;
                        GL.Clear(true, true, Color.black);
                        RenderTexture.active = prevActive;
                    }
                    VRSession.Trace($"Render {cam.name} {eye}");
                    cam.Render();
                    VRSession.Trace($"Render {cam.name} {eye} done");
                }
                finally
                {
                    cam.targetTexture = savedTarget;
                    cam.orthographic = savedOrtho;
                    cam.ResetProjectionMatrix();
                    tr.SetPositionAndRotation(savedPos, savedRot);
                }
            }
        }

        void OnDestroy()
        {
            VRSession.Shutdown();
        }
    }
}
