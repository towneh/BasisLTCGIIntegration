# Basis LTCGI Integration

Drives [LTCGI](https://ltcgi.dev/) realtime area lighting from a Basis Media Player, so a video
screen casts light onto the room that matches whatever is playing.

Optional bolt-on: compiles only when both `com.basis.mediaplayer` and `at.pimaker.ltcgi` are
present.

## Requirements

Requires a Basis project from 2026-07-08 or later **and** LTCGI. Depends on:

- `com.basis.mediaplayer`, which ships with Basis
- `at.pimaker.ltcgi`, which isn't on a registry. Add it to `Packages/manifest.json` yourself:

  ```json
  "at.pimaker.ltcgi": "https://github.com/PiMaker/ltcgi.git"
  ```

Surfaces that should receive the light need an LTCGI-compatible shader. In a URP project that
means `LTCGI/Lit URP`, which takes the place of `Universal Render Pipeline/Lit` and keeps a Lit
material's settings.

## Installation

### Basis Package Manager (recommended)

Search for **Basis LTCGI Integration** in the
[Basis Package Manager](https://github.com/BasisVR/BasisPackageManager) and install.

### Unity Package Manager (git URL)

In Unity: **Window → Package Manager → + → Add package from git URL…**, then paste:

```
https://github.com/towneh/BasisLTCGIIntegration.git
```

### Manual (`Packages/manifest.json`)

```json
"com.basis.integration.ltcgi": "https://github.com/towneh/BasisLTCGIIntegration.git"
```

## Setup

1. **Select the media player** (such as the `MediaPlayerStreaming` prefab) or its screen, and
   choose **Basis → Tools → LTCGI → Set Up Video Light On Selected Player**. It adds an LTCGI
   Controller if the scene has none, makes the player's screen a dynamic LTCGI screen showing
   the live video, adds the video adapter, and bakes. It can be undone in one step. The screen
   has to be a quad or a triangle, as LTCGI requires.
2. **Give the surfaces an LTCGI shader.** For a URP Lit material, switch its shader to
   `LTCGI/Lit URP`; its settings carry over.
3. **Play** a video. The screen lights the room with it. There's no preview in edit mode.

The controller warns *"Video Texture is not set!"*. That's expected, as the adapter supplies the
video at runtime. The light's own settings stay on LTCGI's components. **Color** on the
`LTCGI_Screen` sets how bright it is, and ticking **Affect Avatars** there (then **Force
Update**) lights avatars as well.

LTCGI has a single live video input, so one player lights the room at a time. With adapters on
more than one player, the first one enabled drives the light and the others turn themselves off
with a warning. To switch players, disable the active adapter, then enable the other one.

Outside desktop builds, Quest included, LTCGI allows only one screen in the scene, so a world
that also ships for Quest should have just the one video light. The setup menu warns about this
on a desktop target and refuses on any other.

### By hand

1. Add an **LTCGI Controller** to the scene.
2. On the renderer that shows the video, add an `LTCGI_Screen`, tick **Dynamic** (and
   **Affect Avatars** to light avatars), and set **Color Mode** to **Texture** with
   **`[Live Video]`** selected.
3. Add **Basis LTCGI Video Adapter** to the media player's GameObject. **Player** fills itself
   in, and **Target** is found at runtime if left empty.
4. On the controller, click **Force Update**. Bake again whenever you change a screen's
   **Dynamic** flag or **Color Mode**.

### A screen in panels

LTCGI screens are flat quads, so a picture shown across several surfaces (an LED wall in
sections, a screen that wraps round a booth, an angled or curved display) needs one LTCGI screen
per flat panel. The adapter can feed them all from one player: each panel gets its own part of
the picture, packed into one cell of the texture LTCGI reads. This needs a desktop build, as
LTCGI allows only one screen elsewhere.

1. **Add a quad per panel.** A child GameObject with a `MeshFilter` holding a quad that covers
   the panel, facing the side it lights. It needs no renderer. Keep the corners in Unity's quad
   order (bottom-left, bottom-right, top-left, top-right, seen from the lit side) with exactly
   square corners. LTCGI reorders the corners of a quad whose edges aren't exactly perpendicular,
   which turns an already ordered quad into a bow-tie, and its single-sided test then drops the
   screen's reflections everywhere and its light wherever no shadowmap supplies it.
2. **Make each quad a screen.** Add an `LTCGI_Screen`, tick **Dynamic**, and set **Color Mode**
   to **Texture** with **`[Live Video]`**, as in *By hand*.
3. **List the parts on the adapter.** In **Source Rects**, add one entry per panel, in a fixed
   order: the part of the frame that panel shows, in the UVs the screen mesh shows it with. Part
   0 is the first panel, part 1 the second, and so on.
4. **Choose Columns.** LTCGI blurs the texture into 2:1 targets for rough surfaces, and it
   blurs evenly only when the whole texture is close to that shape. The texture is
   `Columns × cell width` across and `rows × cell height` high, where a cell is the largest
   part's size. Pick the columns that bring it nearest 2:1:

   | Panels | Each part | Columns | Texture |
   | --- | --- | --- | --- |
   | 2, the halves of a 16:9 frame | 8:9 | 0 (one row) | 16:9 |
   | 4, the quarters of a 16:9 frame | 16:9 | 2 | 16:9 (one row would be 7:1) |
   | 9, a wall of 3 × 3 panels about 1.4:1 each | 1.4:1 | 3 | 1.4:1 (one row would be 12.6:1) |

   **Max Resolution** limits the longest side of the whole texture, so raise it with the
   number of panels: at 2048, a 3 × 3 grid keeps cells about 680 px wide.
5. **Give each quad its cell's UVs.** LTCGI reads the light through the screen mesh's UVs, so
   part i's quad covers the cell `BasisLTCGIVideoAdapter.PartUv(i, count, columns)` returns.
   From an editor script:

   ```csharp
   Rect cell = BasisLTCGIVideoAdapter.PartUv(i, adapter.SourceRects.Length, adapter.Columns);
   quadMesh.uv = new[]
   {
       new Vector2(cell.xMin, cell.yMin), new Vector2(cell.xMax, cell.yMin),
       new Vector2(cell.xMin, cell.yMax), new Vector2(cell.xMax, cell.yMax),
   };
   ```

6. **Bake.** Click **Force Update** on the controller. LTCGI stores each screen's UVs when it
   bakes, so bake again after changing the number of parts, **Columns** or a quad's UVs.

The rectangles in **Source Rects** can change at runtime, for a screen whose mesh swaps between
UV layouts: the next frame copies the new parts into the same cells. The number of parts and
**Columns** set where the cells are, so keep those fixed once the quads are baked.

## Component reference

| Field | Default | Purpose |
| --- | --- | --- |
| **Player** | auto | The `BasisMediaPlayer` to read. Falls back to `GetComponentInParent`. |
| **Target** | auto | The `LTCGI_UdonAdapter` to feed. The setup menu fills it in; otherwise it falls back to the first one in the scene. |
| **Output** | auto | The `BasisVideoMaterialOutput` whose screen the light matches: its flips, projection and placeholder. Falls back to the first one under the player. Set it when the screen's output sits elsewhere. |
| **Max Resolution** | 1024 | Longest side of the texture handed to LTCGI. Larger video is scaled down. |
| **Source Rects** | one, (0, 0, 1, 1) | The parts of the picture handed to LTCGI, in the UVs the screen shows them with, in this order. With one part, set it to the part of the frame the screen shows and give the `LTCGI_Screen`'s quad UVs from 0 to 1, so the light and reflections match the screen and nothing outside it reaches LTCGI. With several, each part fills one cell of the texture LTCGI reads and the quad of the screen for that part takes the cell's UVs (see *A screen in panels*). The rectangles can change at runtime, for a screen that swaps its UV layout. |
| **Columns** | 0 | Cells per row when there are several parts, filled left to right from the bottom row up. 0 puts every part in one row, part i of n covering u from i/n to (i+1)/n. Choose the columns that bring the whole texture nearest 2:1 (see *A screen in panels*). Cells left without a part are black. |

## How it works

The adapter subscribes to `BasisMediaPlayer.OnOutputTextureChanged`, copies each presented
frame into a mipmapped RenderTexture it owns, and binds that texture to LTCGI. LTCGI reads the
mip levels as blur for rough surfaces.

The copy takes its flips, projection mode and stereo eye from the screen's
`BasisVideoMaterialOutput` (**Output**, or the first one on the player or its children), so the
light matches the picture without any settings of its own. For side-by-side and over-under sources, that means the half
the screen shows.

By default a desktop bake sends the texture through LTCGI's blur chain. With Fast Sampling,
which LTCGI forces outside desktop builds, it's bound to the texture LTCGI samples directly, so
the light works on Quest too.

With no picture, the light follows the screen. Before the first frame, after the player closes
and when a video ends (unless it loops), LTCGI gets the output's placeholder, or black where it
has none. The next presented frame brings the video back. When the adapter is disabled, LTCGI
gets the controller's own video texture back. If something clears LTCGI's bindings at runtime
(a Desktop/VR switch, a render pipeline rebuild), the adapter notices and binds again on the
next frame.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| The screen emits a solid colour, not the video | **Color Mode** is **Static**. Set it to **Texture** with **`[Live Video]`**, then **Force Update**. |
| No light, and the Console says LTCGI is *"going to sleep"* | The screen isn't dynamic. Tick **Dynamic** on the `LTCGI_Screen` and **Force Update**. |
| *"no LTCGI_UdonAdapter in the scene"* | The controller hasn't been baked. Click **Force Update**. |
| *"no BasisVideoMaterialOutput under the player"* | The adapter can't see the player's screen output, so the light uses Mono projection and no flips. Keep the output on the player or one of its children. |
| *"already feeds LTCGI's video input"* | Another player's adapter is driving the light. Disable that adapter, then enable this one. |
| *"Too many screens in the scene!"* when baking for Android | LTCGI allows one screen outside desktop builds. Remove or disable the extra `LTCGI_Screen` components, then **Force Update**. |
| The setup menu says the screen *"needs a MeshFilter with a quad or triangle mesh"* | LTCGI can only use a quad or a triangle as a screen. Use a quad, or the stock `VideoScreen16x9` mesh. |
| With a screen in panels, glossy floors show grey bands across the reflection | The texture LTCGI blurs is far from 2:1, usually every part in one row. Set **Columns** (see *A screen in panels*), give the quads their new cells' UVs and **Force Update**. |
| A panel's light comes from the wrong part of the picture | The quad's UVs don't cover its part's cell. Set them from `PartUv` with the adapter's current part count and **Columns**, then **Force Update**. |
| A panel lights nothing, or its reflections are missing everywhere | Its quad's corners aren't in Unity's quad order with exactly square corners, so LTCGI reordered them into a bow-tie. Rebuild the quad (see *A screen in panels*, step 1). |

## License

Licensed under either of [Apache 2.0](LICENSE-APACHE) or [MIT](LICENSE-MIT), at your option.
See [LICENSE.md](LICENSE.md).
