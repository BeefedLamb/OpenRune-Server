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
- `::synth door_open` and the door opens both play synth 62. The clip is 3307 samples, 0.15 s, peak 0.500, rms 0.116. It is not silence. Whether it sounds like a door is unconfirmed from here.

Known limit, not changed: the open locs are shared with other doors. Closing one of these before the 500-tick respawn can put `loc.elfdoor` or the generic castle double door back instead of the hundred_lumbridge loc. Leaving it alone restores the map loc when the door respawns.

Not done: real textures. That is the next task.
