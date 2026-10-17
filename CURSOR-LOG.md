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

## 4. Animation-frame sounds

`:or-cache:exportSequences` now writes each sequence's frame sounds as a 17th tab column on `seqs.txt`. Each entry is `frame:synth:loops:location:retain`. The export wrote 14514 sequences (944 skeletal, 1616 with at least one frame sound, 0 undecodable) to `osrs-port-tools/unity/Assets/StreamingAssets/world/seqs.txt` and the same file was copied to `E:\osrs\My project\Assets\StreamingAssets\world\seqs.txt`. StreamingAssets stays untracked. `OsrsSound` synthesis was not changed.

Files changed:

- `or-cache/src/main/kotlin/dev/openrune/tools/SequenceExporter.kt` — the extra column, from `soundEffects` and `skeletalSounds`.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsAnimation.cs` — the same file is copied into `E:\osrs\My project\Assets\OsrsPort`. `OsrsAnimator.Play` and every frame `Advance` enters call `Hear`. The local player uses `OsrsSound.Play`. Every other animator uses `OsrsSound.PlayAt` at the body, with the frame's location as the range (8 tiles when the location is 0).

Tested in the open Unity editor (`E:\osrs\My project`, 6000.6.5f1), logged in as Lamb, against the server already listening on 127.0.0.1:43610. Sequence 80 (`dragon_attack`) has one frame sound: frame 9, synth 3752, loops 1, location 15, retain 0. Playing it on an animator marked as the local player logged `framesnd local plays=1 last=3752`. Playing it again with that mark cleared logged `framesnd npc plays=1 last=3752`. Both plays went through `OsrsSound`, and 3752 is the synth stored on that frame. Goblin attack sequences 309 and 310 and the unarmed punch sequence 422 have an empty sound column, so they were not the check.

What works: a sequence that has a frame sound plays that synth when the frame is entered, for the player's own animation and for another body.

Still unused: `retain` is exported and not applied. Skeletal sequences are still skipped by the animator, so their exported sounds do not play. A location of 0 uses an 8-tile range.

Where this stopped: task 4 is done. Tasks 5–7 were not started.

## 5. Distinct bot looks

Every bot was logging in with the engine's default body, so they all sent the same `PLOOK`. The Unity client already builds another player's body from that packet (`OsrsNpcManager` / `OsrsPlayerModels`). No Unity script changed.

Files changed:

- `content/osrs-port/bots/src/main/kotlin/org/rsmod/content/osrsport/bots/BotLook.kt` — on login, restore the bot's saved look if there is one. Otherwise roll a body from the login name (body type, a legal identkit from each character-design style table, and colours) and save it with `CharacterDesign`. The name is the stable id, so a restart that has not saved yet still rolls the same body.
- `content/osrs-port/bots/src/main/kotlin/org/rsmod/content/osrsport/bots/BotScript.kt` — applies that look before the bot is placed, and logs the `PLOOK` line.

Tested against the restarted server on 127.0.0.1:43610, logged in as Lamb. `::bot mira wanderer` and `::bot nia wanderer` stood on the same floor. Unity received both `PLOOK`s and built both bodies:

- Mira: body type 1, colours 0 14 9 2 12, torso kit 528, 1791 vertices. `PLOOK 9 1 0 14 9 2 12 -1 -1 -1 -1 528 -1 319 391 433 323 335 554`
- Nia: body type 0, colours 22 15 26 4 7, torso kit 363, 2313 vertices. `PLOOK 10 0 22 15 26 4 7 -1 -1 -1 -1 363 -1 343 360 385 289 299 368`

The default roster logged different lines too (Aldric and Brina are both body type 1 with different colours and kits; Corwin is body type 0).

What works: two bots next to each other get different bodies through the existing `PLOOK` path. The same name keeps that body.

Still the same on an unarmed wanderer: the four empty equipment slots stay -1, because there is no helm or weapon to put there. A woodcutter, fighter, or companion still equips their tool on top of this body.

Where this stopped: task 5 is done. Tasks 6–7 were not started.

## 6. Music

The server already decides region music (`MusicAreaScript` / `MusicPlayer`) and sends `MidiSongV2` and `MidiJingle`. The Unity link was dropping both. Songs in the cache are Jagex's packed MIDI, not SMF files.

Files changed:

- `osrs-port-tools/exporter/export_music.py` — reads index 6 (songs) and index 11 (jingles), converts each to a standard MIDI file, and writes `world/music.bin.gz`. The export is 882 songs and 315 jingles, none failed. Copied into `E:\osrs\My project\Assets\StreamingAssets\world`. StreamingAssets stays untracked.
- `content/osrs-port/unity-link/src/main/kotlin/org/rsmod/content/osrsport/unitylink/LinkClient.kt` — `MidiSongV2` becomes `SONG id fadeOutDelay fadeOutSpeed fadeInDelay fadeInSpeed`. `MidiJingle` becomes `JINGLE id`. Id 147 is `midi.stop_music`.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsServerLink.cs` — those lines call `OsrsMusic`. The same file is copied into `E:\osrs\My project\Assets\OsrsPort`.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsMusic.cs` — loads the MIDI and synthesises it with a small General MIDI wavetable soundfont (sine, triangle, square, saw, organ, noise). A song replaces the current one. A jingle plays on its own source and ducks the song. The same file is in the Unity project.

Tested in the open Unity editor, logged in as Lamb on the Lumbridge castle floor (map square 50,50), against the server on 127.0.0.1:43610. The server logged `Unity link song 2`. Unity loaded 882 songs and 315 jingles, then logged `music track=2 voices=4 peak=0.152 dur=136.0s notes=2098 plays=1`. That peak is from half a second of synthesised samples at the first note, and the clip is playing. Two earlier logins in the same area sent songs 76 and 64 (the modern Lumbridge list is shuffled). Song 64's first note is 26 seconds in, so a one-second sample from the start was silent; the measure now starts at the first note.

What works: walking into / logging into a music area sends that area's `MidiSongV2`, and the soundfont synth produces samples for it.

Still not the OSRS instruments: the soundfont is a wavetable bank, not the cache's index-15 patches and index-14 Vorbis samples, so the timbre is not the client's. A jingle was not sent during this login; `JINGLE` uses the same synth when the server writes a `MidiJingle`.

Where this stopped: task 6 is done. Task 7 was not started.
