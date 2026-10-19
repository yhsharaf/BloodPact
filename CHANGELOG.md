# Blood Pact changelog

Both players must always run the same version of Blood Pact. When you update, your partner updates too.

---

## 0.2.3

**Boss fights where the player running the fight dies, and bosses the mod didn't recognise.**

### Fixed
- **A boss stopped moving when the player who entered first died.**
  - **What you saw:** the surviving player kept hitting the boss, but it stood still and never attacked.
  - **Why:** a boss's intro locks its controls until the intro ends. On the second player's side the intro never fully plays, so the lock stayed on. When the survivor took the boss over, it stayed locked forever.
  - **Now:** the lock is cleared and the boss keeps fighting. Tested on Jeanne, Carmilla, Agnes and both forms of Dracula.
- **Agnes could freeze mid-dash after a takeover.**
  - **Why:** the dash waits for a signal from the game of the player who died.
  - **Now:** the stuck move is stopped and she picks her next attack.
- **Mammon's and Dracula's arenas weren't treated as boss rooms.**
  - **What you saw:** no "Fight together?" prompt, your partner wasn't called in, and after dying you didn't watch the fight.
  - **Why:** the mod recognised boss rooms by their name, and these two are named differently.
  - **Now:** all of that works in every boss arena.
- **A boss taken over after a death could restart in the wrong phase.**
  - **Now:** the boss's phase and state are shared, so it carries on where it was.

### New
- **Connection help on screen.** When the two games can't connect, the mod tells you why instead of just waiting:
  - you're both set to Host (or both to Guest);
  - your friend's game doesn't have the mod running;
  - you have different versions;
  - no Steam friend is hosting (friends list, Invisible status, save not loaded);
  - for Tailscale: no answer from the host's address, with what to check.
  - Added because some players couldn't connect and had no idea why.

### Stability
- If your partner disconnects, everything goes back to normal (monster health, boss data, rest data), and nothing carries over into the next session.
- One error in one part of the mod can no longer stop the rest, and a broken message can't end the session.
- Reconnecting while dead tells your partner, so they don't wait for you.
- Resting is only shared while you're actually connected.

### Tested
- Tested automatically on two copies of the game: Leon, Jeanne, Medusa, Mammon, Carmilla, Agnes and Dracula (both forms).
- Three cases each: both win together, the player who entered first dies, the second player dies.
- Also tested: monsters (same monsters in the same places, kills and hits shared) and switches.

---

## 0.2.2

**The "frozen after a boss fight" bugs, chests, and a bug-report key.**

### Fixed
- **The second player was frozen after joining a boss fight.**
  - **Why:** being teleported in skipped the arena entrance that starts the boss's intro, so their copy of the boss stayed in its intro pose, which locks the player's controls.
  - **Now:** the entrance is triggered for them on arrival.
- **The player who died couldn't pick up the boss reward.**
  - **Why:** their copy of the boss was still being driven by the other game, which blocked its death and its reward.
  - **Now:** it is released and dies properly.
- **The player who died now respawns normally at the mirror** after their partner wins (no more getting up in place, which caused stuck screens). When they return to the boss room, the boss falls for them and leaves its reward.
  - Before this, the reward could appear before their own fight had started, and the game got stuck in a fight that never ended.
- **Bosses aim their phase-change moves at a real player.** Leon's phase change failed when aimed at the partner's stand-in.
- **Invisible swords and frozen bosses after a spell:** the fix for screen effects (0.2.1) changed a shared game file. It now only changes the effect while it plays.

### New
- **Chests reach your partner anywhere.** When you open a chest, your partner gets its items at once, wherever they are in the world. Before, they only got them when they walked into that room.
- **F10 bug report:** saves your log and your partner's log together in `BepInEx\BloodPact-reports`. Send that folder when reporting a bug.
- **Version warning:** a warning shows if the two of you run different builds of the mod.

---

## 0.2.1

**First version for Steam, plus fixes from the first real online sessions.**

### New
- **Steam is now the default connection.** Just be Steam friends, set one player to Host, and launch. Tailscale still works as a fallback.
- **Simpler install:** the zip's contents go straight into the game folder.

### Fixed
- **Leon disappeared mid-fight.**
  - **Why:** his jump attacks looked for platform data on the partner's stand-in, which didn't have it, so the move broke and Leon vanished.
  - **Now:** the stand-in has it.
- **Couldn't move when Leon entered his second phase.** Same cause as above.
- **The boss froze when your partner died.** The survivor now takes the fight over and the boss carries on.
- **The boss came back to life, or the reward didn't drop, after a shared win.** The other player's copy of the boss now gets the game's real death, so the fight ends and the reward drops for both.
- **HUD missing after getting up after a win:** fixed.
- **Pausing stopped the world for both players:** the pause menu no longer stops time while playing together (you can still open it).
- **Black screen when your partner opened a chest or entered a house:**
  - partner chests now give their item without replaying the opening;
  - background doors are only saved, not played on your screen.
- **Your partner's screen effects played on your screen** (the fade when falling into spikes, hit flashes, camera shake): they now stay on their screen.
- **Boss calls stay within the same area** (you're no longer pulled across the map), and the "Joining the boss fight" message always clears.

---

## 0.2.0

**First public release (Tailscale).**

- Two players, each in their own game and save, see each other and play together.
- Monsters are shared: the first player to enter a room runs it. Both can hit them and be hit, and monsters go after whichever player is closer.
- Doors, levers and gates are shared.
- Resting at a mirror is shared, Souls-style.
- **Boss fights together:**
  - a "Fight together?" prompt brings your partner in;
  - you watch the fight if you fall;
  - the win and the reward count for both.
- Co-op difficulty (Elden Ring style): monsters ×1.3 health, bosses ×1.6, while both of you are in the room. You can change this in the settings file.
- Your partner is shown on the map.
