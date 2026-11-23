using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace OlliVR
{
    /// <summary>
    /// Thin wrapper over OpenVR: init/shutdown, per-frame poses, and texture submission.
    /// Submission happens on Unity's render thread through GL.IssuePluginEvent, because the
    /// D3D11 immediate context belongs to that thread (Unity runs with threaded rendering).
    /// </summary>
    internal static unsafe class VRSession
    {
        public const int EventSubmitOverlay = 2;
        public const int EventSubmitEyesBase = 100; // + pose slot

        public static bool Ready { get; private set; }
        public static CVRSystem System;
        public static CVRCompositor Compositor;
        public static CVROverlay Overlay;

        public static uint EyeWidth, EyeHeight;
        public static ETrackingUniverseOrigin Universe = ETrackingUniverseOrigin.TrackingUniverseSeated;

        static readonly TrackedDevicePose_t[] renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        static readonly TrackedDevicePose_t[] gamePoses = new TrackedDevicePose_t[0];

        // Render-thread shared state (written on main thread before issuing the event).
        static volatile IntPtr leftTex, rightTex, overlayTex;
        static VRTextureBounds_t eyeBounds;
        public static ulong OverlayHandle;
        static int lastSubmitErrorLogged;

        public static IntPtr RenderEventFunc { get; private set; }

        // Step tracing for the first few VR frames (crash diagnosis). Needs BepInEx InstantFlushing.
        public static int TraceBudget = 40;
        public static void Trace(string s)
        {
            if (TraceBudget <= 0) return;
            TraceBudget--;
            Plugin.L.LogInfo("[trace] " + s);
        }

        public static bool Init(bool logFailure = true)
        {
            try
            {
                if (!OpenVR.IsRuntimeInstalled())
                {
                    if (logFailure) Plugin.L.LogError("SteamVR runtime not installed.");
                    return false;
                }
                var err = EVRInitError.None;
                System = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
                if (err != EVRInitError.None || System == null)
                {
                    if (logFailure) Plugin.L.LogError($"OpenVR init failed: {err} ({OpenVR.GetStringForHmdError(err)}). Retrying every 5 s...");
                    try { OpenVR.Shutdown(); } catch { }
                    return false;
                }
                Compositor = OpenVR.Compositor;
                Overlay = OpenVR.Overlay;
                if (Compositor == null)
                {
                    Plugin.L.LogError("IVRCompositor unavailable.");
                    return false;
                }
                Compositor.SetTrackingSpace(Universe);
                System.GetRecommendedRenderTargetSize(ref EyeWidth, ref EyeHeight);

                var hz = System.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref _propErr);
                if (hz > 1f) frameDuration = 1f / hz;
                vsyncToPhotons = System.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_SecondsFromVsyncToPhotons_Float, ref _propErr);

                delegate* unmanaged<int, void> fp = &OnRenderEvent;
                RenderEventFunc = (IntPtr)fp;

                Ready = true;
                Plugin.L.LogInfo($"OpenVR ready. Recommended eye size {EyeWidth}x{EyeHeight}, " +
                                 $"HMD refresh {System.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref _propErr):0.#} Hz");
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogError("OpenVR init threw: " + e);
                return false;
            }
        }
        static ETrackedPropertyError _propErr;

        public static void Shutdown()
        {
            if (!Ready) return;
            Ready = false;
            try { OpenVR.Shutdown(); } catch { }
        }

        static float frameDuration = 1f / 90f, vsyncToPhotons;
        static readonly TrackedDevicePose_t[] predictedPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        /// <summary>
        /// Non-blocking predicted HMD pose for the frame being rendered now. The compositor wait happens on the
        /// render thread (see OnRenderEvent), so the main thread never blocks on SteamVR.
        /// </summary>
        public static bool GetPredictedHmdPose(out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            float sinceVsync = 0; ulong frameCounter = 0;
            System.GetTimeSinceLastVsync(ref sinceVsync, ref frameCounter);
            // This frame is displayed roughly one frame after the render thread picks it up.
            float predict = Mathf.Clamp(frameDuration - sinceVsync + frameDuration + vsyncToPhotons, 0f, 0.1f);
            System.GetDeviceToAbsoluteTrackingPose(Universe, predict, predictedPoses);
            var p = predictedPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (!p.bPoseIsValid) return false;
            LastRenderPose = p.mDeviceToAbsoluteTracking;
            ToUnity(p.mDeviceToAbsoluteTracking, out pos, out rot);
            return true;
        }

        /// <summary>The HMD pose the current frame is being rendered with (raw OpenVR).</summary>
        public static HmdMatrix34_t LastRenderPose;

        // Ring buffer so a render-thread event always submits with the pose of the frame that issued it,
        // even if the main thread has already moved on to the next frame.
        const int PoseSlots = 8;
        static readonly HmdMatrix34_t[] slotPoses = new HmdMatrix34_t[PoseSlots];
        static int nextSlot;

        /// <summary>Returns the event id to pass to GL.IssuePluginEvent for submitting this frame.</summary>
        public static int PrepareSubmit()
        {
            int slot = nextSlot;
            nextSlot = (nextSlot + 1) % PoseSlots;
            slotPoses[slot] = LastRenderPose;
            return EventSubmitEyesBase + slot;
        }

        public static bool RecenterRequested;

        public static void PollEvents()
        {
            var ev = new VREvent_t();
            uint size = (uint)Marshal.SizeOf<VREvent_t>();
            while (System.PollNextEvent(ref ev, size))
            {
                if ((EVREventType)ev.eventType == EVREventType.VREvent_TrackedDeviceUserInteractionStarted &&
                    ev.trackedDeviceIndex == OpenVR.k_unTrackedDeviceIndex_Hmd)
                {
                    // Headset put on: put the screen in front of the user.
                    RecenterRequested = true;
                }
                if ((EVREventType)ev.eventType == EVREventType.VREvent_Quit)
                {
                    Plugin.L.LogInfo("SteamVR requested quit.");
                    System.AcknowledgeQuit_Exiting();
                    Application.Quit();
                }
            }
        }

        /// <summary>Eye position relative to the head (metres, Unity convention).</summary>
        public static Vector3 GetEyeOffset(EVREye eye)
        {
            ToUnity(System.GetEyeToHeadTransform(eye), out var pos, out _);
            return pos;
        }

        /// <summary>Full eye-to-head transform (some headsets have canted displays).</summary>
        public static void GetEyeToHead(EVREye eye, out Vector3 pos, out Quaternion rot)
            => ToUnity(System.GetEyeToHeadTransform(eye), out pos, out rot);

        /// <summary>The headset's own projection for an eye, as a Unity camera projection matrix.</summary>
        public static Matrix4x4 GetEyeProjection(EVREye eye, float near, float far)
        {
            var p = System.GetProjectionMatrix(eye, near, far);
            var m = new Matrix4x4();
            m.m00 = p.m0; m.m01 = p.m1; m.m02 = p.m2; m.m03 = p.m3;
            m.m10 = p.m4; m.m11 = p.m5; m.m12 = p.m6; m.m13 = p.m7;
            m.m20 = p.m8; m.m21 = p.m9; m.m22 = p.m10; m.m23 = p.m11;
            m.m30 = p.m12; m.m31 = p.m13; m.m32 = p.m14; m.m33 = p.m15;
            return m;
        }

        /// <summary>Height of the seated zero pose above the floor, or 0 when unknown.</summary>
        public static float SeatedHeightAboveFloor()
        {
            try { return System.GetSeatedZeroPoseToStandingAbsoluteTrackingPose().m7; }
            catch { return 0f; }
        }

        /// <summary>OpenGL-style off-centre projection from view-space tangents (extent / distance).</summary>
        public static Matrix4x4 Frustum(float left, float right, float bottom, float top, float n, float f)
        {
            var m = new Matrix4x4();
            m.m00 = 2f / (right - left);
            m.m02 = (right + left) / (right - left);
            m.m11 = 2f / (top - bottom);
            m.m12 = (top + bottom) / (top - bottom);
            m.m22 = -(f + n) / (f - n);
            m.m23 = -(2f * f * n) / (f - n);
            m.m32 = -1f;
            return m;
        }

        /// <summary>OpenVR right-handed pose -> Unity left-handed position/rotation.</summary>
        public static void ToUnity(HmdMatrix34_t p, out Vector3 pos, out Quaternion rot)
        {
            pos = new Vector3(p.m3, p.m7, -p.m11);
            // Columns of the converted rotation matrix (z axis flipped).
            var up = new Vector3(p.m1, p.m5, -p.m9);
            var fwd = new Vector3(-p.m2, -p.m6, p.m10);
            rot = fwd.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(fwd, up) : Quaternion.identity;
        }

        public static HmdMatrix34_t ToOpenVR(Vector3 pos, Quaternion rot)
        {
            var m = Matrix4x4.TRS(pos, rot, Vector3.one);
            return new HmdMatrix34_t
            {
                m0 = m.m00, m1 = m.m01, m2 = -m.m02, m3 = m.m03,
                m4 = m.m10, m5 = m.m11, m6 = -m.m12, m7 = m.m13,
                m8 = -m.m20, m9 = -m.m21, m10 = m.m22, m11 = -m.m23,
            };
        }

        public static void SetEyeTextures(IntPtr left, IntPtr right, bool flip)
        {
            leftTex = left; rightTex = right;
            eyeBounds = new VRTextureBounds_t { uMin = 0, uMax = 1, vMin = flip ? 1 : 0, vMax = flip ? 0 : 1 };
        }

        public static void SetOverlayTexture(IntPtr tex) => overlayTex = tex;
        public static void SetScreenTexture(IntPtr tex) => screenTex = tex;
        public static ulong ScreenHandle;
        static volatile IntPtr screenTex;
        static int overlayErrorsLogged;
        public const int EventSubmitScreen = 3;

        [UnmanagedCallersOnly]
        static void OnRenderEvent(int id)
        {
            try
            {
                if (!Ready) return;
                if (id >= EventSubmitEyesBase && id < EventSubmitEyesBase + PoseSlots)
                {
                    var pose = slotPoses[id - EventSubmitEyesBase];
                    Trace($"render thread: submit begin L=0x{leftTex.ToInt64():X} R=0x{rightTex.ToInt64():X}");
                    if (leftTex == IntPtr.Zero || rightTex == IntPtr.Zero) return;
                    // Frame pacing lives here on the render thread, like Unity's own OpenVR plugin.
                    var we = Compositor.WaitGetPoses(renderPoses, gamePoses);
                    Trace("render thread: WaitGetPoses " + we);
                    SubmitEye(EVREye.Eye_Left, leftTex, pose);
                    Trace("render thread: left submitted");
                    SubmitEye(EVREye.Eye_Right, rightTex, pose);
                    Trace("render thread: right submitted");
                    Compositor.PostPresentHandoff();
                    Trace("render thread: handoff done");
                }
                else if (id == EventSubmitScreen)
                {
                    if (screenTex == IntPtr.Zero || ScreenHandle == 0 || Overlay == null) return;
                    var t = new Texture_t { handle = screenTex, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
                    var e = Overlay.SetOverlayTexture(ScreenHandle, ref t);
                    if (e != EVROverlayError.None && overlayErrorsLogged++ < 10) Plugin.L.LogWarning("SetOverlayTexture (screen) failed: " + e);
                }
                else if (id == EventSubmitOverlay)
                {
                    if (overlayTex == IntPtr.Zero || OverlayHandle == 0 || Overlay == null) return;
                    var t = new Texture_t { handle = overlayTex, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
                    var e = Overlay.SetOverlayTexture(OverlayHandle, ref t);
                    if (e != EVROverlayError.None && overlayErrorsLogged++ < 10) Plugin.L.LogWarning("SetOverlayTexture (ui) failed: " + e);
                }
            }
            catch (Exception e)
            {
                // Never let an exception escape onto Unity's render thread.
                try { Plugin.L.LogError("Render event error: " + e.Message); } catch { }
            }
        }

        static void SubmitEye(EVREye eye, IntPtr tex, HmdMatrix34_t pose)
        {
            // Tell the compositor exactly which pose this image was rendered with, so its reprojection
            // corrects from that pose rather than from the WaitGetPoses prediction.
            var t = new VRTextureWithPose_t
            {
                handle = tex, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto,
                mDeviceToAbsoluteTracking = pose,
            };
            var b = eyeBounds;
            ref var asBase = ref global::System.Runtime.CompilerServices.Unsafe.As<VRTextureWithPose_t, Texture_t>(ref t);
            var err = Compositor.Submit(eye, ref asBase, ref b, EVRSubmitFlags.Submit_TextureWithPose);
            if (err != EVRCompositorError.None && lastSubmitErrorLogged < 10)
            {
                lastSubmitErrorLogged++;
                Plugin.L.LogWarning($"Submit {eye} failed: {err}");
            }
        }
    }
}
