using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace OlliVR
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "olliolliworld.vr";
        public const string Name = "OlliVR";
        public const string Version = "0.1.0";

        internal static ManualLogSource L;
        internal static VRConfig Cfg;
        internal static string PluginDir;

        public override void Load()
        {
            L = Log;
            Cfg = new VRConfig(Config);
            PluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // openvr_api.dll lives next to this plugin, not on the default DLL search path.
            NativeLibrary.SetDllImportResolver(typeof(Valve.VR.OpenVR).Assembly, (name, asm, path) =>
            {
                if (name == "openvr_api")
                {
                    var full = Path.Combine(PluginDir, "openvr_api.dll");
                    if (File.Exists(full)) return NativeLibrary.Load(full);
                }
                return IntPtr.Zero;
            });

            if (!Cfg.Enabled.Value)
            {
                L.LogInfo("OlliVR disabled in config.");
                return;
            }

            ClassInjector.RegisterTypeInIl2Cpp<VRRig>();

            var go = new GameObject("OlliVR_Rig");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<VRRig>();

            L.LogInfo($"{Name} {Version} loaded.");
        }
    }
}
