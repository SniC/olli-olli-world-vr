using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using Valve.VR;

namespace OlliVR
{
    /// <summary>
    /// Moves the game's screen-space canvases onto a dedicated off-screen camera and shows the resulting
    /// texture as a flat SteamVR overlay panel, anchored in front of the recentered view.
    /// </summary>
    internal class UIOverlay
    {
        const int Width = 1920, Height = 1080;

        public Camera Camera { get; private set; }
        RenderTexture rt;
        readonly HashSet<int> converted = new();
        Vector3 lastOriginPos = new(float.NaN, 0, 0);
        Quaternion lastOriginRot;

        public bool Init()
        {
            try
            {
                var ov = VRSession.Overlay;
                if (ov == null) { Plugin.L.LogWarning("IVROverlay unavailable; UI overlay disabled."); return false; }

                rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    name = "OlliVR_UI",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                rt.Create();
                UnityEngine.Object.DontDestroyOnLoad(rt);

                var go = new GameObject("OlliVR_UICamera");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                // Far away from the level so it never sees world geometry on the UI layer.
                go.transform.position = new Vector3(0, -50000, 0);
                Camera = go.AddComponent<Camera>();
                Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = new Color(0, 0, 0, 0);
                Camera.cullingMask = 1 << 5;
                Camera.depth = 100;
                Camera.nearClipPlane = 0.01f;
                Camera.farClipPlane = 10f;
                Camera.targetTexture = rt;
                Camera.allowHDR = false;
                Camera.allowMSAA = false;

                ulong handle = 0;
                var err = ov.CreateOverlay("olliolliworld.vr.ui", "OlliOlli World UI", ref handle);
                if (err != EVROverlayError.None) { Plugin.L.LogWarning("CreateOverlay failed: " + err); return false; }
                VRSession.OverlayHandle = handle;
                ov.SetOverlayWidthInMeters(handle, Plugin.Cfg.UIWidth.Value);
                bool flip = Plugin.Cfg.FlipVertical.Value;
                var bounds = new VRTextureBounds_t { uMin = 0, uMax = 1, vMin = flip ? 1 : 0, vMax = flip ? 0 : 1 };
                ov.SetOverlayTextureBounds(handle, ref bounds);
                ov.SetOverlayFlag(handle, VROverlayFlags.IsPremultiplied, false);
                ov.SetOverlaySortOrder(handle, 1);
                ov.ShowOverlay(handle);

                VRSession.SetOverlayTexture(rt.GetNativeTexturePtr());

                // "Virtual screen" behind the UI for flat (orthographic) game views such as the world map.
                ScreenTexture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    name = "OlliVR_Screen",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                ScreenTexture.Create();
                UnityEngine.Object.DontDestroyOnLoad(ScreenTexture);
                ulong sh = 0;
                err = ov.CreateOverlay("olliolliworld.vr.screen", "OlliOlli World Screen", ref sh);
                if (err == EVROverlayError.None)
                {
                    VRSession.ScreenHandle = sh;
                    ov.SetOverlayWidthInMeters(sh, Plugin.Cfg.UIWidth.Value);
                    ov.SetOverlayTextureBounds(sh, ref bounds);
                    ov.SetOverlaySortOrder(sh, 0);
                    VRSession.SetScreenTexture(ScreenTexture.GetNativeTexturePtr());
                }
                else Plugin.L.LogWarning("CreateOverlay (screen) failed: " + err);
                Plugin.L.LogInfo("UI overlay created.");
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogError("UI overlay init failed: " + e);
                return false;
            }
        }

        /// <summary>Retargets screen-space canvases to the UI camera. Called periodically to catch new canvases.</summary>
        public void ScanCanvases()
        {
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Canvas>()))
                {
                    var c = o.Cast<Canvas>();
                    if (c == null || !c.isRootCanvas) continue;

                    if (!ShouldConvert(c)) continue;

                    c.renderMode = RenderMode.ScreenSpaceCamera;
                    c.worldCamera = Camera;
                    c.planeDistance = 1f;
                    Camera.cullingMask |= 1 << c.gameObject.layer;

                    if (converted.Add(c.GetInstanceID()))
                        Plugin.L.LogInfo($"UI canvas '{c.name}' -> VR overlay (layer {c.gameObject.layer}, order {c.sortingOrder})");
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Canvas scan error: " + e.Message);
            }
        }

        /// <summary>
        /// Overlay canvases always move to the panel. Screen-space-camera canvases move only if they were drawn
        /// by a UI-only camera (like the game's own "UI Camera"). Canvases attached to a 3D scene camera (for
        /// example the front end's "World Space Back/Front" backdrops) stay in the stereo scene.
        /// </summary>
        bool ShouldConvert(Canvas c)
        {
            if (c.renderMode == RenderMode.ScreenSpaceOverlay) return true;
            if (c.renderMode != RenderMode.ScreenSpaceCamera) return false;
            var wc = c.worldCamera;
            if (wc == null) return true;            // behaves like an overlay
            if (wc == Camera) return false;         // already ours
            return (wc.cullingMask & ~(1 << 5)) == 0; // UI-only camera
        }

        public RenderTexture Texture => rt;
        public RenderTexture ScreenTexture { get; private set; }
        bool screenVisible;

        public void SetScreenVisible(bool visible)
        {
            if (visible == screenVisible || VRSession.ScreenHandle == 0) return;
            screenVisible = visible;
            if (visible) VRSession.Overlay.ShowOverlay(VRSession.ScreenHandle);
            else VRSession.Overlay.HideOverlay(VRSession.ScreenHandle);
            Plugin.L.LogInfo(visible ? "Flat view -> virtual screen" : "Virtual screen hidden");
        }

        /// <summary>Places the panel straight ahead of the recentered origin (in seated tracking space).</summary>
        public void UpdatePlacement(Vector3 originPos, Quaternion originRot)
        {
            if (originPos == lastOriginPos && originRot == lastOriginRot) return;
            lastOriginPos = originPos; lastOriginRot = originRot;

            var pos = originPos + originRot * new Vector3(0, Plugin.Cfg.UIHeightOffset.Value, Plugin.Cfg.UIDistance.Value);
            var m = VRSession.ToOpenVR(pos, originRot);
            VRSession.Overlay.SetOverlayTransformAbsolute(VRSession.OverlayHandle, VRSession.Universe, ref m);
            if (VRSession.ScreenHandle != 0)
                VRSession.Overlay.SetOverlayTransformAbsolute(VRSession.ScreenHandle, VRSession.Universe, ref m);
        }
    }
}
