# osrs-port

Our own content for the offline single-player remake. Everything we add lives under this
folder so upstream OpenRune merges stay clean.

- Each subfolder (give it a unique name — Gradle drops modules whose folder name matches another module, e.g. `core`) with a `build.gradle.kts` is picked up automatically by `settings.gradle.kts`
  and put on the server classpath by `server/shared` — no core files need editing to add one.
- Package: `org.rsmod.content.osrsport.<module>`.
- `osrs-core/` — shared bits and the `::port` sanity-check command.
- `unity-link/` — TCP listener on 127.0.0.1:43610 that logs the Unity client in as a normal player and moves it to the tile Unity reports. `::where` lists linked players.
- Planned: `bots/`, `sailing/`.

Core engine edits (movement/protocol) that can't live here must be kept minimal and marked
with `// osrs-port:` so they're easy to find when merging upstream.
