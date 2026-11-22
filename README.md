# OlliVR: OlliOlli World in VR

A BepInEx 6 (IL2CPP) plugin that renders OlliOlli World in stereo through SteamVR (Steam Frame, Index, Quest Link, and so on). The side-on view stays. Your head acts as the game's follow camera, so turning and leaning lets you look around. Play with a gamepad.

## How it works
- The game keeps its cameras disabled and renders them by hand. The mod notes, through `Camera.onPreCull`, which cameras actually rendered to the screen each frame.
- At the end of each frame, it renders those cameras again, once per eye, into RenderTextures. This uses the predicted HMD pose and SteamVR's own per-eye projection matrix.
- The eye textures go to the SteamVR compositor on Unity's render thread through `GL.IssuePluginEvent`. That thread also does the `WaitGetPoses` frame pacing, so the game's main thread never blocks on SteamVR. Each submit carries the pose it was rendered with (`Submit_TextureWithPose`), so the compositor's reprojection is correct.
- The game frames the skater through a narrow ~33° lens from far away. Auto-framing moves the viewpoint forward so that framing fills a comfortable part of the headset's field of view.
- Screen-space UI is moved to an off-screen camera and shown as a SteamVR overlay panel in front of you.
- TAA is swapped for FXAA, and motion blur is turned off. Both break in stereo.

## Install (already done on this machine)
1. Copy BepInEx 6 Bleeding Edge, the IL2CPP win-x64 build, into the game folder. Run the game once so it generates `BepInEx\interop`.
2. Run `dotnet build -c Release`. This copies `OlliVR.dll` and `openvr_api.dll` (taken from SteamVR) to `BepInEx\plugins\OlliVR\`.

## Use
- Connect the headset to SteamVR, then start the game. If the headset connects later, the mod retries every 5 s.
- **Recenter**: press F8, or hold L3 + R3 for 1 s.
- Config file: `BepInEx\config\olliolliworld.vr.cfg`. It is created on first run. The main settings:
  - `FramedFOV` (default 60): how many degrees of your vision the game's normal framing fills. Raise it to sit closer to the skater, lower it to sit further back. `AutoFrame` works out the skater's distance from a raycast through the centre of the view (and Cinemachine), and you can pin it with `FocusDistance`.
  - `WorldScale`: stereo depth and head-movement scale. Raise it for a miniature look.
  - `CameraPullIn`: an extra manual push forward (or back, with a negative value) on top of auto-framing.
  - `VerboseLogging`: logs camera, framing and trace output for debugging. Creating an empty file `BepInEx\OlliVR_capture.trigger` while playing saves both eye images as PNGs.
  - `RenderScale`, `FlipVertical` (turn on if the image is upside down), `UIDistance`/`UIWidth`.
- Logs: `BepInEx\LogOutput.log`. Lines are prefixed `OlliVR`.

## Uninstall
Delete `winhttp.dll` from the game folder, or set `enabled = false` in `doorstop_config.ini`.
