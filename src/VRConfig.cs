using BepInEx.Configuration;

namespace OlliVR
{
    internal class VRConfig
    {
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<float> WorldScale;
        public readonly ConfigEntry<bool> PositionalTracking;
        public readonly ConfigEntry<float> CameraPullIn;
        public readonly ConfigEntry<bool> AutoFrame;
        public readonly ConfigEntry<float> FramedFOV;
        public readonly ConfigEntry<float> FocusDistance;
        public readonly ConfigEntry<float> RenderScale;
        public readonly ConfigEntry<bool> FlipVertical;
        public readonly ConfigEntry<string> RecenterKey;
        public readonly ConfigEntry<bool> UIOverlay;
        public readonly ConfigEntry<float> UIDistance;
        public readonly ConfigEntry<float> UIWidth;
        public readonly ConfigEntry<float> UIHeightOffset;
        public readonly ConfigEntry<string> DisabledEffects;
        public readonly ConfigEntry<bool> ReplaceTAA;
        public readonly ConfigEntry<bool> UncapFramerate;
        public readonly ConfigEntry<bool> VerboseLogging;

        public VRConfig(ConfigFile f)
        {
            Enabled = f.Bind("General", "Enabled", true, "Enable the VR mod.");
            UncapFramerate = f.Bind("General", "UncapFramerate", true,
                "Disable the game's vsync / frame cap so SteamVR paces frames at the headset refresh rate.");
            VerboseLogging = f.Bind("General", "VerboseLogging", false,
                "Log camera dumps, render statistics, framing distances and first-frame traces. " +
                "Creating BepInEx\\OlliVR_capture.trigger always saves both eye images as PNGs.");

            WorldScale = f.Bind("View", "WorldScale", 1.0f,
                "Game units per real-world metre. Scales both stereo separation (IPD) and head movement. " +
                "Higher = the world looks smaller (miniature), lower = bigger.");
            PositionalTracking = f.Bind("View", "PositionalTracking", true,
                "Apply head translation (leaning). Off = rotation only.");
            CameraPullIn = f.Bind("View", "CameraPullIn", 0.0f,
                "Move the view forward along the game camera's look direction (game units). " +
                "Negative values pull back. Useful because the headset FOV is much wider than the game's.");
            AutoFrame = f.Bind("View", "AutoFrame", true,
                "Automatically move the viewpoint toward the skater so the game's framing fills FramedFOV degrees of your view.");
            FramedFOV = f.Bind("View", "FramedFOV", 60f,
                "With AutoFrame: how many degrees (vertically) the game's normal view should span in the headset. Larger = closer.");
            FocusDistance = f.Bind("View", "FocusDistance", 0f,
                "With AutoFrame: distance from the game camera to the skater in game units. 0 = detect from Cinemachine.");
            RecenterKey = f.Bind("View", "RecenterKey", "F8",
                "Keyboard key (UnityEngine.InputSystem.Key name) that recenters the view. Holding L3+R3 for 1s on a gamepad also recenters.");

            RenderScale = f.Bind("Rendering", "RenderScale", 1.0f,
                "Multiplier on SteamVR's recommended per-eye resolution.");
            FlipVertical = f.Bind("Rendering", "FlipVertical", true,
                "Flip eye images vertically on submit. Toggle if the headset image is upside down.");
            ReplaceTAA = f.Bind("Rendering", "ReplaceTAA", true,
                "Replace temporal anti-aliasing with FXAA (TAA history is shared between eyes and ghosts in stereo).");
            DisabledEffects = f.Bind("Rendering", "DisabledEffects", "MotionBlur",
                "Comma-separated Post Processing v2 effect type names to turn off (e.g. MotionBlur,DepthOfField,Vignette,ChromaticAberration).");

            UIOverlay = f.Bind("UI", "UIOverlay", true,
                "Show the game's screen-space UI as a flat SteamVR overlay panel in front of you.");
            UIDistance = f.Bind("UI", "UIDistance", 1.6f, "Distance of the UI panel in metres.");
            UIWidth = f.Bind("UI", "UIWidth", 2.0f, "Width of the UI panel in metres.");
            UIHeightOffset = f.Bind("UI", "UIHeightOffset", 0.0f, "Vertical offset of the UI panel in metres.");
        }
    }
}
