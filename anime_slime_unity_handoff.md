# Anime Slime → Unity handoff brief

**Audience:** the AI agent (or human) that owns the Unity project consuming `anime_slime.fbx`.
**Status of this document:** spec / brief only. It contains no C# on purpose — implementing the
movement component is a separate, explicitly approved task.
**Authored from:** `anime_slime_fbx_studio.html` (the generator), the exported
`fbx-tools/out/anime_slime.fbx`, and a Blender 5.2.1 round-trip report
(`fbx-tools/out/blender-report.txt`). Every number below is measured from the file, not guessed.

---

## 0. Read this first

- **The FBX is a static prop.** It contains 13 meshes + 9 materials and **zero** animation,
  rig, skin, or physics data. Verified by scanning the binary: `AnimationStack 0`,
  `AnimationCurve 0`, `KeyTime 0`, `Deformer 0`, `Skin 0`, `Cluster 0`, `LimbNode 0`,
  `Texture 0`. The strings `bounciness`, `wobble`, `jump` do not exist in the file.
- **Therefore all motion is the Unity agent's job.** The wobble/bounce/jump was a
  `requestAnimationFrame` loop in the browser and was never exported. Section 5 is the
  exact contract to reproduce; section 6 tells you which sliders live in the file vs. in code.
- **Single source of truth = the studio page.** If a slider moves there, the FBX *and* the
  constants in section 5 must be re-derived together (section 10). Never hand-edit the FBX.
- **Do not add features that were not asked for** (physics bounce, extra bones, turning to
  face the jump direction, ground contact effects). Section 11 lists the traps.

---

## 1. What you are receiving (measured facts)

| Item | Value |
| --- | --- |
| File | `anime_slime.fbx`, binary FBX **7400**, **232 KB** (colours only — no embedded textures) |
| Preset used | `unity` → **Y up**, face looks down **+Z**, **1 unit = 1 m** (`GlobalSettings.UnitScaleFactor = 100`) |
| Contents | 13 meshes, 5,905 triangles, 3,345 vertices, 9 materials, 2 empty group nodes |
| Imported size | body base at y = 0, body top at **y = 1.929**, glow aura top at **y = 2.16**, width/depth 2.398 (aura 2.685) |
| Rig | **none** (no bones/avatar) → Model Importer Rig tab = *None/Generic*, no clips |
| Textures / UVs | material colours only; no image maps (`embedTextures: false`) |
| Geometry detail | `detail: 40` → the blob is a flat-bottomed dome: radius 1.2, Y-scale 0.85, bottom cut at polar 0.85π |

Tri/vert budget per mesh (use for LOD/overdraw reasoning, and as a drift check):

| Mesh | Tris | Verts | Mesh | Tris | Verts |
| --- | --- | --- | --- | --- | --- |
| `Slime_Body` | 2,200 | 1,189 | `Slime_Core` | 816 | 475 |
| `Slime_GlowAura` | 2,200 | 1,189 | `Slime_CoreGlow` | 520 | 315 |
| `Slime_Mouth` | 27 | 29 | `Slime_Highlight` | 24 | 16 |
| `Slime_Eye_L` / `_R` | 20 each | 22 each | `Slime_HighlightDot` | 14 | 16 |
| `Slime_Blush_L` / `_R` | 20 each | 22 each | `Slime_EyeShine_L` / `_R` | 12 each | 14 each |

## 2. Import checklist (Unity)

1. Drop the FBX in `Assets/Models/`. Model tab: Scale Factor **1**, Convert Units **on**,
   Use File Scale on → the slime should appear **1.93 m tall**. If it arrives 100× off, the
   unit conversion is the culprit, not the FBX — do not fix it by scaling the mesh.
2. Rig tab: **None/Generic**, no avatar, no clips, no material remap needed.
3. Create a **prefab** `Slime.prefab`; never instance the raw FBX into scenes (keeps a single
   place to edit material setup and the movement component).
4. Materials auto-map by name — confirm you get exactly these nine:
   `Slime_Body`, `Slime_Core`, `Slime_CoreGlow`, `Slime_GlowAura`, `Slime_Eyes`,
   `Slime_Mouth`, `Slime_Blush`, `Slime_Highlight`, `Slime_Shine`. If a name is missing, the
   asset changed — stop and diff it (section 10).
5. Three of them are **not** what FBX describes (`Slime_GlowAura`, `Slime_CoreGlow` are
   additive shells) and the flat face cards need two-sided rendering. Set them up by hand as
   in section 4 — this is the one unavoidable manual step, and the generator's own export
   dialog says so.

---

## 3. Hierarchy and pivots — read carefully, this is where ports go wrong

```
AnimeSlime                     (empty, at world 0,0,0 → the model's ground line)
├── Slime_Body                 (mesh)  pivot at local (0, 0.909, 0)
│   └── Slime_Face             (empty, identity)
│       ├── Slime_Eye_L        (mesh, y ≈ 1.03)   ← Slime_EyeShine_L is a CHILD of this eye
│       ├── Slime_Eye_R        (mesh, y ≈ 1.03)   ← Slime_EyeShine_R is a CHILD of this eye
│       ├── Slime_Mouth        (mesh, y ≈ 0.93)
│       ├── Slime_Blush_L / _R (mesh, y ≈ 0.98, x ≈ ∓0.70)
│       └── Slime_Highlight, Slime_HighlightDot (mesh, upper-left front)
├── Slime_GlowAura             (mesh, same geometry as the body, node scale 1.12)
├── Slime_Core                 (mesh, ø0.67, centre y = 1.092)
└── Slime_CoreGlow             (mesh, ø1.272 = 1.9 × the core, same centre)
```

Three facts that dictate the implementation:

1. **`Slime_Face` is a child of `Slime_Body`.** That is deliberate in the generator
   ("*inherits the jelly squash*"). Any squash applied to `Slime_Body` must be inherited by the
   face. **Never** position/scale the eyes, mouth, blush or highlights yourself — they are
   already placed on the (selected) body height and carry the correct orientation.
2. **`Slime_Body`'s pivot is the ellipsoid centre, 0.909 above the ground — not at the base.**
   Scaling it the way the browser does therefore pivots at the blob's middle: at maximum idle
   stretch (×1.096) the base sinks ≈0.087 units (8.7 cm) below the floor. Reproduce the
   browser 1:1 and accept that, or (nicer, if the art director allows) compensate the pivot:
   keep the body inside a child transform whose origin is on the floor and offset the mesh by
   +0.909 in local space, then scale that child. Decide explicitly and write it down — do not
   "discover" it later.
3. **The aura's pivot is not the body's.** Its bounds are 1.12×
   the body's (dims 2.685 × 2.16 × 2.685) and its pivot sits at y = 1.018 = 0.909 × 1.12, so
   the aura also rests exactly on the floor. In the preview the aura is re-scaled **every
   frame** as `bodyScale × 1.12`, component-wise — do the same, or the aura pops out of
   alignment during squash/stretch. The core and core glow do **not** follow the squash; they
   stay rigid at the centre height. Keep that behaviour: it is what makes the jelly read.

## 4. Materials — final values (from the Blender round-trip, after colour management)

| Material | Colour | Alpha | Emission | Roughness | Preview blending | Unity setup |
| --- | --- | --- | --- | --- | --- | --- |
| `Slime_Body` | `#10b981` | **0.65** | `#34d399` × **1.6** | 0.15 | normal, two-sided, depth-write | URP/Lit, Surface Type **Transparent**, Metallic 0, Smoothness 0.85, Emission = colour ×1.6, Render Face Both |
| `Slime_Core` | `#a7f3d0` | 1.0 | `#a7f3d0` × **2.5** | 0.35 | opaque | URP/Lit, Smoothness 0.65, Emission = colour ×2.5 |
| `Slime_CoreGlow` | `#a7f3d0` | 0.5 | none | — | **additive**, back-side, no depth write | Unlit additive (Blend One One, ZWrite Off, Cull Front) |
| `Slime_GlowAura` | `#34d399` | 0.35 | none | — | **additive**, back-side, no depth write | Unlit additive (Blend One One, ZWrite Off, Cull Front) |
| `Slime_Eyes` | `#0f172a` | 1.0 | none | — | opaque, **two-sided** | Unlit/Lit opaque, Render Face Both |
| `Slime_Mouth` | `#0f172a` | 1.0 | none | — | opaque, **two-sided** | Unlit/Lit opaque, Render Face Both |
| `Slime_Blush` | `#f472b6` | 0.4 | none | — | transparent, two-sided | Transparent, Render Face Both |
| `Slime_Highlight` | `#ffffff` | 0.55 | none | — | transparent, two-sided | Transparent, Render Face Both |
| `Slime_Shine` | `#ffffff` | 0.9 | none | — | transparent, two-sided | Transparent, Render Face Both |

Notes:

- **Additive is not in the file.** FBX has no blending mode; the Blender trip read all nine as
  alpha-hashed. `Slime_GlowAura` and `Slime_CoreGlow` must be switched to additive by hand or
  the glow will look like grey cling film.
- **Emission intensity**: the file carries the full value (1.6 / 2.5), but the *browser
  preview* deliberately dims emissive to 30% for viewport comfort. Pick one target and be
  consistent: file values = punchier glow, ×0.3 = matches the studio screenshot.
- **Transparency**: use the Opacity values. The viewport's glassiness (transmission/refraction)
  is a three.js-only effect and is not in the FBX — real glass needs a URP custom shader or a
  refraction pass, which is out of scope unless explicitly requested.
- **Shadows**: in the preview only `Slime_Body` casts (the face cards, aura, core and core glow
  do not). Match that for consistent framing, and turn *off* shadow casting on the additive
  shells — additive geometry that casts shadows looks broken.

---

## 5. Motion spec — the contract to reproduce

The browser had exactly two states and one transition. Everything below is the *measured*
behaviour of `animate()`; treat it as the spec, not as inspiration.

### 5.1 Constants

```jsonc
{
  "idle":  { "phaseRate": 3.0,            // rad/s at wobbleSpeed 1.0
             "bobAmplitude": 0.08,        // world units, clamped to >= 0 (never sinks)
             "stretchY": 0.08,            // × bounciness
             "squashXZ": 0.06 },          // × bounciness, opposite sign of stretchY
  "jump":  { "duration": 1.309,           // seconds  (78.5 frames at 60 fps in the browser)
             "apex": 1.8,                 // world units × bounciness  -> 2.16 at default
             "distance": 1.885,           // world units of straight-line travel (PI * 0.6)
             "prepSquashY": 0.25,         // × bounciness (rise half only)
             "prepBulgeXZ": 0.20,         // × bounciness (rise half only)
             "fallScale": [0.9, 1.2, 0.9] },
  "auraFollowsBody": true,                // aura.localScale = body.localScale * 1.12 every frame
  "coreFollowsBody": false,               // core + core glow stay rigid
  "jumpInterruptible": false              // triggers while airborne are dropped
}
```

Defaults from the studio: `bounciness = 1.2` (slider 0.2–2.0), `wobbleSpeed = 1.0` (slider 0–3).

### 5.2 Idle (state: Idle)

```
phase += dt * 3.0 * wobbleSpeed                     // radians
root.y  = max(0, sin(phase * 2) * 0.08)             // bob — NOT scaled by bounciness
sy      = 1 + sin(phase * 4) * 0.08 * bounciness
sxz     = 1 - sin(phase * 4) * 0.06 * bounciness
body.localScale = (sxz, sy, sxz)
aura.localScale = (sxz, sy, sxz) * 1.12              // auraSize
```

- At `wobbleSpeed 1` the squash is **1.91 Hz (period 0.524 s)** and the bob is **0.955 Hz
  (period 1.047 s)**. The bob is exactly half the squash frequency — keep that 2:1 ratio or the
  jelly/bob relationship breaks.
- Amplitude at default `bounciness 1.2`: `sy ∈ [0.904, 1.096]`, `sxz ∈ [1 ∓ 0.072]` — the
  horizontal term is the *opposite* sign of the vertical one. That sign flip is the whole
  jelly effect; do not "simplify" it into a uniform pulse.
- The volume is **not** preserved (≈ −5 % … +4 % across the cycle). It is an artistic curve, not a
  physical model.

### 5.3 Jump (state: Jump, one-shot)

```
triggerJump(): if (jumping) return;             // no queue, no re-trigger mid-air
               jumping = true; progress = 0
               dir = (cos(θ), 0, sin(θ)),  θ = random * 2π     // random azimuth, no rotation

per frame:     progress += (0.04 * 60) * dt     // browser used +0.04 PER RENDERED FRAME
               if (progress >= π): jumping = false; return to idle pose
               else:
                 root.y        = sin(progress) * 1.8 * bounciness
                 horizontal    = dir * (progress * 0.6)          // 1.885 units total
                 s             = sin(progress * 2)
                 if (progress < π/2) body.localScale = (1 + s*0.20*bounciness,
                                                        1 - s*0.25*bounciness,
                                                        1 + s*0.20*bounciness)
                 else                body.localScale = (0.9, 1.2, 0.9)
```

- Total airtime **1.309 s**, apex **1.8 × bounciness** (2.16 at default) at **0.655 s**,
  travel **1.885 units** — i.e. a constant ground speed of ~1.44 m/s, and a peak height of
  exactly one body height.
- The arc is (accidentally or not) a nearly perfect ballistic curve: apex 2.16 m in 0.655 s
  implies **g ≈ 10.1 m/s²**. If you prefer real physics for the arc, use gravity ≈ 10.1 and the
  timing matches the preview within ~1 %; Unity's default 9.81 is also fine if you keep the
  horizontal speed at 1.44 m/s.
- Trigger source in the studio: the "Test Directional Jump" button and the **Spacebar**
  (filtered so it does not fire while a form control has focus). Map to whatever gameplay
  input the project uses; keep the "ignore while airborne" gate.

### 5.4 Four preview quirks — copy or fix, but decide explicitly

1. **`+= 0.04` per frame is frame-rate dependent.** At 144 fps the jump was 2.4× faster. Use
   dt-based timing (1.309 s). This is a correction, not a deviation.
2. **The studio snaps the blob back to the origin when the jump ends**
   (`position.set(0, 0, 0)`). That is a preview affordance for a fixed camera. In Unity the
   horizontal displacement must **persist** (accumulate), unless the design says otherwise.
3. **Two hard scale discontinuities**: at `progress = π/2` the scale jumps from (1, 1, 1) to
   (0.9, 1.2, 0.9) instantly, and at the end of the jump it snaps back to (1, 1, 1). Reproduce
   exactly for an identical feel, or cross-fade over ~0.08 s. Do not leave it half-done.
4. **At high `bounciness` (≈2.0) the rigid core pokes through the top of the squashed body**
   during the jump: body top becomes 0.909 + 0.909 × 0.5 = 1.364 while the core still reaches
   1.427. The browser has the same flaw at extreme settings. Do not silently "fix" it, and do
   not hide it — flag it as a known deviation if you do.

---

## 6. Where every studio slider lives — do not expect these in the FBX

| Studio parameter (default) | Owner | Consequence in Unity |
| --- | --- | --- |
| `bodyColor #10b981`, `opacity 0.65`, `gloss 0.85` | **Material** `Slime_Body` | Already in the file (roughness was written as 1 − gloss = 0.15) |
| `transmission 0.25` | viewport-only | **Not in the file.** FBX has no transmission channel — ignore it, or author a custom glass shader |
| `bodyHeight 1.0` (range 0.6–1.5) | **Geometry** | Baked. Body is 1.929 tall at 1.0 → changing it requires a re-export **and** a re-check of section 5 numbers (apex/travel are in world units, unaffected; the model gets taller) |
| `detail 40` (range 8–64) | **Geometry** | Baked tessellation (2,200 body tris at 40) |
| `glow`, `glowColor #34d399`, `glowStrength 1.6` | mesh presence + material | In the file except *additive* (see §4) |
| `haloSize 1.12` (1.0–1.6) | **Node scale** | In the file — and it is the one *safe* runtime tweak: scale `Slime_GlowAura`'s node, never its geometry |
| `haloStrength 0.35` | material alpha | In the file |
| `core`, `coreColor #a7f3d0`, `coreSize 0.45` (0.1–0.95), `coreGlow`, `coreGlowStrength 2.5` | mesh presence + material + **radius** | Radius is baked: `coreR = max(0.06, 1.2 × coreSize × 0.62)` → ø0.67 at default; the outer shell is 1.9 × that |
| `face`, `faceColor #0f172a`, `blush`, `highlight` | mesh presence + material | Baked; the face layout follows `bodyHeight`, so it is always correctly placed |
| **`bounciness 1.2`** (0.2–2.0) | **Code only** | Never in the FBX. Multiply amplitudes exactly as in §5 |
| **`wobbleSpeed 1.0`** (0–3) | **Code only** | Never in the FBX |
| `exportPreset 'unity'`, `exportUnitScale 100` | export settings | Not model data; they produced the Y-up/metre file described in §1 |

Rule of thumb: **vertex changes ⇒ re-export; colour/alpha/emission ⇒ already in the file;
motion ⇒ code only.**

---

## 7. Recommended shape of the Unity side (architecture, not code)

```
SlimeAgent            (gameplay root: stays exactly on the ground, y = 0)
└── Visual            (empty; owns the idle bob and the jump arc — §5.2 / §5.3 "root")
    └── AnimeSlime    (the imported FBX subtree: Slime_Body, aura, core…)
```

- **Keep the visual offset off the gameplay root.** The agent never floats; colliders, ground
  raycasts and pathing stay honest, and the bob/arc cannot desync hitboxes.
- **One component** (e.g. `SlimeAnimation`) with `bounciness` / `wobbleSpeed` as serialized
  fields, the §5 constants as private constants, and a small public API:
  `PlayIdle()`, `TriggerJump(Vector3? direction = null)` (null ⇒ random azimuth),
  `bool IsAirborne`, and an event/callback when the jump completes. Keep the
  "ignore triggers while airborne" gate inside that one place.
- **Resolve the two scaling targets once at startup** (`Slime_Body`, `Slime_GlowAura`) and log
  a clear, actionable error if either name is missing — that is the early warning that the FBX
  was regenerated with `glow` off.
- **Do not use Mecanim.** There are no clips, and the spec is five lines of maths; an Animator
  would hide the numbers that designers need to tune. If the team insists on clips, treat §5 as
  the curve spec and note the switch.
- **Never write to the visual root's `localScale`** — it would scale the aura and the face
  together. Squash is written to `Slime_Body.localScale` only, and the aura is set explicitly.
- **Drive time from an accumulator**, not `Time.time`, so `wobbleSpeed` changes and future
  pauses/slow-motion behave. For the jump use a normalised `t = elapsed / duration`, reading
  `bounciness` live every frame (the browser did) so a mid-air tuning change does not teleport
  the blob.
- **Horizontal travel owner:** for a hopping-blob design, move the agent root and leave
  `Visual` at local `(0, 0, 0)`. If a `Rigidbody`/`NavMeshAgent` is introduced later, let it
  own the translation and keep this component purely visual — the discrete jump then becomes a
  velocity impulse, but the squash stays as specified.

---

## 8. Tuning surface and safe ranges

| Knob | Range (parity with the studio) | Default | What it changes |
| --- | --- | --- | --- |
| `bounciness` | **0.2 – 2.0** | 1.2 | Idle amplitudes (`±0.08·b` vertical, `∓0.06·b` horizontal), jump apex (`1.8·b`), jump prep squash (`0.25·b` / `0.20·b`) |
| `wobbleSpeed` | **0 – 3** | 1.0 | Idle phase rate only (`3.0·w rad/s`). **The jump ignores it, as in the browser** |

Interactions worth knowing before someone "tunes it":

- **`wobbleSpeed = 0` is a clean rest pose**: phase frozen at 0 → `scale = (1,1,1)`, `root.y = 0`.
  Jumps still play at full speed. That makes it the natural "idle off / posed" setting.
- **Travel distance does not scale with `bounciness`**, only apex does. At `b=2.0` the hop is
  3.6 m high but still 1.885 m long (a steep, vertical-looking hop); at `b=0.2` it is 0.36 m high
  over the same 1.885 m (a long, flat skim). This asymmetry is authentic to the studio — keep it
  unless the design says otherwise.
- Clamp the serialized fields to those ranges so a designer cannot reach a pose the studio preview
  cannot show.

---

## 9. Acceptance tests (numbers to assert, not vibes)

1. **Structure**: the prefab holds 13 `MeshRenderer`s under 2 empty groups, exactly the 9 material
   names of §4, 5,905 triangles total; overall bounds x/z = ±1.343 (aura), y = 0 → 2.16.
2. **Rest pose** (`wobbleSpeed 0`, or phase 0): body `localScale = (1,1,1)`, aura `= 1.12`,
   visual root `y = 0`, blob base exactly on the ground plane (no gap, no sink).
3. **Idle at defaults**: over a 2 s window `body.localScale.y` must visit **0.904 and 1.096**
   (±0.005), visual root `y ∈ [0, 0.08]` and *never negative*.
4. **Frequencies**: scale-Y peak-to-peak period ≈ **0.524 s ±0.02**; root-Y period ≈
   **1.047 s ±0.02** (at `wobbleSpeed 1`).
5. **Jump**: total airtime **1.309 s ±0.02**; apex **2.16 ±0.05** above ground (body top ≈ 4.09 at
   apex with the default `1.8·b = 2.16` visual offset against the model top 1.929);
   straight-line displacement **1.885 ±0.02** in the random direction, and it must **persist**
   afterwards.
6. **Gate**: two jump triggers issued together produce one arc; triggering mid-air never restarts
   or queues.
7. **Face integrity**: after a full jump, `Slime_Face` local position/rotation/scale remain
   exactly identity — all squash comes from the parent.
8. **Asset drift**: after any re-export, counts (13 meshes / 9 materials / 5,905 tris) and the
   1.929 body height are unchanged, or §1–§5 were updated deliberately.

---

## 10. Regenerating the asset and detecting drift

- The FBX is generated by `anime_slime_fbx_studio.html` (in-browser export). **Never hand-edit the
  FBX** — fix the generator instead.
- A self-contained harness ships next to the page in `fbx-tools/`:
  - `npm run check` — builds `out/anime_slime.fbx` from the default parameters and verifies FBX
    magic, mesh/triangle counts and round-trip drift (currently 0).
  - `npm run blender` — writes `out/blender-report.txt`, the authoritative source for §1 and §4
    (objects, sizes, materials). Blender 5.2.1 was used for the numbers in this brief.
  - `npm run browser` — renders the page headlessly and fails on JS errors/preview failure.
  - `npm run serve` — serves the page for manual export through the UI.
- If a designer changed sliders before exporting, the file will legitimately differ from the
  harness build. The §5 motion constants are in **world units**, so they survive a `bodyHeight`,
  `detail` or colour change; only a change to the *model height* invalidates the "apex = 1 body
  height" relationship quoted in §5.3.
- After any re-export: re-run §9 and re-confirm the nine material names. A renamed material breaks
  the additive/transparent setup **silently**.

---

## 11. Hard rules — violations are bugs, not style choices

1. **Never ask the importer for clips or an avatar.** The file has no animation and no rig.
2. **Never reparent, offset or scale the face.** Its placement is baked onto the body surface and it
   must inherit squash from `Slime_Body` only.
3. **Never write `localScale` on the visual root or on `AnimeSlime`** — only `Slime_Body` squashes.
4. **The aura's imported node scale is 1.12**, not 1. Code that derives the aura scale must be
   `body.localScale × 1.12`; a bare `body.localScale` assignment silently shrinks the glow.
5. **`bounciness` is a cosmetic multiplier, not restitution.** Do not build a `PhysicMaterial` from
   it, and do not add a `Rigidbody` unless a separate ticket asks for physics.
6. **Do not rotate the slime toward its jump direction.** The preview keeps facing +Z always.
7. **Do not keep the browser's per-frame `+= 0.04`.** Use `deltaTime` (1.309 s airtime).
8. **Do not keep the "snap back to origin" reset** when the jump ends.
9. **The prefab's local `y = 0` is the ground line.** Place it with its pivot on the floor; never
   compensate for a bad import by offsetting the model.
10. **The core and core glow staying rigid is intentional** — not a missing constraint.
11. **Keep the 2:1 bob-to-squash frequency ratio** and the opposite sign of the horizontal term.

---

## 12. Decisions a human must make before code is written

1. **Pivot:** accept the browser's ~8.7 cm ground sink at maximum stretch, or add the base-pivot
   compensation from §3.2? (Recommendation: compensate — it is invisible work now, obvious artifacts
   later on slopes or with a shadow-casting floor.)
2. **The two hard scale pops in the jump** (§5.4.3): reproduce 1:1, or cross-fade over ~0.08 s?
3. **Travel:** unlimited accumulation, or clamp to a play area / return home when idle?
4. **Future needs:** turning to face travel, collisions, or `Rigidbody` physics? If yes, §7's
   layout (and the ownership of translation) changes now, not later.
5. **Where do `bounciness` / `wobbleSpeed` live?** Serialized prefab fields, a `ScriptableObject`
   (recommended: one asset whose defaults are §5's numbers, mirrored with the studio page), or
   gameplay-driven (e.g. agitation/stress)?
6. **High-bounciness core clipping** (§5.4.4): document it as authentic, or parent the core to the
   body as a deliberate deviation (which then needs its own squash rule)?

---

### Provenance

Every measurement in this brief was taken from `fbx-tools/out/anime_slime.fbx` via a Blender 5.2.1
round-trip, plus a byte-level inspection of the FBX (entity counts) and a read of the generator's
export and animation code. If the FBX is regenerated, re-derive §1 and §4 and re-run §9 — do not
assume these numbers still hold.

*Suggested home in the Unity repo: `Assets/Models/anime_slime/README.md`, next to the FBX, so the
next agent finds it without being told.*
