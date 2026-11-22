// Korajin > Beautiful Lighting -- run after Build Meadow. Quality URP settings for a desktop PC, SSAO,
// Adaptive Probe Volumes (baked bounce light), a tinted procedural sky, reflection probe, a soft "milky" grade.
// Then the user runs Window > Rendering > Lighting > Generate Lighting on his machine.
//
// Conservative-API rule: anything not 100% stable across Unity 6000.0 / URP 17 is set through SerializedObject
// (by serialized field name) or reflection, so a renamed member logs a warning instead of a compile error.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Korajin.Bridge
{
    public static class BeautifulLighting
    {
        const string LOG = "[Korajin/Lighting] ";

        [MenuItem("Korajin/Beautiful Lighting", false, 20)]
        public static void Run()
        {
            var scene = EditorSceneManager.GetActiveScene();
            string root = KorajinBridge.RootFolder();
            string gen = root + "/Generated";
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), gen));

            Dictionary<string, object> cfg = null;
            Step("read json", () =>
            {
                string folder = KorajinBridge.ZoneFolder("meadow");
                string abs = Path.Combine(Path.GetDirectoryName(Application.dataPath), folder);
                if (!Directory.Exists(abs)) return;
                foreach (var f in Directory.GetFiles(abs, "*.json"))
                {
                    cfg = MiniJson.Parse(File.ReadAllText(f)) as Dictionary<string, object>;
                    break;
                }
            });
            if (cfg == null) cfg = new Dictionary<string, object>();

            GameObject meadow = FindMeadowRoot(scene);
            if (meadow == null)
            {
                EditorUtility.DisplayDialog("Korajin", "Run Korajin > Build Meadow first (no 'meadow' object in the open scene).", "OK");
                return;
            }
            Camera cam = Camera.main;
            if (cam == null) cam = UnityEngine.Object.FindFirstObjectByType<Camera>();

            Bounds play = PlayBounds(cfg, meadow, cam);
            Debug.Log(LOG + "play area bounds center=" + play.center + " size=" + play.size);

            Step("URP asset quality", () => UrpQuality());
            Step("SSAO renderer feature", () => EnableSsao());
            Step("lights mixed + soft shadows", () => Lights(meadow));
            Step("static / contribute GI", () => StaticFlags(meadow));
            Step("sky", () => Sky(cfg, cam));
            Step("fog", () => Fog(cfg));
            Step("adaptive probe volume", () => ProbeVolumeFor(play));
            Step("reflection probe", () => ReflectionProbeFor(play));
            Step("lighting settings", () => LightingSettingsAsset(gen + "/Meadow_LightingSettings.lighting"));
            Step("volume", () => Grade(cfg, gen + "/meadow_BeautifulVolume.asset", cam));

            Step("save", () =>
            {
                EditorSceneManager.MarkSceneDirty(scene);
                if (!string.IsNullOrEmpty(scene.path)) EditorSceneManager.SaveScene(scene);
                else EditorSceneManager.SaveScene(scene, gen + "/Meadow.unity");
                AssetDatabase.SaveAssets();
            });

            Debug.Log(LOG + "DONE. Next: Window > Rendering > Lighting, then click 'Generate Lighting' (bottom right of that window).\n" +
                      "It bakes the bounce light into probes on YOUR PC: roughly 5-20 minutes on a decent GPU (progress bar at the bottom right of the editor).\n" +
                      "The Game view updates when it finishes. Then screenshot the Game view next to Korajin > Open Game Frame.");
            EditorUtility.DisplayDialog("Korajin - Beautiful Lighting",
                "Settings applied.\n\nNow: Window > Rendering > Lighting > Generate Lighting.\nThe bake runs on your PC, about 5-20 minutes (progress bar bottom right).\n\nThen screenshot the Game view.", "OK");
        }

        static void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception e) { Debug.LogWarning(LOG + "step '" + name + "' failed: " + e.Message + "\n" + e.StackTrace); }
        }

        // ---------------------------------------------------------------- helpers

        static GameObject FindMeadowRoot(UnityEngine.SceneManagement.Scene scene)
        {
            GameObject best = null;
            int bestCount = 0;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (go.name == "meadow") return go;
                int n = go.GetComponentsInChildren<Renderer>(true).Length;
                if (n > bestCount) { bestCount = n; best = go; }
            }
            return bestCount > 20 ? best : null;
        }

        /// The playable area: union of the sidecar's collider meshes (ground, banks, rocks...), else renderers near the camera.
        static Bounds PlayBounds(Dictionary<string, object> cfg, GameObject meadow, Camera cam)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coll = J.Get(cfg, "colliders.meshes") as List<object>;
            if (coll != null) foreach (var o in coll) { var s = o as string; if (s != null) names.Add(s); }

            bool have = false;
            Bounds b = new Bounds();
            foreach (var r in meadow.GetComponentsInChildren<Renderer>(true))
            {
                if (!names.Contains(r.gameObject.name) && !(r.transform.parent != null && names.Contains(r.transform.parent.name))) continue;
                if (r.bounds.size.magnitude > 1500f) continue;
                if (!have) { b = r.bounds; have = true; } else b.Encapsulate(r.bounds);
            }
            Vector3 c = cam != null ? cam.transform.position : Vector3.zero;
            if (!have)
            {
                foreach (var r in meadow.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.bounds.size.magnitude > 400f || Vector3.Distance(r.bounds.center, c) > 150f) continue;
                    if (!have) { b = r.bounds; have = true; } else b.Encapsulate(r.bounds);
                }
            }
            if (!have) b = new Bounds(c, new Vector3(160, 60, 160));
            // keep the bake sane: cap to 320 x 120 x 320 m around the centre, pad 4 m
            Vector3 size = b.size + new Vector3(8, 8, 8);
            size = new Vector3(Mathf.Min(size.x, 320f), Mathf.Clamp(size.y, 20f, 120f), Mathf.Min(size.z, 320f));
            return new Bounds(b.center, size);
        }

        static bool SetSO(SerializedObject so, string path, object value)
        {
            var p = so.FindProperty(path);
            if (p == null) { Debug.Log(LOG + "  (no field '" + path + "' on " + so.targetObject.GetType().Name + ")"); return false; }
            try
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Boolean: p.boolValue = Convert.ToBoolean(value); break;
                    case SerializedPropertyType.Integer: p.intValue = Convert.ToInt32(value); break;
                    case SerializedPropertyType.Float: p.floatValue = Convert.ToSingle(value); break;
                    case SerializedPropertyType.Enum: p.intValue = Convert.ToInt32(value); break;
                    case SerializedPropertyType.Vector3: p.vector3Value = (Vector3)value; break;
                    case SerializedPropertyType.Color: p.colorValue = (Color)value; break;
                    default: Debug.Log(LOG + "  (field '" + path + "' is " + p.propertyType + ", skipped)"); return false;
                }
                return true;
            }
            catch (Exception e) { Debug.Log(LOG + "  (field '" + path + "': " + e.Message + ")"); return false; }
        }

        /// Set a public property or field by name via reflection (enums accept ints or names).
        static bool SetMember(object obj, string name, object value)
        {
            if (obj == null) return false;
            var t = obj.GetType();
            try
            {
                var pi = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (pi != null && pi.CanWrite) { pi.SetValue(obj, Coerce(value, pi.PropertyType), null); return true; }
                var fi = t.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (fi != null) { fi.SetValue(obj, Coerce(value, fi.FieldType)); return true; }
            }
            catch (Exception e) { Debug.Log(LOG + "  (" + t.Name + "." + name + ": " + e.Message + ")"); return false; }
            Debug.Log(LOG + "  (no member " + t.Name + "." + name + ")");
            return false;
        }

        static object Coerce(object v, Type target)
        {
            if (v == null) return null;
            if (target.IsEnum)
            {
                var s = v as string;
                return s != null ? Enum.Parse(target, s, true) : Enum.ToObject(target, Convert.ToInt32(v));
            }
            if (target == typeof(float)) return Convert.ToSingle(v);
            if (target == typeof(int)) return Convert.ToInt32(v);
            if (target == typeof(bool)) return Convert.ToBoolean(v);
            return v;
        }

        // ---------------------------------------------------------------- URP asset

        static UniversalRenderPipelineAsset Urp()
        {
            return GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        }

        static void UrpQuality()
        {
            var asset = Urp();
            if (asset == null) { Debug.LogWarning(LOG + "no active URP asset."); return; }
            var so = new SerializedObject(asset);
            SetSO(so, "m_SupportsHDR", true);
            SetSO(so, "m_MSAA", 4);
            SetSO(so, "m_RenderScale", 1f);
            SetSO(so, "m_MainLightShadowsSupported", true);
            SetSO(so, "m_MainLightShadowmapResolution", 4096);
            SetSO(so, "m_AdditionalLightShadowsSupported", true);
            SetSO(so, "m_ShadowDistance", 140f);
            SetSO(so, "m_ShadowCascadeCount", 4);
            SetSO(so, "m_SoftShadowsSupported", true);
            SetSO(so, "m_SoftShadowQuality", 3);       // High
            SetSO(so, "m_ColorGradingMode", 1);        // HDR grading
            SetSO(so, "m_LightProbeSystem", 1);        // Adaptive Probe Volumes
            SetSO(so, "m_ProbeVolumeSHBands", 2);      // L2 for softer, richer bounce
            SetSO(so, "m_SupportProbeVolumeGPUStreaming", false);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            Debug.Log(LOG + "URP asset '" + asset.name + "': HDR, MSAA 4x, 4096 shadows, 4 cascades @140 m, soft shadows high, APV on.");
            if (QualitySettings.names.Length > 0)
                Debug.Log(LOG + "Quality level in use: " + QualitySettings.names[QualitySettings.GetQualityLevel()] + " (settings went to that level's URP asset).");
        }

        static void EnableSsao()
        {
            var asset = Urp();
            if (asset == null) return;
            var so = new SerializedObject(asset);
            var list = so.FindProperty("m_RendererDataList");
            if (list == null || !list.isArray) { Debug.LogWarning(LOG + "can't find renderer list on the URP asset."); return; }
            var ssaoType = typeof(UniversalRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
            if (ssaoType == null) { Debug.LogWarning(LOG + "SSAO feature type not found."); return; }

            for (int i = 0; i < list.arraySize; i++)
            {
                var data = list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererData;
                if (data == null) continue;
                ScriptableRendererFeature ssao = null;
                foreach (var f in data.rendererFeatures)
                    if (f != null && f.GetType() == ssaoType) { ssao = f; break; }

                if (ssao == null)
                {
                    ssao = ScriptableObject.CreateInstance(ssaoType) as ScriptableRendererFeature;
                    ssao.name = "ScreenSpaceAmbientOcclusion";
                    AssetDatabase.AddObjectToAsset(ssao, data);
                    data.rendererFeatures.Add(ssao);
                    // keep the renderer's feature map in sync (it stores local file ids)
                    string guid; long localId;
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ssao, out guid, out localId))
                    {
                        var dso = new SerializedObject(data);
                        var map = dso.FindProperty("m_RendererFeatureMap");
                        if (map != null && map.isArray)
                        {
                            map.arraySize++;
                            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                            dso.ApplyModifiedPropertiesWithoutUndo();
                        }
                    }
                    Debug.Log(LOG + "added SSAO to renderer '" + data.name + "'");
                }
                ssao.SetActive(true);
                var fso = new SerializedObject(ssao);
                SetSO(fso, "m_Settings.Intensity", 1.1f);
                SetSO(fso, "m_Settings.Radius", 0.6f);
                SetSO(fso, "m_Settings.DirectLightingStrength", 0.3f);
                SetSO(fso, "m_Settings.Falloff", 60f);
                SetSO(fso, "m_Settings.Downsample", false);
                fso.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(ssao);
                EditorUtility.SetDirty(data);
                Debug.Log(LOG + "SSAO on for renderer '" + data.name + "'");
            }
        }

        // ---------------------------------------------------------------- scene content

        static void Lights(GameObject meadow)
        {
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.type != LightType.Directional) continue;
                l.lightmapBakeType = LightmapBakeType.Mixed;   // realtime direct + baked bounce
                if (l == RenderSettings.sun)
                {
                    l.shadows = LightShadows.Soft;
                    l.shadowStrength = 0.9f;                   // a little sky fill in the shadows
                }
                Debug.Log(LOG + "light '" + l.name + "' -> Mixed" + (l == RenderSettings.sun ? " (sun, soft shadows)" : ""));
            }
        }

        static void StaticFlags(GameObject meadow)
        {
            int n = 0, skipped = 0;
            foreach (var r in meadow.GetComponentsInChildren<Renderer>(true))
            {
                string nm = r.gameObject.name.ToLowerInvariant();
                // moving / far stuff: clouds, sky-scale far ring, skinned bodies
                bool dynamic = r is SkinnedMeshRenderer || nm.Contains("cloud") || nm.Contains("mdw_far") || r.bounds.size.magnitude > 1500f;
                if (dynamic) { skipped++; continue; }
                var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                flags |= StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic;
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags);
                // The glb has no lightmap UVs: receive bounce light from the probe volume instead of lightmaps.
                var mr = r as MeshRenderer;
                if (mr != null) mr.receiveGI = ReceiveGI.LightProbes;
                r.lightProbeUsage = LightProbeUsage.BlendProbes;
                n++;
            }
            Debug.Log(LOG + n + " renderers marked Contribute GI (receive GI from probes), " + skipped + " left dynamic (clouds / far / skinned).");
        }

        static void Sky(Dictionary<string, object> cfg, Camera cam)
        {
            var sh = Shader.Find("Skybox/Procedural");
            if (sh == null) { Debug.LogWarning(LOG + "Skybox/Procedural shader not found."); return; }
            var m = new Material(sh);
            m.name = "Meadow_Sky";
            Color fogC, ground;
            if (!J.TryColor(J.Get(cfg, "fog.color"), out fogC)) fogC = new Color(0.61f, 0.78f, 0.92f);
            if (!J.TryColor(J.Get(cfg, "hemi.ground"), out ground)) ground = new Color(0.55f, 0.6f, 0.5f);
            // Procedural sky tint: 0.5 grey = neutral; push it toward the game's sky blue
            Color tint = Color.Lerp(new Color(0.5f, 0.5f, 0.5f), fogC, 0.7f);
            if (m.HasProperty("_SkyTint")) m.SetColor("_SkyTint", tint);
            if (m.HasProperty("_GroundColor")) m.SetColor("_GroundColor", Color.Lerp(ground, fogC, 0.5f));
            if (m.HasProperty("_AtmosphereThickness")) m.SetFloat("_AtmosphereThickness", 0.75f); // milky, pale
            if (m.HasProperty("_Exposure")) m.SetFloat("_Exposure", 1.25f);
            if (m.HasProperty("_SunSize")) m.SetFloat("_SunSize", 0.035f);
            if (m.HasProperty("_SunSizeConvergence")) m.SetFloat("_SunSizeConvergence", 4f);
            string path = KorajinBridge.RootFolder() + "/Generated/Meadow_Sky.mat";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(m, path);

            RenderSettings.skybox = m;
            RenderSettings.ambientMode = AmbientMode.Skybox;   // ambient + baked GI see the sky colour
            RenderSettings.ambientIntensity = 1.1f;
            RenderSettings.reflectionIntensity = 0.6f;
            if (cam != null) cam.clearFlags = CameraClearFlags.Skybox;
            try { DynamicGI.UpdateEnvironment(); } catch { }
            Debug.Log(LOG + "procedural sky tint=" + tint + "; ambient from sky.");
        }

        static void Fog(Dictionary<string, object> cfg)
        {
            Color c;
            if (J.TryColor(J.Get(cfg, "fog.color"), out c)) RenderSettings.fogColor = c;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0022f;   // a touch of aerial haze: ~25% at 200 m, thick past 600 m
            Debug.Log(LOG + "fog exp2 density " + RenderSettings.fogDensity);
        }

        static void ProbeVolumeFor(Bounds play)
        {
            var t = typeof(Volume).Assembly.GetType("UnityEngine.Rendering.ProbeVolume");
            if (t == null) { Debug.LogWarning(LOG + "ProbeVolume type not found (needs Unity 6 / SRP Core 17)."); return; }
            foreach (var old in UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None))
            {
                var oc = old as Component;
                if (oc != null && oc.gameObject.name == "Korajin Probe Volume") UnityEngine.Object.DestroyImmediate(oc.gameObject);
            }
            var go = new GameObject("Korajin Probe Volume");
            go.transform.position = play.center;
            var pv = go.AddComponent(t);
            var so = new SerializedObject(pv);
            SetSO(so, "mode", 2);              // Local (0 Global, 1 Scene, 2 Local)
            SetSO(so, "size", play.size);
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log(LOG + "Adaptive Probe Volume " + play.size + " at " + play.center);
        }

        static void ReflectionProbeFor(Bounds play)
        {
            var old = GameObject.Find("Korajin Reflection Probe");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var go = new GameObject("Korajin Reflection Probe");
            go.transform.position = play.center + Vector3.up * 2f;
            var rp = go.AddComponent<ReflectionProbe>();
            rp.mode = ReflectionProbeMode.Baked;
            rp.size = play.size;
            rp.resolution = 256;
            rp.boxProjection = false;
            rp.intensity = 0.8f;
            Debug.Log(LOG + "baked reflection probe " + play.size);
        }

        static void LightingSettingsAsset(string path)
        {
            var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            bool isNew = ls == null;
            if (isNew) ls = new LightingSettings();
            ls.name = "Meadow_LightingSettings";
            SetMember(ls, "bakedGI", true);
            SetMember(ls, "realtimeGI", false);
            SetMember(ls, "mixedBakeMode", "IndirectOnly");
            SetMember(ls, "lightmapper", "ProgressiveGPU");
            SetMember(ls, "maxBounces", 4);
            SetMember(ls, "indirectScale", 1.25f);   // milky, generous bounce
            SetMember(ls, "albedoBoost", 1.15f);
            SetMember(ls, "directSampleCount", 32);
            SetMember(ls, "indirectSampleCount", 256);
            SetMember(ls, "environmentSampleCount", 256);
            SetMember(ls, "lightmapResolution", 2f);  // only matters if something gets lightmaps; APV does the rest
            SetMember(ls, "autoGenerate", false);
            if (isNew) AssetDatabase.CreateAsset(ls, path);
            EditorUtility.SetDirty(ls);
            Lightmapping.lightingSettings = ls;
            Debug.Log(LOG + "lighting settings: baked GI, GPU lightmapper, 4 bounces, indirect x1.25 -> " + path);
        }

        // ---------------------------------------------------------------- the grade

        static void Grade(Dictionary<string, object> cfg, string profilePath, Camera cam)
        {
            foreach (var v in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(v.gameObject);

            AssetDatabase.DeleteAsset(profilePath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);

            double exposure;
            if (!J.TryNum(J.Get(cfg, "renderer.exposure"), out exposure) || exposure <= 0) exposure = 1;

            try
            {
                var tm = profile.Add<Tonemapping>(true);
                tm.mode.Override(TonemappingMode.ACES);
                AssetDatabase.AddObjectToAsset(tm, profile);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "tonemapping: " + e.Message); }

            try
            {
                var ca = profile.Add<ColorAdjustments>(true);
                ca.postExposure.Override(Mathf.Log((float)exposure, 2f) + 0.15f);
                ca.contrast.Override(-12f);       // film-like soft contrast
                ca.saturation.Override(8f);       // gentle colour
                AssetDatabase.AddObjectToAsset(ca, profile);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "color adjustments: " + e.Message); }

            try
            {
                var b = profile.Add<Bloom>(true);
                b.intensity.Override(0.55f);
                b.threshold.Override(0.85f);
                b.scatter.Override(0.8f);         // wide, milky glow
                b.tint.Override(new Color(1f, 0.96f, 0.9f));
                AssetDatabase.AddObjectToAsset(b, profile);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "bloom: " + e.Message); }

            try
            {
                var wb = profile.Add<WhiteBalance>(true);
                wb.temperature.Override(6f);      // a hair warm
                AssetDatabase.AddObjectToAsset(wb, profile);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "white balance: " + e.Message); }

            try
            {
                var st = profile.Add<SplitToning>(true);
                st.shadows.Override(new Color(0.55f, 0.6f, 0.75f));    // cool, lifted shade
                st.highlights.Override(new Color(0.8f, 0.72f, 0.6f));  // warm light
                st.balance.Override(10f);
                AssetDatabase.AddObjectToAsset(st, profile);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "split toning: " + e.Message); }

            try
            {
                var vg = profile.Add<Vignette>(true);
                vg.intensity.Override(0.16f);
                vg.smoothness.Override(0.5f);
                AssetDatabase.AddObjectToAsset(vg, profile);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "vignette: " + e.Message); }

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var go = new GameObject("Global Volume (Beautiful)");
            var vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 1;
            vol.sharedProfile = profile;

            if (cam != null)
            {
                try
                {
                    var data = cam.GetUniversalAdditionalCameraData();
                    data.renderPostProcessing = true;
                    data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                    data.antialiasingQuality = AntialiasingQuality.High;
                }
                catch (Exception e) { Debug.LogWarning(LOG + "camera: " + e.Message); }
            }
            Debug.Log(LOG + "grade: ACES, soft contrast -12, sat +8, wide bloom, warm WB, split toning, light vignette, SMAA.");
        }
    }
}
