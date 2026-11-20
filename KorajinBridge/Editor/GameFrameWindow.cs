// Korajin > Open Game Frame: shows the web game's screenshot (<zone>_game.jpg) for side-by-side comparison.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Korajin.Bridge
{
    public class GameFrameWindow : EditorWindow
    {
        Texture2D tex;
        string label = "";

        public static void Open(string zone)
        {
            var w = GetWindow<GameFrameWindow>(false, "Korajin Game Frame", true);
            w.Load(zone);
            w.Show();
        }

        void Load(string zone)
        {
            string p = KorajinBridge.GameFramePath(zone);
            if (p == null) { label = "No *_game.jpg in " + KorajinBridge.ZoneFolder(zone); tex = null; return; }
            tex = GameViewAspect.LoadImage(p);
            label = tex != null ? p + "  (" + tex.width + "x" + tex.height + ")" : "Could not load " + p;
        }

        void OnGUI()
        {
            GUILayout.Label(label, EditorStyles.miniLabel);
            if (tex == null) return;
            Rect r = GUILayoutUtility.GetRect(10, 10, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit, false);
        }

        void OnDestroy()
        {
            if (tex != null) DestroyImmediate(tex);
        }
    }

    /// Best effort: set the Game view to the web screenshot's aspect via the (internal) GameViewSizes API.
    /// All reflection; if Unity changed the internals, it just logs and you pick the aspect by hand.
    public static class GameViewAspect
    {
        public static Texture2D LoadImage(string assetPath)
        {
            string abs = Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath);
            if (!File.Exists(abs)) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            if (!ImageConversion.LoadImage(t, File.ReadAllBytes(abs))) return null;
            return t;
        }

        public static void TrySet(Dictionary<string, object> cfg)
        {
            int w = 0, h = 0;
            string p = KorajinBridge.GameFramePath("meadow");
            if (p != null)
            {
                var t = LoadImage(p);
                if (t != null) { w = t.width; h = t.height; UnityEngine.Object.DestroyImmediate(t); }
            }
            if (w <= 0 || h <= 0) { Debug.Log("[Korajin] no game frame; Game view aspect unchanged."); return; }

            try
            {
                var asm = typeof(Editor).Assembly;
                var sizesType = asm.GetType("UnityEditor.GameViewSizes");
                var single = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
                var instance = single.GetProperty("instance").GetValue(null, null);
                var groupType = sizesType.GetProperty("currentGroupType").GetValue(instance, null);
                var group = sizesType.GetMethod("GetGroup").Invoke(instance, new object[] { (int)groupType });
                var gType = group.GetType();

                string name = "Korajin " + w + "x" + h;
                int idx = -1;
                var texts = gType.GetMethod("GetDisplayTexts").Invoke(group, null) as string[];
                if (texts != null)
                    for (int i = 0; i < texts.Length; i++) if (texts[i].StartsWith(name)) { idx = i; break; }

                if (idx < 0)
                {
                    var sizeType = asm.GetType("UnityEditor.GameViewSize");
                    var enumType = asm.GetType("UnityEditor.GameViewSizeType");
                    var ctor = sizeType.GetConstructor(new[] { enumType, typeof(int), typeof(int), typeof(string) });
                    var size = ctor.Invoke(new object[] { Enum.ToObject(enumType, 1), w, h, name }); // 1 = FixedResolution
                    gType.GetMethod("AddCustomSize").Invoke(group, new[] { size });
                    texts = gType.GetMethod("GetDisplayTexts").Invoke(group, null) as string[];
                    for (int i = 0; i < texts.Length; i++) if (texts[i].StartsWith(name)) { idx = i; break; }
                }
                if (idx < 0) throw new Exception("custom size not found after adding");

                var gvType = asm.GetType("UnityEditor.GameView");
                var gv = EditorWindow.GetWindow(gvType);
                var prop = gvType.GetProperty("selectedSizeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null) prop.SetValue(gv, idx, null);
                else
                {
                    var m = gvType.GetMethod("SizeSelectionCallback", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    m.Invoke(gv, new object[] { idx, null });
                }
                gv.Repaint();
                Debug.Log("[Korajin] Game view set to " + name);
            }
            catch (Exception e)
            {
                Debug.Log("[Korajin] could not set the Game view size automatically (" + e.Message + "). Pick " + w + "x" + h + " in the Game view's aspect dropdown.");
            }
        }
    }
}
