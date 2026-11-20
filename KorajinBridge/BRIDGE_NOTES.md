## THE BRIDGE -- the test IS the exporter (his, Nov 18)
His goal: keep working exactly this way (web, phone, many workers), and at the end move the game to Unity mostly AUTOMATICALLY, hand-fixing
only where it fails. Built for US first; if it gets close to seamless it could become a tool for other three.js-with-AI game makers. The game
looking amazing and finished stays the FIRST goal; this is a side lane.
- **Snapshot export (in the game):** load a zone, let it build, one button writes the LIVE scene as glTF (`vendor/GLTFExporter.js`: meshes,
  transforms, PBR materials, instancing, lights) + a sidecar JSON (sun, fog, exposure, tone map, bloom, LOOK PANEL values, colliders,
  object ids / tags, spawn points). Procedural geometry is captured as meshes, so no builder code has to be translated.
- **Unity importer (a package in `korajin-unity`):** reads the glTF + JSON and builds a ready scene: URP materials (custom shader effects via a
  lookup: our wind -> foliage wind, glow -> emission + bloom), the post-processing volume, colliders, the player start.
- **Gameplay code is NOT converted by the tool** -- an agent ports it system by system later; the data-first tables make that easier.
- **First run = this test:** the UPPER GARDEN (not the kingdom below) through the bridge, the hero dropped in, compared on his phone.
- Habits that keep the door open (cheap, now): assets as GLB, stable ids, data-first tables (SCENE_COMPANION.md), and a custom shader trick
  gets a line naming its engine equivalent.

### PART 1 BUILT (m4294): the snapshot export
- **In game:** TOOLS > SNAPSHOT EXPORT (or `?snap=1`, + `snapwait=6` s after the zone builds, `snaptex=1024`, `snaphero=1`, `snapkingdom=1`, `snapdl=0`)
  -> `korajin_<zone>_<build>.glb` + `.json`. Code: `zones/snapshot.js` (fetched on the tap, evaled in page scope; the scene is NOT touched: it
  exports a parallel tree that shares the game's vertex buffers). Headless: `npm run snapshot <zone>` -> `exports/<zone>/` (.glb, .json,
  `_game.jpg` = the game's own frame as the export starts). Blender check: `blender -b -P tools/snapshot_blend.py -- exports/<zone>/<zone>.glb`
  (Cycles CPU; this container has no EGL for EEVEE) -> `<zone>_blender.png`.
- **The .glb:** visible meshes (procedural included), local TRS, Standard / Basic materials (Lambert / Phong / Toon -> Standard; an opaque
  ShaderMaterial -> a Standard stand-in from its first colour + texture), every texture the GPU can sample (KTX2, half-float, data) blitted to
  8-bit, JPEG where opaque; InstancedMesh -> EXT_mesh_gpu_instancing (scale-0 "hidden" instances dropped: their NaN rotation made Blender refuse
  the whole file); point / spot / sun -> KHR_lights_punctual at the root in world space; `cam_main`.
- **The .json (sidecar):** renderer (tone map, exposure), fog, background, sun (dir, colour, intensity, shadow box / bias), hemi, the LOOK PANEL
  (`LKP.v` + defaults) and every look table (LOOKU, LSTYLE, ILL, STYLE, GLOW, SOFT, PALETTE, BAKE, GLOOK, LOOK), stone colours, colliders
  (`COLL.meshes` tagged `collider: mesh` per node + all `BOXES`), spawns (his position + heading, the zone's ENTRY row, its KPLACES rows, nodes
  named spawn / start), `nodes{}` (path in the game tree, userData ids, materials, collider, shadow flags), `materials{}` (source name, type,
  SHADER TAGS, the uniforms of a stand-in), `fx[]` (what was left out and where), `skinned[]`, `skipped{}`, `stat`.
- **Left out on purpose, listed in the sidecar:** see-through shader FX (water surface, falls, glows, shafts: no glTF form -- `fx[]` keeps their
  place + uniforms), points / lines / sprites, skinned bodies, his rig, HUD / debug / other layers, the garden's far kingdom.

### SHADER TRICKS -> their Unity (URP) equivalents (the importer's lookup; a material's `tags` in the sidecar name which apply)
| tag (sidecar) | the game's trick | Unity URP equivalent |
|---|---|---|
| `look` / `lookPatch` (uLk*) | LOOK: luminance banded into soft steps, the shade recoloured toward a tint, box bevel normals (`aBoxC`), a rim that skips floors | Shader Graph toon-ramp sub-graph (custom lighting: Main Light + ramp) + Fresnel rim masked by world-up; bevel = baked normal map / Unity's own bevel in the DCC |
| `lsPatch` / `lightstyle` (uLs*) | LIGHT STYLE PAINTED: emissive += albedo x k (self-light), wrapped sun diffuse, warm fill on the shade side, roughness floor .85, warm rim | URP Lit: Emission = Base x k; Smoothness <= .15; a custom-lighting sub-graph for wrap (NdotL*.5+.5) + fill colour |
| `paint` (uPaint*) | the global MeshStandard prototype patch: world-space paint mottling (two rotated samples of a band / cell / ink texture), hue drift, ink seams | Shader Graph: Triplanar sample of the same texture (exported as data later) multiplied into Base Color; a Detail Albedo map is the cheap stand-in |
| `toon` / `rim` / `spec` (uToon*, uRim*, uSpec*) | the garden kit's toon light + coloured rim + soft spec | the same toon-ramp sub-graph as `look` (one ramp shader for both) |
| `triplanar` (uTS, uTB) | rock / cliff / bark mapped in world or object space, no UVs | Shader Graph Triplanar node (object space for movers) -- the .glb carries NO texture for these yet (they render flat in Blender) |
| `wind` (sway, leaf, uSwT, aSw) | vertex sway of leaves / grass / vines by a wind vector + time, weighted per vertex | Shader Graph vertex offset (Time + wind vector, weight from vertex colour / UV) -- or Unity's grass / SpeedTree wind |
| `glow` (uGlow, gem, crystal, vein, emissive maps) | emissive colour / maps under ~4 HDR, the night vein glow | URP Lit Emission (HDR) + the Bloom volume override |
| `anim` (uTime, uT) | anything time-driven (pulse, flow, scroll) | Shader Graph Time node |
| `hue` / `jinHue` | the JINRA preset hue turned in the shader | Shader Graph Hue node (degrees from the sidecar) |
| `lodfade` (uLodCam) | distance fade between LODs | LOD Group cross-fade (dither) |
| `snow` (uSnow) | snow laid on up-facing faces | a world-normal-Y blend layer in Shader Graph |
| `night` | the night glow layer (NIGHT.md) | an emission mask driven by a global time-of-day float |
| `fx[]` kind `shaderFx` | water, falls, light shafts, auras (transparent / additive, no depth write) | URP Unlit transparent / additive material, VFX Graph or Particle System; water -> URP water shader |
| `fx[]` kind `Points` | GPU point particles (motes, sparkles) | Particle System / VFX Graph |
| sidecar `renderer` / `look.panel` | ACES tone map, exposure; the LOOK PANEL post (COLOUR, CONTRAST, EXPOSURE, WARMTH, BLOOM, DEPTH HAZE, ILLUSTRATED, PALETTE LIMITER) | Volume: Tonemapping ACES, Color Adjustments (saturation / contrast / post exposure), White Balance (temperature), Bloom; HAZE = fog; ILLUSTRATED / LIMITER = a Full Screen Pass Renderer Feature |
| sidecar `fog`, `hemi`, `sun` | linear fog, hemisphere light, the sun + its soft shadow | RenderSettings fog (linear), Ambient Mode Gradient (sky / equator / ground), Directional Light (soft shadows, cascade covering the shadow box) |

## The scene (fallback if the bridge stalls: hand-build from assets we already have)
