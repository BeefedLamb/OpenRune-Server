# osrs-port tools

Not part of the server build. Lives at the repo root so upstream merges never touch it.

## exporter/
Pure-Python (stdlib only) OSRS cache exporters for the Unity client.

Whole world (what the client uses now):
```
python export_world.py ../../.data/cache/LIVE <out_dir>
```
Writes `<out_dir>/world/`: `models.bin.gz` (every model placed objects use, once),
`variants.bin.gz` (object → models, recolours, scale), `locs.json` (object names and options),
`index.bin` + `pack_N.bin` (one gzip blob per map square: terrain triangles and object
placements with rotation and offsets already worked out). About 160 MB for the whole game;
takes ~17 min. `verify_world.py <out_dir>/world` rebuilds a square the way Unity does and
renders it as a check. Re-run after a cache/revision bump.

Single area (older, baked triangles, kept for reference):
```
python export_area.py ../../.data/cache/LIVE lumbridge.osm 50 50 1
```

Known simplifications: shaped overlays (paths/coastlines) are split on a diagonal instead of the
client's exact tile shapes; no textures (each texture uses its average colour); contoured-ground
objects are not bent to the terrain; animated objects are static.

## unity/
Copy `Assets/OsrsPort` into the Unity project and the exported `world/` folder into
`Assets/StreamingAssets/world/`. Pressing Play streams the map squares around the player
(`OsrsWorldLoader`, radius 1 = 3×3 squares) and spawns a first-person player at Lumbridge
(WASD, mouse, Shift run, Space jump, N noclip, Esc frees the cursor). `OsrsServerLink`
connects to the server's unity-link port if the server is running.
