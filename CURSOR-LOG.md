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

## 7. First-person arms

The first-person eye stays in front of the face. A copy of the whole body on that camera faced the wrong way, so the view was the back of the torso, the belt, and the legs, and the sword and shield were not readable.

The arms layer is now only the arms, hands, weapon, and shield (wear slots 6, 9, 3, and 5). Head, torso, and legs are not in that mesh. It sits on the camera in front of the eye, yaw 180 and pitched 25 degrees, so the view looks forward between the arms. The real body renderer is hidden in first person and shown again in third person.

Files changed:

- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsPlayerModels.cs` — `GetArmsMesh` builds that partial look. The same file is copied into `E:\osrs\My project\Assets\OsrsPort`.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/FirstPersonController.cs` — the camera child `FpArms` uses that mesh at local (0.02, -0.8, 0.7), euler (25, 180, 0). V or F5 still toggles third person. The same file is in the Unity project.

Tested in the open Unity editor, logged in as Lamb, against the server already listening on 127.0.0.1:43610. `::item bronze_sword` and wield. Worn slots were weapon 1277 and shield 1171. Looking slightly down, the center of the view is the castle window ahead. The left arm holds the wooden shield and the right hand holds the bronze sword (brown blade, yellow hilt). The back, belt, and legs are not in the frame. The shot is `E:\osrs\arms-final.png`. Third person in the same session turned the arms layer off, turned the real body on, and put the camera behind the whole character (`E:\osrs\arms-third.png`).

What works: first person looks forward past the arms with the equipped weapon and shield in view. Third person still shows the whole body. The eye position, tab lock, doors, and movement were left as they are.

The arms layer is the rest pose of those parts, not the walk animation. The shield sits large on the left because that is the size of the wooden-shield model in front of the camera.

Where this stopped: task 7 is done. The handoff list is finished.

## Session 2: 1:1 OSRS

### 1. OSRS instruments for music

The region-music player was a General MIDI wavetable. It now plays the cache's own patches and samples.

Files changed:

- `osrs-port-tools/exporter/export_instruments.py` — writes `world/instruments.bin.gz` (magic `OSI1`): index 15 music patches, index 14 Vorbis samples (not the shared setup archive), then the setup header. The LIVE cache export is 187 patches and 580 samples, about 3.8 MB. Copied to `E:\osrs\My project\Assets\StreamingAssets\world\instruments.bin.gz`. StreamingAssets stays untracked.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsVorbis.cs` — the client's Vorbis decoder. Sample 1 matches a separate Python port of the same decoder byte for byte (`md5 d11cb946`, 29652 samples, 22050 Hz, loop 23323–29652).
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsMusicBank.cs` — patch decode, MIDI player, and the mix. Pitch uses the client's 256-units-per-semitone step into 22050 Hz. Volume, expression, pan, and the 220-sample envelope tick follow the same channel defaults (volume 12800, expression 16383, pan 8192, bend 8192, bend range 256). Channel 9 starts on bank 128. A patch sample whose key is even after `key - 1` is an index-4 synth; an odd key is Vorbis.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsSound.cs` — `TryMix` / `TryMixId` render one index-4 synth to signed bytes and return its loop, in samples, so a drum patch can use it.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsMusic.cs` — `SONG` / `JINGLE` stream a 22050 Hz mono clip from that mixer. Stop id 147 still stops the song. A jingle ducks the song to 0.3 and the song comes back when the jingle finishes. The same four scripts are in `E:\osrs\My project\Assets\OsrsPort\Scripts` and match the repo copies.
- `osrs-port-tools/music-test/` — a small .NET program that compiles the mixer without Unity and writes a wav. It is not part of the Unity project.

Tested two ways.

Offline, from `music.bin.gz` and `instruments.bin.gz`, with the 12163 synths loaded: song 64 (a Lumbridge track) is 4493 notes, 324 seconds, 8 voices in the first 8 seconds after the first note, peak 0.179, rms 0.038. Song 2 is 2098 notes, 136 seconds, 6 voices, peak 0.098, rms 0.023. The wavs are `E:\osrs\music-song64.wav` and `E:\osrs\music-song2.wav`. The autocorrelation of the song 64 window peaks around 87 Hz, so the render is tonal.

In the open Unity editor (6000.6.5f1, `E:\osrs\My project`), after the scripts compiled (no CS errors) and play mode logged in as Lamb. The server was already listening on 127.0.0.1:43610. Unity logged `882 songs, 315 jingles, 187 patches, 580 samples` and then `music track=163 voices=8 peak=0.222 dur=115.2s notes=2264 plays=1`. Track 163 is the song the server sent for Lamb's Lumbridge tile on this login. No exception followed. Door synth 62 still played on the three Lumbridge doors.

What works: a region song is mixed from the cache patches and Vorbis samples, at the client's sample rate, and the clip is playing in the editor.

Still not a line-by-line port of the client's mixer. The output is mono, with the left and right pan-law gains folded together. Loop and ping-pong use linear interpolation; the client's other mixers are not ported. Bend-range RPN is not read (the Lumbridge songs that were inspected only send controllers 7, 10, and sometimes 1). A released note with no release envelope stops instead of sustaining. A synth note is not trimmed by `calculateDelay`, so it can start with a few milliseconds of silence. Whether this sounds like the real game still needs a listen.

Where this stopped: task 1 plays in game. Accepted for now. Still not exact: the mix is mono, and the mixer paths are approximate (pan-law fold, no bend-range RPN, release without an envelope stops the note, synth notes are not delay-trimmed). Left as it is.

### Arms, before the login screen

The first-person arms hung from the top of the view and the wooden shield covered the left side. The arms layer is lower, further forward, scaled to 60%, and tilted so the hands sit at the bottom. Same place as before: parented to the first-person camera.

Files changed:

- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/FirstPersonController.cs` — arms local position `(-0.06, -0.86, 0.88)`, euler `(26, 180, 0)`, scale `0.60`. The same file is in `E:\osrs\My project\Assets\OsrsPort\Scripts`.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsVerify.cs` — `E:\osrs\run-shot.txt` holds a png path. On play it logs in as Lamb, wears the bronze sword and wooden shield if they are not already on, stands at 3222,3212, looks at the fountain, and writes that png.
- `osrs-port-tools/unity/Assets/OsrsPort/Editor/OsrsVerifyBoot.cs` — enters play when that file or `run-verify.txt` is present.

Tested in Unity 6000.6.5f1, logged in as Lamb, sword and shield already worn, tile 3222,3212, yaw 0, looking at the fountain. Before: `E:\osrs\arms-before.png` (arms and shield fill both sides from the top). After: `E:\osrs\arms-after.png` (courtyard and fountain in the top of the frame, forearms and hands along the bottom, the shield a small shape at the bottom-left, the sword tip at the bottom-right). No CS errors.

What works: the top of the view is the courtyard. The arms come in from the bottom instead of hanging from the top.

Still a rest pose. The forearms sit nearer the bottom centre than the extreme corners, and the sword is only the tip. Walk and attack on the arms are task 5.

Where this stopped: the arm placement is in. Task 2 (login screen) is next.

### 2. Login screen, first step

On Play, once the player exists, the login screen is the cache title instead of the brown panel. The world still loads behind it. The screen is covered as soon as the login UI draws.

Files changed:

- `osrs-port-tools/exporter/export_title.py` — writes `ui/title.jpg` (index 10, archive `title.jpg`, 383x503, the left half) and the index-8 sprites `logo`, `titlebox`, and `titlebutton` as PNG. Run into `E:\osrs\My project\Assets\StreamingAssets`. Those binaries stay untracked.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsTitle.cs` — mirrors `title.jpg` to 765x503, composites the logo at (234, 8), the box at (202, 200) and the button at (308, 320), and draws that page scaled to the window with black bars. Plays song 0 (Scape Main) once when the screen shows. The same file is in `E:\osrs\My project\Assets\OsrsPort\Scripts`.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsCharacterDesign.cs` — the username field and Login sit on that page. Character design is unchanged and still only appears on a first login. The same file is in the Unity project.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsWorldLoader.cs` — the debug square-count line is hidden while the login screen is up.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsVerify.cs` — a shot path containing "title" saves the login screen and stops, without logging in.

The cache page itself is `E:\osrs\title-composed.png`. Ours, from the Unity game view, logged in as the name Lamb and waiting on Login: `E:\osrs\title-screen.png`. Unity 6000.6.5f1, no CS errors. The editor log shows `music track=0 voices=6 peak=0.000 dur=196.9s notes=6764 plays=1`. Peak 0 is the probe before the first audible buffer; the song is the one that starts.

What works: the stone hall, the Old School logo, the login box and the stone Login button are the cache sprites, laid out at 765x503 and scaled to the window. The username field and Login are on the box. Scape Main starts with the screen. Returning accounts still skip character design.

Still missing from task 2: the animated rune flames, text in the cache fonts (the field and the word Login are Unity's IMGUI font), and showing this screen before the world starts loading. The world load is covered once the login UI is up.

Where this stopped: the static title is in and screenshotted. Flames and cache fonts are the rest of task 2.

### 2. Login screen, flames and cache fonts

Play now opens on the title screen and does not load the world until Login. The box is where the client draws it. The words are the cache bitmap fonts. The side flames are the client's flame algorithm with the cache rune sprites.

Files changed:

- `osrs-port-tools/exporter/export_title.py` — also writes `rune0.png`–`rune11.png` (index 8 `runes`, 12 frames of 96x96) and `b12.fnt` / `p12.fnt`. A font file is `FNT1`, then 256 glyphs: advance from index 13, then ox, oy, sw, sh, then the glyph alpha. Run into both StreamingAssets trees. Those binaries stay untracked.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsFont.cs` — draws one cache font. The y argument is the baseline. A second pass at +1,+1 is the black shadow. Glyphs are white and `GUI.color` tints them.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsFlames.cs` — `calcFlamesPosition`, `doFlamesDrawing` and `randomizeBackground` from the 317 client. Left panel at (0, 0), right panel at (637, 0), each 128x265. The three palettes and the red/green shift match. A rune's opaque pixels punch holes in the height field so the fire rises in that shape. The first 220 steps run at load, which is about how long the client takes to form the first rune.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsTitle.cs` — mirrors `title.jpg` into a 765x503 page. Logo at (234, 18). Box at (202, 171). Login button sprite at box-local (27, 130). "Enter your username." in b12 yellow, centred on the box at local y 53. "Username: " plus the name in b12 white at local (90, 90), with a blinking bar. "Login" in b12 white centred on the button. A status line, when there is one, is p12.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsTitleScreen.cs` — created at startup, draws the title immediately, and holds the world until Login. Enter or the button calls `Release`, which then creates the world loader and copies the name onto the player link.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsWorldLoader.cs` — `AutoStart` returns until the title releases. `SpawnPlayer` copies the pending name.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsCharacterDesign.cs` — the username and connecting stages use the same cache-font login. Character design is unchanged and still only appears on a first login.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsVerify.cs` — a shot path containing "title" captures after 2.5s and does not release the world. Any other shot releases as Lamb so the world still starts.

The same scripts are in `E:\osrs\My project\Assets\OsrsPort\Scripts`. Unity 6000.6.5f1 compiled them with no CS errors.

Real flame, one frame of the wiki animation `Login_flame.webp` (175x335, the side fire above the brazier): `E:\osrs\login-flame-wiki.png`. Page: https://oldschool.runescape.wiki/w/File:Login_flame.webp . Ours, game view, before any world load: `E:\osrs\title-screen.png`. The editor log for that shot is `OsrsVerify: title shot` with no `world index loaded` after the compile, and `music track=0`.

What works: the screen is up on Play with the world not loading. The stone hall is the mirrored cache jpeg. The logo, box and Login button are the cache sprites at the client's positions. The side fire forms a rune above each brazier. "Enter your username.", "Username:" and "Login" are b12 with the shadow. Scape Main starts with the screen. Login still starts the world and the existing character-design flow.

Still short of a paused frame of the live client. The fire is the 128-wide classic panels, so it is narrower than the post-Kebos wiki still (175 wide) and a bit shorter in this frame. There is no Cancel button and no password field, which is the handoff. The caret is the font's bar glyph.

Where this stopped: task 2 is in. Next is the minimap, compass and orbs.

### 3. Minimap, compass and orbs, first step

The corner of the game view is the cache minimap: the stone frame, a map built from the loaded ground colours, the compass, the dots and the four orbs. Clicks on it do not walk.

Files changed:

- `osrs-port-tools/exporter/export_hud.py` — writes `ui/hud/` from index 8: mapback 12, compass 169, the red arrow 424, mapdots 300, the orb fills and icons (1060–1071, 1607, 1610) and `ui/p11.fnt`. Run into both StreamingAssets trees. The pngs stay untracked.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsWorldLoader.cs` — each loaded square keeps a 64x64 unlit ground colour, one pixel per tile. `TryTileColor` reads it.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsMinimap.cs` — draws that colour through the frame's transparent hole, rotated so the way you face is up. Yellow dots are NPCs, white dots are other players, red dots are ground items, and the red arrow is you. The compass is sprite 169 with the flat brown backing keyed out, and a click calls `FaceNorth`. The four orbs use the cache fills, icons and frame. Numbers are p11. Hitpoints, prayer and special come from the server. Run energy is on the same message once the server is rebuilt.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsFont.cs` — `DrawScreen` draws a cache font at screen pixels, for the orb numbers.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsServerLink.cs` — `ME` now also carries prayer, base prayer and run energy (0–10000).
- `content/osrs-port/unity-link/.../UnityLinkScript.kt` and `UnityLinkServer.kt` — the server sends those three extra fields. The process that was already listening on 43610 is the previous build, so this shot's run orb has no number yet.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsInteraction.cs` — a click on the map, compass or orbs is not a world click. The old bottom HP and special bars are gone.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/FirstPersonController.cs` — `FaceNorth` snaps the body to yaw 0. The arm placement is untouched.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsTitleScreen.cs` — the title stays up until Login, and does not come back over the world afterwards.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsVerify.cs` — the courtyard shot is a game-view capture, so the interface is in the png.

The same scripts are in `E:\osrs\My project\Assets\OsrsPort\Scripts`. Unity 6000.6.5f1 compiled them with no CS errors.

Ours, logged in as Lamb, standing in the Lumbridge courtyard and looking north: `E:\osrs\minimap-screen.png`. The frame, compass, arrow and orbs are the cache sprites. The map in that shot shows the courtyard grass, the path and the castle, with yellow NPC dots and red item dots. Prayer on this account is 7, hitpoints 17, special 100.

Still missing from task 3: wall lines (white, red for doors), map-scene sprites and map-function icons. The run-energy number needs the unity-link process restarted so it sends the new `ME` fields.

Where this stopped: the frame, the rotating map, the compass, the dots and the orbs are in and screenshotted. Walls and map icons are the rest of task 3.

### Cache UI scaled by whole pixels

The login text was soft because the 765x503 page was stretched by a fraction of a pixel. Cache UI now draws at 1x, 2x, 3x only, with nearest-neighbour filtering, centred in the window. Whatever is left is black. The minimap, compass, orbs and their numbers use the same step. A game view smaller than 765x503 stays at 1x and the page is cropped equally, so the glyphs do not blur.

Files changed:

- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsUi.cs` — the shared whole-pixel scale.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsTitle.cs` — the page, flames, logo, box and hit test go through it.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsFont.cs` — screen text (the orb numbers) is the same whole-pixel size, shadow included.
- `osrs-port-tools/unity/Assets/OsrsPort/Scripts/OsrsMinimap.cs` — frame, compass, arrow, dots and orbs use that scale.

The server was relaunched with `run-server.bat` (`gradlew run --offline`) after the old process on 43610 was stopped. The log says the Unity link is listening. The run orb now has a number.

Ours, login, game view 873x370: `E:\osrs\title-scale.png`. The yellow "Enter your username." is solid cache-font pixels with a one-pixel shadow, and the page sits between black side bars. The real login flames are `E:\osrs\login-flame-wiki.png`.

Ours, courtyard, same view: `E:\osrs\minimap-scale.png`. Hitpoints 17, prayer 7, run 100, special 100. The previous courtyard shot without the run number is `E:\osrs\minimap-screen.png`.

Where this stopped: the scale and the run number are in. Wall lines, map-scene sprites and map-function icons are still the rest of task 3.

