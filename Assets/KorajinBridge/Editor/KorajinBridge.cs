// Korajin Bridge -- one-click import of a web-game zone snapshot (.glb + sidecar .json) into a URP scene.
// Assumed: Unity 6000.0 LTS, URP 17.0.x, glTFast 6.x (com.unity.cloud.gltfast) as the .glb importer.
// Every optional step is wrapped in try/catch so one failure never stops the scene from being built.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Korajin.Bridge
{
    public static class KorajinBridge
    {
        const string LOG = "[Korajin] ";
        const string ZONE = "meadow";

        // ---------------------------------------------------------------- paths

        /// Folder the user dropped in (normally "Assets/KorajinBridge"), found from this script's location.
        public static string RootFolder()
        {
            try
            {
                foreach (var guid in AssetDatabase.FindAssets("KorajinBridge t:MonoScript"))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
                    if (p.EndsWith("/Editor/KorajinBridge.cs"))
                        return p.Substring(0, p.Length - "/Editor/KorajinBridge.cs".Length);
                }
            }
            catch (Exception e) { Debug.LogWarning(LOG + "root lookup: " + e.Message); }
            return "Assets/KorajinBridge";
        }

        public static string ZoneFolder(string zone) { return RootFolder() + "/Exports/" + zone; }

        static string FindFile(string folder, Func<string, bool> pred)
        {
            string abs = AssetToAbs(folder);
            if (!Directory.Exists(abs)) return null;
            var files = Directory.GetFiles(abs);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (pred(name.ToLowerInvariant())) return folder + "/" + name;
            }
            return null;
        }

        static string AssetToAbs(string assetPath)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(projectRoot, assetPath);
        }

        static void EnsureFolder(string assetPath)
        {
            Directory.CreateDirectory(AssetToAbs(assetPath));
        }

        public static string GameFramePath(string zone)
        {
            string folder = ZoneFolder(zone);
            return FindFile(folder, n => n.EndsWith("_game.jpg") || n.EndsWith("_game.png"))
                ?? FindFile(folder, n => n.EndsWith(".jpg") || n.EndsWith(".png"));
        }

        // ---------------------------------------------------------------- menu

        [MenuItem("Korajin/Build Meadow", false, 1)]
        public static void BuildMeadowMenu() { BuildZone(ZONE); }

        [MenuItem("Korajin/Open Game Frame", false, 2)]
        public static void OpenGameFrameMenu() { GameFrameWindow.Open(ZONE); }

        public static void BuildZone(string zone)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            string folder = ZoneFolder(zone);
            string glbPath = FindFile(folder, n => n.EndsWith(".glb") || n.EndsWith(".gltf"));
            string jsonPath = FindFile(folder, n => n.EndsWith(".json"));
            Debug.Log(LOG + "Building zone '" + zone + "' from " + folder + "  glb=" + glbPath + "  json=" + jsonPath);
            if (glbPath == null)
            {
                EditorUtility.DisplayDialog("Korajin", "No .glb found in " + folder + ".\nPut the export there and try again.", "OK");
                return;
            }

            string genFolder = RootFolder() + "/Generated";
            EnsureFolder(genFolder);
            EnsureFolder(genFolder + "/Materials_" + zone);
            AssetDatabase.Refresh();

            // ---- sidecar json
            Dictionary<string, object> cfg = null;
            Step("read json", () =>
            {
                if (jsonPath == null) { Debug.LogWarning(LOG + "No sidecar .json; using defaults."); return; }
                cfg = MiniJson.Parse(File.ReadAllText(AssetToAbs(jsonPath))) as Dictionary<string, object>;
                if (cfg != null) Debug.Log(LOG + "json top-level keys: " + string.Join(", ", new List<string>(cfg.Keys).ToArray()));
            });
            if (cfg == null) cfg = new Dictionary<string, object>();

            // ---- new scene
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ---- import model
            GameObject root = null;
            Step("instantiate glb", () =>
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
                if (prefab == null)
                {
                    AssetDatabase.ImportAsset(glbPath, ImportAssetOptions.ForceUpdate);
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
                }
                if (prefab == null) throw new Exception("glb did not import as a GameObject. Is glTFast (com.unity.cloud.gltfast) installed?");
                root = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (root == null) root = UnityEngine.Object.Instantiate(prefab);
                root.name = zone;
                // Unpack so material swaps are plain scene edits, not prefab overrides on a read-only importer asset.
                try { PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction); } catch { }
            });

            Camera cam = Camera.main;
            if (cam == null) cam = UnityEngine.Object.FindFirstObjectByType<Camera>();
            Light sun = null;
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && (root == null || !l.transform.IsChildOf(root.transform))) { sun = l; break; }

            Step("lights", () => SetupLights(cfg, root, ref sun));
            Step("ambient", () => SetupAmbient(cfg));
            Step("fog", () => SetupFog(cfg));
            Step("camera", () => SetupCamera(cfg, root, cam));
            Step("imported cameras off", () => DisableImported(root, cam, sun));
            Step("materials", () => ConvertMaterials(cfg, root, genFolder + "/Materials_" + zone));
            Step("volume", () => SetupVolume(cfg, genFolder + "/" + zone + "_Volume.asset", cam));
            Step("shadow distance", () => SetupShadowDistance(cfg, root));

            Step("save scene", () =>
            {
                string scenePath = genFolder + "/" + Capitalize(zone) + ".unity";
                EditorSceneManager.SaveScene(scene, scenePath);
                AssetDatabase.SaveAssets();
                Debug.Log(LOG + "Saved " + scenePath);
            });
            Step("frame scene view", () =>
            {
                if (SceneView.lastActiveSceneView != null && cam != null)
                    SceneView.lastActiveSceneView.AlignViewToObject(cam.transform);
            });
            Step("game view aspect", () => GameViewAspect.TrySet(cfg));

            Debug.Log(LOG + "Done. Look at the Game view (or press Play) and compare with Korajin > Open Game Frame.");
        }

        static void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception e) { Debug.LogWarning(LOG + "step '" + name + "' failed: " + e.Message + "\n" + e.StackTrace); }
        }

        static string Capitalize(string s) { return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        // ---------------------------------------------------------------- coordinate conversion
        // three.js / glTF are right-handed; glTFast converts by negating X (pos) and (x,-y,-z,w) (rot).

        static Vector3 ConvPos(Vector3 v) { return new Vector3(-v.x, v.y, v.z); }
        static Quaternion ConvRot(Quaternion q) { return new Quaternion(q.x, -q.y, -q.z, q.w); }

        // three.js Euler (radians, order XYZ) -> three-space quaternion
        static Quaternion ThreeEulerXYZ(Vector3 e)
        {
            float c1 = Mathf.Cos(e.x / 2), c2 = Mathf.Cos(e.y / 2), c3 = Mathf.Cos(e.z / 2);
            float s1 = Mathf.Sin(e.x / 2), s2 = Mathf.Sin(e.y / 2), s3 = Mathf.Sin(e.z / 2);
            return new Quaternion(
                s1 * c2 * c3 + c1 * s2 * s3,
                c1 * s2 * c3 - s1 * c2 * s3,
                c1 * c2 * s3 + s1 * s2 * c3,
                c1 * c2 * c3 - s1 * s2 * s3);
        }

        // ---------------------------------------------------------------- lighting

        // three.js r155+ (this export: r180) uses physically based light units: diffuse = albedo/PI * E.
        // URP's Lit has no 1/PI, so a three intensity maps to Unity intensity / PI.
        static readonly float THREE_TO_UNITY = 1f / Mathf.PI;

        static void SetupLights(Dictionary<string, object> cfg, GameObject root, ref Light sun)
        {
            var sd = J.Get(cfg, "sun", "lights.sun", "light.sun") as Dictionary<string, object>;
            var lights = J.Get(cfg, "lights") as List<object>;

            // directional lights the glb brought in (KHR_lights_punctual via glTFast)
            var imported = new List<Light>();
            if (root != null)
                foreach (var l in root.GetComponentsInChildren<Light>(true))
                    if (l.type == LightType.Directional) imported.Add(l);

            Light chosenSun = null;
            foreach (var l in imported)
            {
                var entry = FindLightEntry(lights, l.name, l.transform.parent != null ? l.transform.parent.name : null);
                l.shadows = LightShadows.None;
                if (entry != null)
                {
                    Color c; double i;
                    if (J.TryColor(J.Get(entry, "color"), out c)) l.color = c;
                    if (J.TryNum(J.Get(entry, "intensity"), out i)) l.intensity = (float)i * THREE_TO_UNITY;
                    object sh = J.Get(entry, "shadow");
                    if ((sh is bool && (bool)sh) || (sh is double && (double)sh > 0)) chosenSun = l;
                }
                Debug.Log(LOG + "imported directional '" + l.name + "' intensity=" + l.intensity + (entry == null ? " (no json match)" : ""));
            }

            if (chosenSun != null)
            {
                // drop the template's default light; the export carries its own sun
                if (sun != null && sun != chosenSun) UnityEngine.Object.DestroyImmediate(sun.gameObject);
                sun = chosenSun;
            }
            else if (sun == null)
            {
                var go = new GameObject("Sun");
                sun = go.AddComponent<Light>();
                sun.type = LightType.Directional;
            }
            else if (imported.Count > 0)
            {
                // imported lights exist but none flagged as the shadow caster: keep the default as the sun, at json values
                Debug.Log(LOG + "no imported light flagged shadow:true; using the scene's light as the sun.");
            }

            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 1f;
            sun.enabled = true;
            RenderSettings.sun = sun;

            if (sd == null) { Debug.LogWarning(LOG + "no sun block in json."); return; }
            Color sc; double si;
            if (J.TryColor(J.Get(sd, "color", "colour"), out sc)) sun.color = sc;
            if (J.TryNum(J.Get(sd, "intensity"), out si)) sun.intensity = (float)si * THREE_TO_UNITY;

            // direction the light travels: target - position (three.js); the sidecar 'dir' is the same vector, normalised
            Vector3 pos, tgt, travel = Vector3.zero;
            if (J.TryVec3(J.Get(sd, "position", "pos"), out pos) && J.TryVec3(J.Get(sd, "target", "tgt"), out tgt)) travel = tgt - pos;
            else if (J.TryVec3(J.Get(sd, "dir", "direction"), out travel)) { }
            if (travel.sqrMagnitude > 1e-8f)
                sun.transform.rotation = Quaternion.LookRotation(ConvPos(travel).normalized, Vector3.up);

            var shd = J.Get(sd, "shadow") as Dictionary<string, object>;
            if (shd != null)
            {
                double b;
                if (J.TryNum(J.Get(shd, "normalBias"), out b)) sun.shadowNormalBias = Mathf.Clamp((float)b * 10f, 0f, 3f);
            }
            if (sun.transform.parent != null && root != null && sun.transform.IsChildOf(root.transform)) sun.name = sun.name + " (Sun)";
            else sun.name = "Sun";
            Debug.Log(LOG + "sun '" + sun.name + "' color=" + sun.color + " intensity=" + sun.intensity + " fwd=" + sun.transform.forward);
        }

        static Dictionary<string, object> FindLightEntry(List<object> lights, string name, string parentName)
        {
            if (lights == null) return null;
            foreach (var o in lights)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string n = J.Get(d, "name") as string;
                if (n == null) continue;
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || string.Equals(n, parentName, StringComparison.OrdinalIgnoreCase)) return d;
            }
            return null;
        }

        static void AddLinear(ref Color acc, Color srgb, float k)
        {
            Color lin = srgb.linear;
            acc.r += lin.r * k; acc.g += lin.g * k; acc.b += lin.b * k;
        }

        static void SetupAmbient(Dictionary<string, object> cfg)
        {
            // sum every HemisphereLight (in linear), then /PI like the direct lights
            Color sky = Color.black, ground = Color.black;
            int found = 0;
            var lights = J.Get(cfg, "lights") as List<object>;
            if (lights != null)
            {
                foreach (var o in lights)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !string.Equals(J.Get(d, "type") as string, "HemisphereLight", StringComparison.OrdinalIgnoreCase)) continue;
                    Color s, g; double k;
                    float kf = J.TryNum(J.Get(d, "intensity"), out k) ? (float)k : 1f;
                    if (J.TryColor(J.Get(d, "color", "sky"), out s)) AddLinear(ref sky, s, kf * THREE_TO_UNITY);
                    if (J.TryColor(J.Get(d, "ground", "groundColor"), out g)) AddLinear(ref ground, g, kf * THREE_TO_UNITY);
                    found++;
                }
            }
            if (found == 0)
            {
                var h = J.Get(cfg, "hemi", "hemisphere") as Dictionary<string, object>;
                if (h == null) { Debug.LogWarning(LOG + "no hemisphere light in json."); return; }
                Color s, g; double k;
                float kf = J.TryNum(J.Get(h, "intensity"), out k) ? (float)k : 1f;
                if (J.TryColor(J.Get(h, "sky", "color"), out s)) AddLinear(ref sky, s, kf * THREE_TO_UNITY);
                if (J.TryColor(J.Get(h, "ground"), out g)) AddLinear(ref ground, g, kf * THREE_TO_UNITY);
            }

            // the game's environment map (PMREM) adds diffuse light too; approximate it with the fog/sky colour
            Color env = Color.black;
            double ei;
            var envd = J.Get(cfg, "environment") as Dictionary<string, object>;
            if (envd != null && J.TryNum(J.Get(envd, "intensity"), out ei) && ei > 0)
            {
                Color skyCol = RenderSettings.fogColor;
                Color fc;
                if (J.TryColor(J.Get(cfg, "fog.color"), out fc)) skyCol = fc;
                AddLinear(ref env, skyCol, (float)ei);
            }

            Color skyL = sky + env, groundL = ground + env * 0.6f;
            Color eqL = (skyL + groundL) * 0.5f;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = ToGamma(skyL);
            RenderSettings.ambientEquatorColor = ToGamma(eqL);
            RenderSettings.ambientGroundColor = ToGamma(groundL);
            RenderSettings.ambientIntensity = 1f;
            Debug.Log(LOG + "ambient trilight (linear) sky=" + skyL + " eq=" + eqL + " ground=" + groundL + " (" + found + " hemi lights + env)");
        }

        static Color ToGamma(Color lin)
        {
            // Color.gamma clamps poorly above 1 on some versions; scale into range first
            float m = Mathf.Max(1f, lin.maxColorComponent);
            Color g = new Color(lin.r / m, lin.g / m, lin.b / m, 1f).gamma;
            return new Color(g.r * m, g.g * m, g.b * m, 1f);
        }

        static void SetupFog(Dictionary<string, object> cfg)
        {
            object f = J.Get(cfg, "fog", "scene.fog", "atmosphere.fog");
            var fd = f as Dictionary<string, object>;
            if (fd == null) { RenderSettings.fog = false; Debug.Log(LOG + "no fog in json."); return; }
            Color c;
            if (J.TryColor(J.Get(fd, "color", "colour"), out c)) RenderSettings.fogColor = c;
            double near, far, dens;
            if (J.TryNum(J.Get(fd, "near", "start"), out near) | J.TryNum(J.Get(fd, "far", "end"), out far))
            {
                RenderSettings.fogMode = FogMode.Linear;
                if (J.TryNum(J.Get(fd, "near", "start"), out near)) RenderSettings.fogStartDistance = (float)near;
                if (J.TryNum(J.Get(fd, "far", "end"), out far)) RenderSettings.fogEndDistance = (float)far;
            }
            else if (J.TryNum(J.Get(fd, "density"), out dens))
            {
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogDensity = (float)dens;
            }
            RenderSettings.fog = true;
            Debug.Log(LOG + "fog " + RenderSettings.fogMode + " " + RenderSettings.fogStartDistance + ".." + RenderSettings.fogEndDistance);
        }

        // ---------------------------------------------------------------- camera

        static void SetupCamera(Dictionary<string, object> cfg, GameObject root, Camera cam)
        {
            if (cam == null) { var go = new GameObject("Main Camera"); go.tag = "MainCamera"; cam = go.AddComponent<Camera>(); }

            // background
            Color bg;
            if (J.TryColor(J.Get(cfg, "background", "bg", "backgroundColor", "clearColor"), out bg))
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = bg;
            }
            else if (RenderSettings.fog)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = RenderSettings.fogColor;
            }

            bool placed = false;
            float fov = -1;

            // 1) the sidecar's camera block (three.js world space: pos + quat, camera looks down -Z)
            var cd = J.Get(cfg, "camera", "cam_main", "camMain", "cam") as Dictionary<string, object>;
            if (cd != null)
            {
                double d;
                if (J.TryNum(J.Get(cd, "fov", "fovY", "vfov"), out d)) fov = (float)d;
                if (J.TryNum(J.Get(cd, "near"), out d) && d > 0) cam.nearClipPlane = (float)d;
                if (J.TryNum(J.Get(cd, "far"), out d) && d > 0) cam.farClipPlane = (float)d;
                Vector3 p, t, e; Quaternion q;
                if (J.TryVec3(J.Get(cd, "pos", "position", "p"), out p))
                {
                    cam.transform.position = ConvPos(p);
                    if (J.TryQuat(J.Get(cd, "quat", "quaternion", "q"), out q))
                        cam.transform.rotation = ConvRot(q) * Quaternion.Euler(0, 180, 0);
                    else if (J.TryVec3(J.Get(cd, "target", "lookAt", "tgt"), out t))
                        cam.transform.LookAt(ConvPos(t), Vector3.up);
                    else if (J.TryVec3(J.Get(cd, "rotation", "euler", "rot"), out e))
                        cam.transform.rotation = ConvRot(ThreeEulerXYZ(e)) * Quaternion.Euler(0, 180, 0);
                    placed = true;
                    Debug.Log(LOG + "camera from json: pos=" + cam.transform.position + " fwd=" + cam.transform.forward);
                }
            }

            // 2) fallback: the cam_main node in the glb
            Transform node = root != null ? FindDeep(root.transform, "cam_main") : null;
            if (!placed && node != null)
            {
                var ic = node.GetComponentInChildren<Camera>(true);
                if (ic != null)
                {
                    cam.transform.SetPositionAndRotation(ic.transform.position, ic.transform.rotation);
                    if (fov <= 0) fov = ic.fieldOfView;
                }
                else cam.transform.SetPositionAndRotation(node.position, node.rotation * Quaternion.Euler(0, 180, 0));
                placed = true;
                Debug.Log(LOG + "camera from glb node cam_main");
            }
            if (fov > 1 && fov < 179) cam.fieldOfView = fov;
            if (cam.farClipPlane < 500f) cam.farClipPlane = 2000f;
            if (!placed) Debug.LogWarning(LOG + "no cam_main found (json or glb); camera left at default.");
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase)) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        static void DisableImported(GameObject root, Camera main, Light sun)
        {
            if (root == null) return;
            foreach (var c in root.GetComponentsInChildren<Camera>(true))
                if (c != main) { c.enabled = false; }
        }

        // ---------------------------------------------------------------- materials (SHADER TRICKS -> URP)

        class MatInfo { public bool lightstyle; public float lsK; public List<string> tags = new List<string>(); }

        // lightstyle is a global switch in the game (look.lightStyle: pick != "PBR" and k > 0) applied to materials tagged lsPatch / lightstyle.
        static float LightStyleK(Dictionary<string, object> cfg)
        {
            var ls = J.Get(cfg, "look.lightStyle") as Dictionary<string, object>;
            if (ls == null) return 0f;
            string pick = J.Get(ls, "pick") as string;
            double k;
            if (!J.TryNum(J.Get(ls, "k"), out k)) return 0f;
            if (pick != null && pick.ToUpperInvariant() == "PBR") return 0f;
            return (float)k;
        }

        static MatInfo InfoFor(Dictionary<string, object> cfg, string matName, float lsK)
        {
            var info = new MatInfo();
            var md = J.Get(cfg, "materials") as Dictionary<string, object>;
            if (md != null && matName != null)
            {
                object e;
                Dictionary<string, object> entry = null;
                if (md.TryGetValue(matName, out e)) entry = e as Dictionary<string, object>;
                else
                {
                    // glTFast may tweak names; match by the sidecar's 'out' name too
                    foreach (var kv in md)
                    {
                        var d = kv.Value as Dictionary<string, object>;
                        if (d != null && string.Equals(J.Get(d, "out") as string, matName, StringComparison.OrdinalIgnoreCase)) { entry = d; break; }
                    }
                }
                var tags = entry != null ? J.Get(entry, "tags") as List<object> : null;
                if (tags != null) foreach (var t in tags) { var s = t as string; if (s != null) info.tags.Add(s.ToLowerInvariant()); }
            }
            if (lsK > 0 && (info.tags.Contains("lspatch") || info.tags.Contains("lightstyle"))) { info.lightstyle = true; info.lsK = lsK; }
            return info;
        }

        static readonly string[] BaseTexNames = { "baseColorTexture", "_BaseMap", "_MainTex", "baseColorTex" };
        static readonly string[] BaseColNames = { "baseColorFactor", "_BaseColor", "_Color" };
        static readonly string[] EmisColNames = { "emissiveFactor", "_EmissionColor" };
        static readonly string[] EmisTexNames = { "emissiveTexture", "_EmissionMap" };
        static readonly string[] NormalNames = { "normalTexture", "_BumpMap" };
        static readonly string[] MetalNames = { "metallicFactor", "_Metallic" };
        static readonly string[] RoughNames = { "roughnessFactor" };

        static string FirstProp(Material m, string[] names)
        {
            foreach (var n in names) if (m.HasProperty(n)) return n;
            return null;
        }

        static void ConvertMaterials(Dictionary<string, object> cfg, GameObject root, string matFolder)
        {
            if (root == null) return;
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) Debug.LogWarning(LOG + "URP Lit shader not found; only tweaking glTFast materials.");
            var cache = new Dictionary<string, Material>();
            int converted = 0, kept = 0, failed = 0, lsCount = 0;
            float lsK = LightStyleK(cfg);
            Debug.Log(LOG + "lightstyle k = " + lsK + (lsK > 0 ? "" : " (off in this export: look.lightStyle.pick is PBR or k=0)"));

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                bool hasVC = false;
                try
                {
                    Mesh mesh = null;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                    var smr = r as SkinnedMeshRenderer;
                    if (smr != null) mesh = smr.sharedMesh;
                    if (mesh != null) hasVC = mesh.HasVertexAttribute(VertexAttribute.Color);
                }
                catch { }

                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    if (src == null) continue;
                    string key = src.GetInstanceID() + (hasVC ? "_vc" : "");
                    Material dst;
                    if (!cache.TryGetValue(key, out dst))
                    {
                        try
                        {
                            var info = InfoFor(cfg, src.name, lsK);
                            if (info.lightstyle) lsCount++;
                            // Transparent, unlit (MeshBasic) and vertex-coloured stay on glTFast's shader graphs:
                            // they already render in URP, and URP Lit would drop blending / unlit / vertex colours.
                            bool transparent = src.renderQueue >= 2900;
                            bool unlit = src.shader != null && src.shader.name.ToLowerInvariant().Contains("unlit");
                            bool useLit = lit != null && !hasVC && !transparent && !unlit;
                            dst = useLit ? ToUrpLit(src, lit, info) : TweakCopy(src, info);
                            string safe = MakeSafe(src.name) + (hasVC ? "_vc" : "");
                            string path = AssetDatabase.GenerateUniqueAssetPath(matFolder + "/" + safe + ".mat");
                            AssetDatabase.CreateAsset(dst, path);
                            if (useLit) converted++; else kept++;
                        }
                        catch (Exception e)
                        {
                            failed++;
                            Debug.LogWarning(LOG + "material '" + src.name + "' kept as-is: " + e.Message);
                            dst = src;
                        }
                        cache[key] = dst;
                    }
                    if (dst != src) { mats[i] = dst; changed = true; }
                }
                if (changed) r.sharedMaterials = mats;
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows = true;
            }
            Debug.Log(LOG + "materials: " + converted + " -> URP Lit, " + kept + " kept on glTFast shader (vertex colour / transparent / unlit), " + lsCount + " lightstyle, " + failed + " failed.");
        }

        static string MakeSafe(string n)
        {
            if (string.IsNullOrEmpty(n)) n = "mat";
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n.Replace('/', '_').Replace(':', '_');
        }

        static Material ToUrpLit(Material src, Shader lit, MatInfo info)
        {
            var m = new Material(lit);
            m.name = src.name;

            string p;
            Texture baseTex = null;
            Color baseCol = Color.white;
            if ((p = FirstProp(src, BaseTexNames)) != null)
            {
                baseTex = src.GetTexture(p);
                m.SetTexture("_BaseMap", baseTex);
                try { m.SetTextureScale("_BaseMap", src.GetTextureScale(p)); m.SetTextureOffset("_BaseMap", src.GetTextureOffset(p)); } catch { }
            }
            if ((p = FirstProp(src, BaseColNames)) != null) baseCol = src.GetColor(p);
            m.SetColor("_BaseColor", baseCol);

            if ((p = FirstProp(src, NormalNames)) != null)
            {
                var n = src.GetTexture(p);
                if (n != null) { m.SetTexture("_BumpMap", n); m.EnableKeyword("_NORMALMAP"); }
            }

            float metal = 0f, rough = 1f;
            if ((p = FirstProp(src, MetalNames)) != null) metal = src.GetFloat(p);
            if ((p = FirstProp(src, RoughNames)) != null) rough = src.GetFloat(p);
            else if (src.HasProperty("_Smoothness")) rough = 1f - src.GetFloat("_Smoothness");
            m.SetFloat("_Metallic", Mathf.Clamp01(metal));
            m.SetFloat("_Smoothness", Mathf.Min(1f - rough, 0.15f)); // painted look
            m.SetFloat("_SpecularHighlights", 1f);
            m.SetFloat("_EnvironmentReflections", 0f);
            m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");

            // alpha cutout
            bool cutout = src.renderQueue >= 2450 && src.renderQueue < 2900;
            if (cutout)
            {
                float cut = src.HasProperty("alphaCutoff") ? src.GetFloat("alphaCutoff") : (src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0.5f);
                m.SetFloat("_AlphaClip", 1f);
                m.SetFloat("_Cutoff", cut);
                m.EnableKeyword("_ALPHATEST_ON");
                m.renderQueue = (int)RenderQueue.AlphaTest;
            }
            // double sided: glTFast exposes _CullMode / _Cull on some shaders
            try
            {
                float cull = -1;
                if (src.HasProperty("_CullMode")) cull = src.GetFloat("_CullMode");
                else if (src.HasProperty("_Cull")) cull = src.GetFloat("_Cull");
                if (cull == 0f) m.SetFloat("_Cull", 0f);
            }
            catch { }

            // emission
            Color emis = Color.black;
            Texture emisTex = null;
            if ((p = FirstProp(src, EmisColNames)) != null) emis = src.GetColor(p);
            if ((p = FirstProp(src, EmisTexNames)) != null) emisTex = src.GetTexture(p);
            // glow: the export already carries emissive colour / maps (+ emissive_strength) -> kept as-is, HDR, Bloom picks it up.
            // lightstyle: emissive += albedo x k.
            if (info.lightstyle)
            {
                emis += baseCol * info.lsK;
                if (emisTex == null) emisTex = baseTex;
            }
            if (emis.maxColorComponent > 0.001f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emis);
                if (emisTex != null) m.SetTexture("_EmissionMap", emisTex);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            return m;
        }

        // Keep glTFast's shader (vertex colours, blending) but apply the painted-look rules.
        static Material TweakCopy(Material src, MatInfo info)
        {
            var m = new Material(src);
            m.name = src.name;
            if (m.HasProperty("roughnessFactor")) m.SetFloat("roughnessFactor", Mathf.Max(m.GetFloat("roughnessFactor"), 0.85f));
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", Mathf.Min(m.GetFloat("_Smoothness"), 0.15f));
            if (info.lightstyle)
            {
                string bc = FirstProp(m, BaseColNames);
                string ec = FirstProp(m, EmisColNames);
                if (bc != null && ec != null)
                {
                    m.SetColor(ec, m.GetColor(ec) + m.GetColor(bc) * info.lsK);
                    m.EnableKeyword("_EMISSION");
                }
            }
            return m;
        }

        // ---------------------------------------------------------------- post (Volume)

        static void SetupVolume(Dictionary<string, object> cfg, string profilePath, Camera cam)
        {
            // remove any volume that came with the template scene
            foreach (var v in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(v.gameObject);

            AssetDatabase.DeleteAsset(profilePath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);

            // sidecar: renderer{toneMapping, exposure}; look.panel{col, con, exp, warm, bloom, ...} (+ panelActive); look.glow{bloomT, bloomK}
            var look = J.Get(cfg, "look") as Dictionary<string, object>;
            if (look == null) look = new Dictionary<string, object>();
            var panel = J.Get(look, "panel") as Dictionary<string, object>;
            object pa = J.Get(look, "panelActive");
            bool panelOn = panel != null && !(pa is bool && !(bool)pa);
            if (!panelOn) panel = new Dictionary<string, object>();
            var glow = J.Get(look, "glow") as Dictionary<string, object>;
            if (glow == null) glow = new Dictionary<string, object>();
            double d;
            Debug.Log(LOG + "LOOK panel " + (panelOn ? "active" : "inactive in the game -> neutral grade"));

            // Tonemapping
            try
            {
                var tm = profile.Add<Tonemapping>(true);
                object t = J.Get(cfg, "renderer.toneMapping", "toneMapping");
                var mode = TonemappingMode.ACES;
                string ts = t as string;
                if (ts != null)
                {
                    ts = ts.ToLowerInvariant();
                    if (ts.StartsWith("no") || ts.StartsWith("linear")) mode = TonemappingMode.None;
                    else if (ts.Contains("agx") || ts.Contains("neutral")) mode = TonemappingMode.Neutral;
                }
                else if (J.TryNum(t, out d))
                {
                    int ti = (int)d; // three.js constants: 0 none, 1 linear, 4 ACES, 6 AgX, 7 Neutral
                    if (ti == 0 || ti == 1) mode = TonemappingMode.None;
                    else if (ti == 6 || ti == 7) mode = TonemappingMode.Neutral;
                }
                tm.mode.Override(mode);
                AssetDatabase.AddObjectToAsset(tm, profile);
                Debug.Log(LOG + "tonemapping " + mode);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "tonemapping: " + e.Message); }

            // Color Adjustments (exposure, contrast, saturation)
            try
            {
                var ca = profile.Add<ColorAdjustments>(true);
                double exposure = 1, pexp;
                if (!J.TryNum(J.Get(cfg, "renderer.exposure", "renderer.toneMappingExposure", "exposure"), out exposure) || exposure <= 0) exposure = 1;
                if (J.TryNum(J.Get(panel, "exp"), out pexp) && pexp > 0) exposure *= pexp;
                ca.postExposure.Override(Mathf.Log((float)exposure, 2f));
                if (J.TryNum(J.Get(panel, "con", "contrast"), out d)) ca.contrast.Override(Mathf.Clamp(Grade(d), -100f, 100f));
                if (J.TryNum(J.Get(panel, "col", "saturation"), out d)) ca.saturation.Override(Mathf.Clamp(Grade(d), -100f, 100f));
                AssetDatabase.AddObjectToAsset(ca, profile);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "color adjustments: " + e.Message); }

            // Bloom
            try
            {
                var b = profile.Add<Bloom>(true);
                float inten = 0.42f, thr = 0.85f;
                if (J.TryNum(J.Get(glow, "bloomK"), out d)) inten = (float)d;
                if (J.TryNum(J.Get(glow, "bloomT"), out d)) thr = (float)d;
                if (J.TryNum(J.Get(panel, "bloom"), out d)) inten *= (float)d;
                b.intensity.Override(Mathf.Max(0f, inten));
                b.threshold.Override(Mathf.Max(0f, thr));
                AssetDatabase.AddObjectToAsset(b, profile);
                Debug.Log(LOG + "bloom intensity=" + inten + " threshold=" + thr);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "bloom: " + e.Message); }

            // White Balance (panel WARMTH; 0 = neutral, assumed -1..1)
            try
            {
                var wb = profile.Add<WhiteBalance>(true);
                if (J.TryNum(J.Get(panel, "warm", "warmth"), out d))
                    wb.temperature.Override(Mathf.Clamp(Math.Abs(d) <= 1 ? (float)d * 100f : (float)d, -100f, 100f));
                AssetDatabase.AddObjectToAsset(wb, profile);
            }
            catch (Exception e) { Debug.LogWarning(LOG + "white balance: " + e.Message); }

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var go = new GameObject("Global Volume");
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
                    data.renderShadows = true;
                }
                catch (Exception e) { Debug.LogWarning(LOG + "camera post toggle: " + e.Message); }
            }
        }

        // LOOK values: a multiplier around 1 (e.g. 1.1) -> Unity -100..100 scale; anything else assumed already in Unity units.
        static float Grade(double v)
        {
            if (v >= 0 && v <= 2.0) return (float)((v - 1.0) * 100.0);
            return (float)v;
        }

        static void SetupShadowDistance(Dictionary<string, object> cfg, GameObject root)
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) { Debug.LogWarning(LOG + "no URP asset active?"); return; }
            // sun.shadow.box = [left, right, top, bottom, near, far] of the game's shadow camera
            float dist = 80f;
            var box = J.Get(cfg, "sun.shadow.box") as List<object>;
            double far;
            if (box != null && box.Count >= 6 && J.TryNum(box[5], out far)) dist = Mathf.Max(40f, (float)far + 20f);
            asset.shadowDistance = dist;
            EditorUtility.SetDirty(asset);
            Debug.Log(LOG + "URP shadow distance = " + dist + " (on the project's URP asset)");
        }
    }

    // ------------------------------------------------------------------ json helpers
    static class J
    {
        /// First non-null value among dotted paths, e.g. Get(cfg, "sun", "lights.sun").
        public static object Get(Dictionary<string, object> d, params string[] paths)
        {
            if (d == null) return null;
            foreach (var path in paths)
            {
                object cur = d;
                foreach (var part in path.Split('.'))
                {
                    var cd = cur as Dictionary<string, object>;
                    if (cd == null) { cur = null; break; }
                    object next = null;
                    if (!cd.TryGetValue(part, out next))
                    {
                        // case-insensitive fallback
                        foreach (var kv in cd) if (string.Equals(kv.Key, part, StringComparison.OrdinalIgnoreCase)) { next = kv.Value; break; }
                    }
                    cur = next;
                    if (cur == null) break;
                }
                if (cur != null) return cur;
            }
            return null;
        }

        public static bool TryNum(object o, out double v)
        {
            v = 0;
            if (o is double) { v = (double)o; return true; }
            if (o is bool) { v = (bool)o ? 1 : 0; return true; }
            var s = o as string;
            if (s != null) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return false;
        }

        public static bool TryVec3(object o, out Vector3 v)
        {
            v = Vector3.zero;
            var l = o as List<object>;
            double x, y, z;
            if (l != null && l.Count >= 3 && TryNum(l[0], out x) && TryNum(l[1], out y) && TryNum(l[2], out z))
            { v = new Vector3((float)x, (float)y, (float)z); return true; }
            var d = o as Dictionary<string, object>;
            if (d != null && TryNum(Get(d, "x"), out x) && TryNum(Get(d, "y"), out y) && TryNum(Get(d, "z"), out z))
            { v = new Vector3((float)x, (float)y, (float)z); return true; }
            return false;
        }

        public static bool TryQuat(object o, out Quaternion q)
        {
            q = Quaternion.identity;
            var l = o as List<object>;
            double x, y, z, w;
            if (l != null && l.Count >= 4 && TryNum(l[0], out x) && TryNum(l[1], out y) && TryNum(l[2], out z) && TryNum(l[3], out w))
            { q = new Quaternion((float)x, (float)y, (float)z, (float)w); return true; }
            var d = o as Dictionary<string, object>;
            if (d != null && TryNum(Get(d, "x"), out x) && TryNum(Get(d, "y"), out y) && TryNum(Get(d, "z"), out z) && TryNum(Get(d, "w"), out w))
            { q = new Quaternion((float)x, (float)y, (float)z, (float)w); return true; }
            return false;
        }

        /// "#rrggbb", "0xrrggbb", 0xrrggbb as a number, [r,g,b] (0-1 or 0-255), {r,g,b}. Treated as sRGB, like Unity's Color.
        public static bool TryColor(object o, out Color c)
        {
            c = Color.white;
            if (o == null) return false;
            var s = o as string;
            if (s != null)
            {
                s = s.Trim();
                if (s.StartsWith("#")) s = s.Substring(1);
                else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
                int hex;
                if (s.Length >= 6 && int.TryParse(s.Substring(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex))
                { c = FromHex(hex); return true; }
                return false;
            }
            if (o is double)
            {
                double dv = (double)o;
                if (dv > 1.0) { c = FromHex((int)dv); return true; }
                return false;
            }
            var l = o as List<object>;
            double r, g, b;
            if (l != null && l.Count >= 3 && TryNum(l[0], out r) && TryNum(l[1], out g) && TryNum(l[2], out b))
            {
                if (r > 1.0 || g > 1.0 || b > 1.0) { r /= 255.0; g /= 255.0; b /= 255.0; }
                c = new Color((float)r, (float)g, (float)b, 1f); return true;
            }
            var d = o as Dictionary<string, object>;
            if (d != null)
            {
                if (TryNum(Get(d, "r"), out r) && TryNum(Get(d, "g"), out g) && TryNum(Get(d, "b"), out b))
                {
                    if (r > 1.0 || g > 1.0 || b > 1.0) { r /= 255.0; g /= 255.0; b /= 255.0; }
                    c = new Color((float)r, (float)g, (float)b, 1f); return true;
                }
                object hexv = Get(d, "hex", "color");
                if (hexv != null && !(hexv is Dictionary<string, object>)) return TryColor(hexv, out c);
            }
            return false;
        }

        static Color FromHex(int hex)
        {
            return new Color(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f, 1f);
        }
    }
}
