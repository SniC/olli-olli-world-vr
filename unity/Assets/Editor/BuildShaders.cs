using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the OlliVR shader bundle and copies it to ../shaders/ollivr_shaders (deployed by the mod's build).
/// Build with the game's exact editor version (2020.3.22f1): bundles from other versions are rejected.
///  * Command line: Unity.exe -quit -projectPath unity -executeMethod BuildShaders.Build
///  * Editor: runs automatically when the project opens and a shader is newer than the bundle,
///    or via the menu OlliVR > Build Shader Bundle.
/// </summary>
[InitializeOnLoad]
public static class BuildShaders
{
    const string BuildDir = "Build";
    const string BundleName = "ollivr_shaders";
    const string GameVersion = "2020.3.22f1";
    static readonly string[] Assets = { "Assets/Shaders/RoomLit.shader" };

    static BuildShaders()
    {
        if (Application.isBatchMode) return;
        EditorApplication.delayCall += () => { if (IsStale()) BuildInternal(); };
    }

    static string Target => Path.GetFullPath(Path.Combine("..", "shaders", BundleName));

    static bool IsStale()
    {
        if (!File.Exists(Target)) return true;
        var built = File.GetLastWriteTimeUtc(Target);
        foreach (var a in Assets)
            if (File.GetLastWriteTimeUtc(a) > built) return true;
        return File.GetLastWriteTimeUtc("Assets/Editor/BuildShaders.cs") > built;
    }

    [MenuItem("OlliVR/Build Shader Bundle")]
    public static void BuildMenu() => BuildInternal();

    public static void Build()
    {
        if (!BuildInternal()) EditorApplication.Exit(1);
    }

    static bool BuildInternal()
    {
        if (Application.unityVersion != GameVersion)
            Debug.LogWarning($"OlliVR: building with {Application.unityVersion}; the game ({GameVersion}) will likely reject this bundle.");
        Directory.CreateDirectory(BuildDir);
        var builds = new[] { new AssetBundleBuild { assetBundleName = BundleName, assetNames = Assets } };
        var manifest = BuildPipeline.BuildAssetBundles(BuildDir, builds,
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            Debug.LogError("OlliVR: bundle build failed");
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Target));
        File.Copy(Path.Combine(BuildDir, BundleName), Target, true);
        Debug.Log("OlliVR: built " + Target);
        return true;
    }
}
