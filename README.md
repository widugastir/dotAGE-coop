# DotAge-coop

**Shared-settlement online co-op for [dotAGE](https://store.steampowered.com/app/638510/dotAGE/).**

Build, research, assign pips, survive disasters, and progress through the ages together in a single shared settlement.

> ## ⚠️ **Early Development!**
>
> DotAge-coop is still experimental.
>
> Bugs, desyncs, crashes, incomplete features, and save incompatibilities may occur.
>
> Save files, memories and profile progression can be affected.

**It is strongly recommended to use a secondary profile (B or C) instead of your main profile!**

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


**Model:** the host owns the simulation (RNG, events, commits).

---



## Requirements

- A working **dotAGE** installation (tested on 1.10.4 GOG version)
- **[Steam](https://store.steampowered.com/)** running (even if it's the GOG version)
- **[MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.x** (tested on v0.7.3)

Default Steam AppID is **480 (Spacewar)** so players with a GOG copy of DotAGE can still use Steam networking. 

**Both players must use the same AppID!**

To use the real DotAGE Steam app instead, write `638510` into:

```text
<game>/UserData/DotAgeCoop/steam_appid.txt
```

(and into `<game>/steam_appid.txt` if that file exists).

---



## Install



### 1. Configure Steam AppID

> ⚠️ Both players must use the same AppID or they will not be able to see each other's lobbies.

Edit or create:

```text
<game>/UserData/DotAgeCoop/steam_appid.txt
```

**Steam version of dotAGE:**

- set text of `steam_appid.txt` to `638510`

**GOG version of dotAGE: (default)**

- set text of `steam_appid.txt` to `480`



### 2. MelonLoader

If MelonLoader is not installed yet:

1. Download [MelonLoader](https://github.com/LavaGang/MelonLoader/releases)
2. Extract into the DotAGE folder so `version.dll` and `MelonLoader\` sit next to `dotAge.exe` (or check [Installation](https://github.com/LavaGang/MelonLoader#install))
3. Launch the game once so MelonLoader can generate its folders



### 3. The mod

- Download [realease build](https://github.com/widugastir/dotAGE-coop/releases) and place `DotAgeCoop.dll` into `<game>/Mods/` (create if not exists)



## How to play

1. Both players start **Steam**, then DotAGE with the mod loaded.
2. Press **F8** to open the lobby overlay.
3. **Host:** Create Lobby → **Copy Code** → send the number to your friend.
4. **Client:** paste the code → **Join**.
5. Host starts a **New Game** or **Load Game**. Clients should already be in the lobby.
6. Play the shared settlement. **Pass Turn** is gated so everyone is ready before the night/morning continues.

**Local two-copy testing** (same PC): use Host Local / Join Local in the F8 overlay.

---



## License & credits

- **Mod code:** free to use and fork.
- **dotAGE** is © CKC Games / Michele Pirovano.
- This project is an unofficial fan mod and is not affiliated with the publisher.

