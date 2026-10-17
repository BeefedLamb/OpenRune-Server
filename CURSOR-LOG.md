# Cursor log

## 1. Lumbridge doors, Tab lock-on, door sound

Files changed:

- `.data/raw-cache/server/loc/doors.toml` — `loc.hundred_lumbridge_door`, `loc.hundred_lumbridge_doubledoorl`, and `loc.hundred_lumbridge_doubledoorr` had no content group, so Open answered "Nothing interesting happens." They now use the shared single-door and double-door scripts, open into `loc.elfdooropen` / `loc.opencastledoubledoorl` / `loc.opencastledoubledoorr`, and play `synth.door_open`.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsVerify.cs` and `Editor/OsrsVerifyBoot.cs` — play-mode check. Runs only when `E:\osrs\run-verify.txt` exists or the editor was launched with `-osrsVerify`.
- `OsrsCharacterDesign.cs` — `AutoSubmitForVerify()` so that check can get past the name screen.
- `OsrsSound.cs` — `Plays` and `LastSynth` counters so the check can see that a synth actually started.

The cache was rebuilt with `:or-cache:buildCache` and the server was restarted onto that cache. The built cache itself is not in git.

Tested in the Unity project (`E:\osrs\My project`), logged in as Lamb, against the server on 127.0.0.1:43610. Capsule radius 0.22, height 1.50.

What works:

- Poor door `loc.poordoor` (1535) at 3226,3214. Closed, the capsule stops on it. Open, the loc is removed, synth 62 plays, and the capsule walks through (progress 2.10).
- Castle west door `loc.hundred_lumbridge_door` (12348) at 3207,3217. Closed, the capsule hits `loc_12348_Door` and stops. Open, synth 62 plays, and the capsule walks north through the gap to z 3218.93 (progress 1.95). An earlier probe reported this door as still blocking after it opened; that probe had started on the wrong floor height and inside the doorway. A walk from the tile south of the slab, at that tile's ground height, goes through.
- Castle large door `loc.hundred_lumbridge_doubledoorl` (12349) at 3213,3221, with its pair 12350. Closed, the capsule hits the door. Open, synth 62 plays, and the capsule walks through (progress 2.15).
- Tab lock-on. Tab locked an attackable NPC, Shift+Tab switched to a different one, Escape cleared it, and a mouse flick cleared it. This was queued Input System state, not a physical key press in the editor.
- `::synth door_open` and the door opens both play synth 62. The clip is 3307 samples, 0.15 s, peak 0.500, rms 0.116. The user confirmed the door clip sounds right.

Known limit, not changed: the open locs are shared with other doors. Closing one of these before the 500-tick respawn can put `loc.elfdoor` or the generic castle double door back instead of the hundred_lumbridge loc. Leaving it alone restores the map loc when the door respawns.

Not done after task 1: real textures. That work is task 2 below.

## 2. Real textures

Files changed:

- `osrs-port-tools/exporter/models.py` — type 2 and type 3 models now keep their texture-triangle P/M/N coordinates, and `face_uvs` writes the client's static mapping in the exported corner order.
- `osrs-port-tools/exporter/export_textures.py` — new. Writes `textures.bin` (128×128 slices, one per texture id, plus scroll speed) and `model_uvs.bin.gz` (per-face UVs for `models.bin.gz` and `models_extra.bin.gz`).
- `osrs-port-tools/exporter/scene.py` and `export_world.py` — terrain blobs are SQ3. Each vertex still has its colour, plus a texture byte (0 = flat colour, otherwise texture id + 1). The loader still accepts the old SQ2 blobs.
- `osrs-port-tools/unity/Assets/OsrsPort/Shaders/VertexColorLit.shader` — when the UV's z is a texture slice, the sample is multiplied by the vertex colour and then fogged. Faces with no texture are unchanged.
- `WorldData.cs` and `OsrsWorldLoader.cs` — load the sidecar and the slices, put the slice in the UV, and light textured faces with a greyscale factor instead of the texture's average colour. The same files are copied into `E:\osrs\My project`.

The world was re-exported to a temp folder and then copied into `E:\osrs\My project\Assets\StreamingAssets\world` (packs, index, models, variants, locs, textures, UVs). StreamingAssets is not in git. 215 texture slices, 28,321 models in the UV sidecar, 1,285 of them textured, 0 face-count mismatches.

Tested in play mode at the Lumbridge spawn (camera 3221.5, 3.40, 3218.5). The console logged `215 textures, 128px` and no exceptions. A frame from the player camera shows stone castle walls, the wooden castle door, grass, the grey path, and the hedges with real textures, still shaded and with fog. 19 terrain meshes in view carried 21,441 textured vertices, and 385 other meshes had a texture.

Still wrong at first: the courtyard fountain's water was a black column with white specks. Flat-coloured tiles that have no cache texture stay flat, which is the old underlay path.

### Fountain

The Lumbridge courtyard fountain is `loc` 879, model 1497, at 3221,3226 and 3221,3210. Its jet uses texture 17, a sprite of blue droplets on a transparent background. The shader drew those empty texels as black, so the jet was a solid black column and the white specks were the droplets.

`VertexColorLit.shader` now discards texels with alpha below 0.5 in the forward, depth, and depth-normals passes. Opaque walls are unchanged (their texels are fully opaque). The droplets stay multiplied by the baked light, then fogged, and the jet scrolls because texture 17 is animated.

Tested in play at the Lumbridge spawn, camera (3221.5, 3.40, 3218.5), looking north. The fountain is a stone basin with blue droplets spraying up. The castle walls, wooden door, grass, path, and hedges are unchanged. No shader or console errors.

Upper floors are task 3 below.

## 3. Upper floors

The four map planes were already built. `OsrsWorldLoader` already shows a plane at or below the player's level and hides higher planes when a roof is overhead (`viewLevel`, `hideRoofs`, `ApplyPlaneState`). That code was left as it is.

What was missing is the Lumbridge castle stair. The ground-floor loc `loc.spiralstairsbottom_3` (56230) only answered Top-floor (op 2, jump two levels). Climb-up (op 1) did nothing. The top loc `loc.spiralstairstop_3` (56231) had the same gap on Climb-down. The middle loc `loc.spiralstairsmiddle` (16672) and `loc.ladder` (16683) already climb through their content groups.

Files changed:

- `content/generic/generic-locs/src/main/kotlin/org/rsmod/content/generic/locs/staircase/SpiralStaircaseScript.kt` — op 1 on `loc.spiralstairsbottom_3` calls the existing `climbUp` (one level). Op 1 on `loc.spiralstairstop_3` calls the existing `climbDown`.

Tested against the restarted server on 127.0.0.1:43610, logged in as Lamb. `OPLOC 56230 3204 3229 0 1` moved the player from tile 3204,3229 level 0 to tile 3206,3229 level 1. Unity then placed the camera at (3206.50, 5.60, 3229.50). That height is the castle's first floor, inside the stone walls, with the arrow slit looking out at the sky. The floor under the player is the upper plane, and the roof is not drawn over that window.

What works:

- Ground-floor Climb-up on `loc.spiralstairsbottom_3` (already committed) goes from level 0 to level 1.
- From that first floor, Climb-up on the middle stair `loc.spiralstairsmiddle` (16672, already wired) at 3204,3229 goes to level 2. Unity followed: camera (3206.50, 7.48, 3229.50), standing on `plane_2` (raycast hit y 7.38). `viewLevel` was 2. The north landing is not under a higher roof, so planes 0–3 stayed visible and the view is the battlements looking out over Lumbridge, with fog.
- Climb-down on the top stair `loc.spiralstairstop_3` (56231) at 3205,3229 op 1 comes back to level 1, tile 3205,3228. Unity followed: camera (3205.50, 5.60, 3228.50), standing on `plane_1` (raycast hit y 5.50). `viewLevel` was 1 and `roofed` was true, so planes 2 and 3 were hidden and nothing above the player collided. The view is the interior stair and the arrow slit, with the floor above not drawn over it.
- The castle's level-3 locs are roof pieces (loc 1913, no options) over the inner rooms. No stair or ladder in this castle reaches a floor on plane 3. `loc.ladder`, `loc.laddermiddle`, and `loc.laddertop` already have climb content groups, so no new op was added.

Where this stopped: task 3 is done. Tasks 4–7 were not started. Lamb is saved on the first floor at 3205,3228.
