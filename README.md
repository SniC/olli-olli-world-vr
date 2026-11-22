# OlliVR: OlliOlli World on a 3D screen in VR

A BepInEx 6 (IL2CPP) plugin that shows OlliOlli World on a big stereoscopic 3D virtual screen in SteamVR (Steam Frame, Index, Quest Link, and so on). The game's own side-on framing is kept, with real 3D depth. **Head-coupled perspective** ("fish tank VR") makes the screen work like a window into the game world: lean or move your head and the view through it shifts. Play with a gamepad.

## How it works
- The game keeps its cameras disabled and renders them by hand. The mod notes, through `Camera.onPreCull`, which cameras actually rendered to the monitor each frame.
- At the end of each frame, it renders those cameras again, once per eye, with a generalised off-axis projection. Each eye's real position (from SteamVR) looks through the screen rectangle. The screen maps to the game's image plane at the **convergence distance**, so objects there sit on the screen surface: nearer things pop out and farther things recede.
- The screen size and window come from the game camera's actual projection matrix. The game zooms and shifts the view with a custom projection, which is why the skater sits left of centre. From the normal viewing position, the image is identical to the flat game.
- The game uses its own scriptable render pipeline (`Roll7.NoComplyRenderer`). One property shapes the whole design: a camera render does **not** draw on top of its target. It renders into internal textures and replaces the whole target, and transparency is lost. So nothing can be drawn over an eye image afterwards.
- The game's screen-space UI (menus, HUD, dialogue bubbles) stays in overlay-equivalent layout on the mod's own orthographic UI camera. Game code positions UI in screen pixels, and the "gloop" blob effect needs that too. Each eye image is placed as a full-screen background behind the UI on that camera, so one UI render produces "eye + UI", composited by the game's own UI path. The UI sits on the screen surface.
- Each eye renders to its own texture. Both are copied into one side-by-side texture, alpha is forced opaque, and it's shown on a SteamVR overlay with the `SideBySide_Parallel` flag.
- Orthographic views (the world map) are shown flat on the screen.
- The monitor keeps showing the game's normal 3D view, but without the UI, which now lives on the mod's UI camera.
- A dark backdrop goes to the compositor as the scene layer. Its `WaitGetPoses` pacing runs on Unity's render thread, so the game's main thread never blocks on SteamVR.
- TAA is swapped for FXAA, and motion blur is turned off. Both misbehave when rendering two views per frame.

## Install (already done on this machine)
1. Copy BepInEx 6 Bleeding Edge, the IL2CPP win-x64 build, into the game folder. Run the game once so it generates `BepInEx\interop`.
2. Run `dotnet build -c Release`. This copies `OlliVR.dll` and `openvr_api.dll` (taken from SteamVR) to `BepInEx\plugins\OlliVR\`. Use `-p:Deploy=false` to build while the game is running.

## Use
- Start the game with SteamVR running. The screen appears 2 m in front of you, and it recenters when you put the headset on.
- **Recenter**: press F8, or hold L3 + R3 for 1 s.
- Config file: `BepInEx\config\olliolliworld.vr.cfg`.

| Setting | Default | What it does |
|---|---|---|
| `ScreenDistance` | 2.0 | Distance to the screen in metres. |
| `ScreenScale` | 1.0 | Screen size. At 1.0 the screen covers the game camera's FOV exactly. |
| `ScreenHeightOffset` | 0 | Raise or lower the screen (metres). |
| `ScreenResolution` | 1080 | Per-eye vertical resolution. |
| `DepthStrength` | 1.0 | Eye separation multiplier. Higher means deeper 3D; 0 is flat. |
| `ConvergenceDistance` | 0 (auto) | Game-world distance that sits on the screen surface. Auto uses a raycast through the view centre and the Cinemachine target. |
| `HeadCoupled` | 1.0 | Fish-tank effect strength. 0 gives a fixed viewpoint. |
| `FlipVertical` | true | Toggle if the screen is upside down. |
| `VerboseLogging` | false | Camera, render and convergence diagnostics. |

An empty file `BepInEx\OlliVR_capture.trigger` makes the mod save the side-by-side image as `BepInEx\OlliVR_capture_Stereo.png`.

## Uninstall
Delete `winhttp.dll` from the game folder, or set `enabled = false` in `doorstop_config.ini`.
