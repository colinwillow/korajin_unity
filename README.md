# Korajin Bridge (Unity)

One click from the web game's Sky Meadow snapshot to a lit URP scene in Unity, for a side-by-side look.

## What you do (4 steps)

1. On GitHub: **Code > Download ZIP**, then unzip it.
2. Drag the **`KorajinBridge`** folder into the Unity **Project** window's `Assets` folder. Wait for the import bar to finish (the .glb takes a moment).
3. Menu bar: **Korajin > Build Meadow**. It makes and saves `Assets/KorajinBridge/Generated/Meadow.unity`.
4. Look at the **Game** view (or press Play), take a screenshot and send it to Claude.
   **Korajin > Open Game Frame** shows the web game's screenshot in a window next to it.

If the Console shows red compile errors, copy them to Claude as they are.
Yellow `[Korajin] step '...' failed` warnings are fine: each step is optional, the rest of the scene is still built. Send those too.

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

No assembly definition: the scripts compile into the default Editor assembly, which already sees URP.
