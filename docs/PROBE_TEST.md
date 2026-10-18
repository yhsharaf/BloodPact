# RoseNet probe test (before milestone 2)

**Goal:** find out whether the same room produces the same enemies, in the same order, on both PCs.
If it does, the enemies can be synced online. You **don't** need to be connected to each other for this test;
each of you plays alone.

## 1. Install
**Host (you):** already done. The probe is switched on in your game.

**Friend:**
1. Update the game in Steam. Both of you must be on the **same version**; the probe log shows it (`game 1.1.3` on the host).
2. Unzip `RoseNet-ProbeKit-0.1.0.zip` **into the game folder**
   (Steam → right-click the game → Manage → Browse local files). Afterwards `winhttp.dll` and the
   `BepInEx` folder sit next to `Castlevania Belmont's Curse.exe`.
3. Start the game once and wait. The first start with BepInEx takes a few minutes while a console
   window prepares the files. Don't close it.

The kit has the probe **on** and RoseNet's network listener **off**. It doesn't change the game otherwise
(RoseCoop isn't included and isn't needed for this test).

## 2. Play the same route
Both of you do exactly this:
1. Main menu → **New Game** in an **empty save slot**, so you don't overwrite a real save.
2. Play the opening and walk through the **first 5–10 rooms along the same route**.
   Kill some enemies and skip none of the rooms on that route.
3. Save once at the first save point, then quit to desktop.

The same route on both PCs matters more than speed. If one of you goes back through a room, write it down.

## 3. Send the results
From the game folder, send:
- `BepInEx\RoseNetProbe\probe_<date>_<time>.log` (the newest one)
- `BepInEx\LogOutput.log`

## What I look for
Each line of the log is one event:
```
ROOM (x,y)
SPAWN room=(x,y) spawner=<scene path>@(position) n=<nth spawn> mob=<enemy> pos=(x,y,z) frame=+<frames after entering>
DEATH room=(x,y) mob=<enemy> id=<spawner#n> pos=(x,y,z)
```
- **Same `ROOM` chunk numbers on both:** room sync is easy.
- **Same spawner paths and positions, and the same mob per `n`:** enemies can be identified across PCs. This is the go/no-go.
- **`# save file` lines in the header:** tell me which file is the story save, so we can copy your save to your friend.

To switch the probe off later: set `[Probe] Enabled = false` in `BepInEx\config\com.rosenet.tailscale.cfg`.
