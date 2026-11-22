// Cloud build entry point (GameCI: buildMethod BuildScript.BuildWebGL).
// 1. Makes sure URP is the active pipeline (a fresh CI checkout has no URP asset yet), 2. reimports the meadow .glb so
// glTFast picks its URP shaders, 3. runs Korajin > Build Meadow, 4. builds that scene for WebGL (uncompressed, so
// GitHub Pages can serve it without Content-Encoding headers).
// Also usable locally: Korajin > Build WebGL (writes Build/WebGL).
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Korajin.Bridge;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class BuildScript
{
    const string LOG = "[KorajinBuild] ";
    const string ZONE = "meadow";

    [MenuItem("Korajin/Build WebGL", false, 40)]
    public static void BuildWebGLMenu() { Run("Build/WebGL", false); }

    // GameCI calls this. It passes -customBuildPath <dir/name>; for WebGL that is the output folder.
    public static void BuildWebGL()
    {
        string path = Arg("-customBuildPath") ?? "build/WebGL/WebGL";
        Run(path, true);
    }

    static void Run(string outPath, bool exitOnFail)
    {
        try
        {
            Debug.Log(LOG + "Unity " + Application.unityVersion + " -> " + outPath);
            EnsureUrp();

            string glb = Directory.GetFiles(KorajinBridge.ZoneFolder(ZONE), "*.glb").FirstOrDefault();
            if (glb != null)
            {
                glb = glb.Replace('\\', '/');
                Debug.Log(LOG + "reimporting " + glb + " under URP");
                AssetDatabase.ImportAsset(glb, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }

            KorajinBridge.BuildZone(ZONE);
            string scene = KorajinBridge.RootFolder() + "/Generated/" + char.ToUpperInvariant(ZONE[0]) + ZONE.Substring(1) + ".unity";
            if (!File.Exists(scene)) throw new Exception("Build Meadow did not write " + scene);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scene, true) };

            ConfigureWebGL();

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = outPath,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None,
            };
            BuildReport report = BuildPipeline.BuildPlayer(opts);
            var s = report.summary;
            Debug.Log(LOG + "result " + s.result + ", " + (s.totalSize / (1024 * 1024)) + " MB, " + s.totalErrors + " errors, " + s.totalTime);
            if (s.result != BuildResult.Succeeded) throw new Exception("BuildPlayer: " + s.result);
            if (exitOnFail) EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError(LOG + "FAILED: " + e);
            if (exitOnFail) EditorApplication.Exit(1);
        }
    }

    // ---------------------------------------------------------------- URP

    static void EnsureUrp()
    {
        var current = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
        if (current == null)
        {
            const string folder = "Assets/Settings";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "Settings");

            string rdPath = folder + "/URP_Renderer.asset";
            string rpPath = folder + "/URP_Asset.asset";
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rdPath);
            if (rd == null)
            {
                rd = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rd, rdPath);
            }
            // Post-processing data (bloom / tonemap shaders). URP usually fills this itself; set it if empty.
            try
            {
                var so = new SerializedObject(rd);
                var p = so.FindProperty("postProcessData");
                if (p != null && p.objectReferenceValue == null)
                {
                    var ppd = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                        "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
                    if (ppd != null) { p.objectReferenceValue = ppd; so.ApplyModifiedPropertiesWithoutUndo(); }
                }
            }
            catch (Exception e) { Debug.LogWarning(LOG + "postProcessData: " + e.Message); }

            current = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(rpPath);
            if (current == null)
            {
                current = UniversalRenderPipelineAsset.Create(rd);
                AssetDatabase.CreateAsset(current, rpPath);
            }
            EditorUtility.SetDirty(rd);
            EditorUtility.SetDirty(current);
            AssetDatabase.SaveAssets();
            GraphicsSettings.defaultRenderPipeline = current;
            Debug.Log(LOG + "URP asset created and assigned: " + rpPath);
        }
        // Every quality level uses the same URP asset (WebGL picks its own default level).
        int keep = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = current;
        }
        QualitySettings.SetQualityLevel(keep, false);
        AssetDatabase.SaveAssets();
        Debug.Log(LOG + "render pipeline: " + GraphicsSettings.currentRenderPipeline);
    }

    // ---------------------------------------------------------------- WebGL player settings

    static void ConfigureWebGL()
    {
        PlayerSettings.productName = "Korajin Meadow";
        PlayerSettings.companyName = "Korajin";
        PlayerSettings.colorSpace = ColorSpace.Linear;
        // GitHub Pages sends no Content-Encoding for .br / .gz, so ship uncompressed files.
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.nameFilesAsHashes = true; // new names per build: no stale cache on the phone
        PlayerSettings.WebGL.dataCaching = true;
        // ASTC: what iPhone GPUs decode natively (best effort; the property name moved between versions).
        TrySetStatic(typeof(EditorUserBuildSettings), "webGLBuildSubtarget", "ASTC");
        Debug.Log(LOG + "WebGL: compression Disabled, linear colour, ASTC textures");
    }

    static void TrySetStatic(Type t, string prop, string enumName)
    {
        try
        {
            var p = t.GetProperty(prop, BindingFlags.Public | BindingFlags.Static);
            if (p == null) { Debug.Log(LOG + "(no " + t.Name + "." + prop + ")"); return; }
            p.SetValue(null, Enum.Parse(p.PropertyType, enumName));
        }
        catch (Exception e) { Debug.LogWarning(LOG + prop + ": " + e.Message); }
    }

    static string Arg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return null;
    }
}
