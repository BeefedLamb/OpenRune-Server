# osrs-port tools

Not part of the server build. Lives at the repo root so upstream merges never touch it.

## exporter/
Pure-Python (stdlib only) OSRS cache → `.osm` mesh exporter for the Unity client.

```
python export_area.py <cache_dir> <out.osm> <regionX> <regionY> [radius]
python export_area.py ../../.data/cache/LIVE lumbridge.osm 50 50 1
```
Writes `<out>.osm` (terrain + objects) and `<out>_locs.json` (every placed object with id,
name, tile, type, rotation and right-click options).

Known simplifications: shaped overlays (paths/coastlines) are split on a diagonal instead of the
client's exact tile shapes; no textures (each texture uses its average colour); contoured-ground
objects are not bent to the terrain; animated objects are static.

## unity/
Copy `Assets/OsrsPort` into the Unity project and put the `.osm` in `Assets/StreamingAssets/`.
Pressing Play loads it automatically and spawns a first-person player (WASD, mouse, Shift run,
Space jump, N noclip, Esc frees the cursor).
