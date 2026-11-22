# Korajin Bridge (Unity)

## Cloud build (phone link, no PC)

Every push to `main` runs `.github/workflows/webgl.yml`: GameCI builds the meadow for WebGL with Unity 6000.0.62f1
(`Assets/Editor/BuildScript.cs`: URP on, Korajin > Build Meadow, uncompressed WebGL) and publishes it to GitHub Pages:
**https://colinwillow.github.io/korajin_unity/**. Needs the secrets UNITY_LICENSE / UNITY_EMAIL / UNITY_PASSWORD and
Settings > Pages > Source = GitHub Actions. The cloud build has no lighting bake (Beautiful Lighting is a PC step).

One click from the web game's Sky Meadow snapshot to a lit URP scene in Unity, for a side-by-side look.

## Tonight: 6 steps

1. On GitHub: **Code > Download ZIP**, then unzip it. (This repo is now a Unity 6 URP project, so you can also
   open the unzipped folder itself in Unity Hub with 6000.0.62f1 and skip step 2.)
2. Drag the **`Assets/KorajinBridge`** folder into the Unity **Project** window's `Assets` folder. Wait for the import bar
   (the .glb takes a minute). If you did this before, delete the old `Assets/KorajinBridge` first.
3. **Korajin > Build Meadow** -- builds and saves `Assets/KorajinBridge/Generated/Meadow.unity`.
4. **Korajin > Beautiful Lighting** -- quality URP settings for your PC, SSAO, soft 4-cascade shadows,
   Adaptive Probe Volumes (bounce light), a sky tinted from the game, reflection probe, a soft "milky" grade.
5. **Window > Rendering > Lighting**, click **Generate Lighting** (bottom right of that window). The bake runs on your PC:
   roughly 5-20 minutes, progress bar at the bottom right of the editor. The view updates when it's done.
6. Screenshot the **Game** view, and open **Korajin > Open Game Frame** for the web version side by side. Send both to Claude.

If the Console shows red compile errors, copy them to Claude as they are.
Yellow `[Korajin] step '...' failed` warnings are fine: each step is optional, the rest still runs. Send those too.

## What Beautiful Lighting does

- Active URP asset: HDR, MSAA 4x, 4096 main-light shadows, 4 cascades over 140 m, soft shadows (high),
  HDR colour grading, Light Probe System = Adaptive Probe Volumes (L2).
- SSAO renderer feature on (added if missing), soft settings.
- Directional lights -> Mixed (realtime direct + baked bounce); the sun keeps soft shadows.
- Meadow meshes -> Contribute GI + Reflection Probe Static, receive GI from probes (the glb has no lightmap UVs, so
  no lightmaps are needed). Clouds and the far ring stay dynamic.
- Procedural skybox tinted with the game's sky / fog blue; ambient from the sky so the bake picks up its colour.
- A touch of exp2 fog; a Local Adaptive Probe Volume fitted to the playable area (the sidecar's collider meshes);
  a baked reflection probe.
- Lighting Settings asset: baked GI, GPU lightmapper, 4 bounces, indirect x1.25, albedo boost 1.15.
- Volume: ACES, soft contrast (-12), saturation +8, wide warm bloom, slightly warm white balance,
  cool-shade / warm-light split toning, light vignette; SMAA on the camera.

## What Build Meadow does

- New scene; instantiates `KorajinBridge/Exports/meadow/meadow.glb` (imported by glTFast).
- Reads `meadow.json` (the sidecar):
  - `sun` + `lights[]` -> the glb's directional lights get the game's colour / intensity; the one with `shadow: true`
    is the Sun (soft shadows, aimed from `sun.pos` to `sun.target`). Intensities are divided by PI
    (three.js r155+ physically based units vs URP Lit).
  - every HemisphereLight + `environment.intensity` -> Trilight ambient (sky / equator / ground).
  - `fog` -> linear fog 340..2000; `background` -> camera solid colour (the sky shader is not exported).
  - `renderer` -> ACES Tonemapping + exposure; `look.panel` (only if `panelActive`) -> contrast / saturation /
    exposure / warmth; `look.glow.bloomK/bloomT` -> Bloom. Saved as `Generated/meadow_Volume.asset`.
  - `camera` (cam_main: pos, quat, fov 38, near, far) -> Main Camera.
  - `materials{}.tags` -> SHADER TRICKS: `lsPatch`/`lightstyle` get emission = base x k when the game's light style is on
    (it is PBR / off in this export).
- Materials: opaque, non-vertex-coloured -> URP Lit, smoothness capped at 0.15 (painted look), no env reflections,
  the export's own emission kept (HDR, so Bloom catches the glow). Vertex-coloured, transparent and unlit (MeshBasic)
  materials stay on glTFast's URP shader graphs with roughness >= 0.85. All copies go to `Generated/Materials_meadow/`.
- Game view set to the screenshot's 960x540 (best effort; otherwise pick 16:9 in the aspect dropdown).
- URP shadow distance set from the game's shadow box (about 80 m) on the project's active URP asset.
- The Console prints `[Korajin] ...` lines for each step; send them with the screenshot.

## Missing on purpose (part 1 scope, see NOTES m4294)

- Water, waterfalls and glow/shimmer FX (custom shaders in the web game)
- Particles
- Grumbo and other characters
- Triplanar rock textures (rocks get their plain base texture)

## Versions assumed

- Unity 6.0 LTS (6000.0.x), Windows
- Universal RP 17.0.x (Universal 3D template)
- glTFast 6.x (`com.unity.cloud.gltfast`)
- Beautiful Lighting sets URP / APV options by serialized field name (m_LightProbeSystem, m_SoftShadowQuality, ...);
  if a name differs in your version it just logs "(no field ...)" and moves on.

No assembly definition: the scripts compile into the default Editor assembly, which already sees URP.
