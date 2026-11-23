using BepInEx.Configuration;

namespace OlliVR
{
    internal class VRConfig
    {
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<bool> UncapFramerate;
        public readonly ConfigEntry<bool> VerboseLogging;
        public readonly ConfigEntry<bool> HeadlessPreview;

        public readonly ConfigEntry<float> ScreenDistance;
        public readonly ConfigEntry<float> ScreenScale;
        public readonly ConfigEntry<float> ScreenHeightOffset;
        public readonly ConfigEntry<string> RecenterKey;

        public readonly ConfigEntry<float> DepthStrength;
        public readonly ConfigEntry<float> ConvergenceDistance;
        public readonly ConfigEntry<float> HeadCoupled;

        public readonly ConfigEntry<float> AmbientGlow;
        public readonly ConfigEntry<float> FloorReflection;

        public readonly ConfigEntry<bool> FlipVertical;
        public readonly ConfigEntry<bool> ReplaceTAA;
        public readonly ConfigEntry<string> DisabledEffects;

        public VRConfig(ConfigFile f)
        {
            Enabled = f.Bind("General", "Enabled", true, "Enable the VR mod.");
            UncapFramerate = f.Bind("General", "UncapFramerate", true,
                "Disable the game's vsync / frame cap so SteamVR paces frames at the headset refresh rate.");
            VerboseLogging = f.Bind("General", "VerboseLogging", false,
                "Log camera dumps, render statistics and convergence distances. " +
                "Creating BepInEx\\OlliVR_capture.trigger always saves the screen images as PNGs.");

            HeadlessPreview = f.Bind("General", "HeadlessPreview", false,
                "Development: if no headset is connected at start-up, run everything except SteamVR (fixed head pose) so the room, the 3D screen and the capture trigger work without a headset. Restart the game to use VR.");

            ScreenDistance = f.Bind("Screen", "ScreenDistance", 2.0f,
                "Distance from you to the virtual screen, in metres.");
            ScreenScale = f.Bind("Screen", "ScreenScale", 1.0f,
                "Screen size multiplier. At 1.0 the screen covers exactly the game camera's field of view, so the " +
                "perspective matches the flat game. Bigger = larger screen (slightly stronger perspective).");
            ScreenHeightOffset = f.Bind("Screen", "ScreenHeightOffset", 0.0f,
                "Raise (+) or lower (-) the screen relative to your eye height, in metres.");
            RecenterKey = f.Bind("Screen", "RecenterKey", "F8",
                "Keyboard key (UnityEngine.InputSystem.Key name) that puts the screen straight in front of you. " +
                "Holding L3+R3 for 1s on a gamepad does the same.");

            DepthStrength = f.Bind("Stereo", "DepthStrength", 1.0f,
                "Multiplier on eye separation. 1.0 = physically correct for the screen size and distance; " +
                "higher = deeper 3D, 0 = flat.");
            ConvergenceDistance = f.Bind("Stereo", "ConvergenceDistance", 0f,
                "Game-world distance (from the game camera) that sits exactly on the screen surface. Nearer things pop " +
                "out of the screen, farther things recede behind it. 0 = automatic (whatever is in the centre of the view).");
            HeadCoupled = f.Bind("Stereo", "HeadCoupled", 1.0f,
                "Fish-tank VR: how much your head movement changes the perspective, as if the screen were a window into " +
                "the game. 1.0 = full window effect, 0 = fixed viewpoint (plain 3D screen).");

            AmbientGlow = f.Bind("Environment", "AmbientGlow", 1.0f,
                "Brightness of the light the screen's edge colours cast on the wall and floor around it. 0 = dark room.");
            FloorReflection = f.Bind("Environment", "FloorReflection", 0.2f,
                "How glossy the varnished floor is: strength of the room's (and the TV's) reflection in it, 0 to 1. 0 = matte.");

            FlipVertical = f.Bind("Rendering", "FlipVertical", true,
                "Flip images vertically on submit. Toggle if the screen is upside down.");
            ReplaceTAA = f.Bind("Rendering", "ReplaceTAA", true,
                "Replace temporal anti-aliasing with FXAA (TAA history is shared between eyes and ghosts in stereo).");
            DisabledEffects = f.Bind("Rendering", "DisabledEffects", "MotionBlur",
                "Comma-separated Post Processing v2 effect type names to turn off (e.g. MotionBlur,DepthOfField,Vignette,ChromaticAberration).");
        }
    }
}
