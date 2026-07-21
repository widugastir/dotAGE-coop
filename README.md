# DotAgeCoop

> ## ⚠️ Work in progress
>
> <strong>This mod is not finished.</strong> Co-op may have <strong>bugs</strong>, <strong>desyncs</strong>, crashes, and incomplete features. Feedback and bug reports are welcome.
>
> <span style="color:#c62828"><strong>Use at your own risk; saves and sessions can break.</strong> Memories and other profile data can also be affected. Prefer a non-main game profile for the mod (e.g. <strong>B</strong> or <strong>C</strong>) — you can switch profiles in-game.</span>

**Shared-settlement online co-op for [dotAGE](https://store.steampowered.com/app/638510/dotAGE/).**

Play the same village together: build, order pips, research, and survive the ages on one host-authoritative world. Lobby flow is simple — create a lobby, copy a code, friend joins.

---

## Features


| Area            | What syncs                                                          |
| --------------- | ------------------------------------------------------------------- |
| **Lobby**       | Steam lobbies by code, or local TCP for two copies on one PC        |
| **World**       | Buildings, resources, food bans, terrain changes                    |
| **Pips**        | Orders, appearance, morning rosters                                 |
| **Progression** | Research, memories, scales                                          |
| **Events**      | Omens, arrivals, boons — host rolls; clients mirror                 |
| **Turns**       | Shared Pass Turn + morning barriers (everyone ready → continue)     |
| **Dialogue**    | Cutscenes / tutorials advance when all players click through        |
| **QoL**         | Live cursors, in-game chat, New Game / Load Game with save transfer |


**Model:** the host owns the simulation (RNG, events, commits). Clients send intents and apply snapshots — one shared village, not two parallel runs.

---

## Requirements

- A working **dotAGE** install (Unity 2022.3 Mono)
- **[Steam](https://store.steampowered.com/)** running (GOG build is fine for networking)
- **[MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.x**

Default Steam AppID is **480 (Spacewar)** so players without a Steam copy of DotAGE can still use Steam networking. **Both players must use the same AppID.**

To use the real DotAGE Steam app instead, write `638510` into:

```text
<game>/UserData/DotAgeCoop/steam_appid.txt
```

(and into `<game>/steam_appid.txt` if that file exists).

---

## Install

### 1. MelonLoader

If MelonLoader is not installed yet:

1. Download [MelonLoader.x64.zip](https://github.com/LavaGang/MelonLoader/releases)
2. Extract into the DotAGE folder so `version.dll` and `MelonLoader\` sit next to `dotAge.exe`
3. Create a `Mods\` folder if it is missing
4. Launch the game once so MelonLoader can generate its folders

### 2. The mod

Either:

- **Release build:** place `DotAgeCoop.dll` into `<game>/Mods/`, or
- **From source:**

```powershell
dotnet build src\DotAgeCoop.Mod\DotAgeCoop.Mod.csproj -c Release
```

On success the DLL is copied to your configured DotAGE `Mods` folder.

Override the game path:

```powershell
dotnet build -c Release -p:DotAgeDir="D:\Games\dotAGE"
```

---

## How to play

1. Both players start **Steam**, then DotAGE with the mod loaded.
2. Press **F8** to open the lobby overlay.
3. **Host:** Create Lobby → **Copy Code** → send the number to your friend.
4. **Client:** paste the code → **Join**.
5. Host starts a **New Game** or **Load Game**. Clients should already be in the lobby.
6. Play the shared settlement. **Pass Turn** is gated so everyone is ready before the night/morning continues (clients can request Pass Turn; the host still drives the turn).

**Local two-copy testing** (same PC): use Host Local / Join Local in the F8 overlay, or the scripts under `tools/`:

```powershell
powershell -ExecutionPolicy Bypass -File tools\coop-test-newgame.ps1 -KillExisting
powershell -ExecutionPolicy Bypass -File tools\coop-test-loadgame.ps1 -KillExisting
```

---

## Status

Working well enough for co-op runs:

- Host-authoritative village sync (buildings, pips, research, events, food)
- Turn / morning / dialogue ready gates
- Synced New Game and Load Game (with peer load-wait)
- Steam code lobbies + local lobby for development

Still early / incomplete:

- Some protocol scaffolding unused in sync
- Lobby UI is in-game IMGUI (F8), not a separate proxy window
- Desync recovery and edge-case polish ongoing

Contributor notes (architecture, invariants, known bugs): see `[Docs/](Docs/)`.

---

## Architecture (short)


| Piece                                     | Role                                             |
| ----------------------------------------- | ------------------------------------------------ |
| `SteamLobbyService` / `LocalLobbyService` | Lobby create/join + transport                    |
| `CoopSession`                             | Message routing, chat, host/client roles         |
| `*SyncService`                            | Turn, events, pips, research, load, bootstrap, … |
| Harmony hooks                             | Gate vanilla input / inject synced commits       |
| `LobbyOverlay`                            | F8 IMGUI lobby UI                                |


---

## License & credits

- **Mod code:** free to use and fork for the DotAgeCoop project.
- **dotAGE** is © [CKC Games](https://ckcgames.com/) / Michele Pirovano — this project is an unofficial fan mod and is not affiliated with the publisher.

Enjoy the village together.