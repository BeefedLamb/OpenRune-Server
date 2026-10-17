# How to test (OSRS Unity port)

## Easy button
1. Double-click `C:\Users\mfears\Projects\OpenRune-Server\run-server.bat` (a Desktop shortcut "OSRS Server" points to it).
2. Wait for **Server ready** in the black window (first start ~1 min; code is pre-compiled).
3. Open **Unity Hub** > Add > select `C:\Users\mfears\Projects\OpenRune-Server\osrs-port-tools\unity` > open with **6000.6.5f1**.
   - First open only: Unity creates ProjectSettings/Packages/Library (the repo only has `Assets`), takes several minutes. Accept any upgrade/import prompts.
4. Open the main scene under `Assets/OsrsPort` and press **Play**. Client connects to 127.0.0.1:43610.
5. To stop: exit Play, close the server window.

If the port is busy, the launcher kills whatever listens on 43610 first.

## Where things are
| What | Path |
|---|---|
| Server repo (Kotlin) | `C:\Users\mfears\Projects\OpenRune-Server` |
| osrs-port server modules | `content\osrs-port\` (bots, core, osrs-core, unity-link) |
| Skills / minigames | `content\skills\`, `content\minigames\` etc. |
| Unity project (open this) | `osrs-port-tools\unity` (Assets only) |
| Unity scripts/shaders | `osrs-port-tools\unity\Assets\OsrsPort\` |
| Exported world/anim/icons | `osrs-port-tools\unity\Assets\StreamingAssets\` (`world\`, `items\`) |
| Python exporter | `osrs-port-tools\exporter\` |
| Game cache + data | `.data\cache`, `.data\raw-cache`, `.data\gamevals` |
| Java 21 | `E:\tools\jdk-21.0.12.1+1` |
| Unity editor | `C:\Program Files\Unity\Hub\Editor\6000.6.5f1` |
| Compile-check scratch project | `C:\Users\mfears\unity-compile-check` (not for playing) |
