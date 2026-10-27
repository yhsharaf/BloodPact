* Version 0.2.6
   * Fixed a crash for the host when a boss appeared while playing together (first reported at Leon)
* Version 0.2.5
   * Fixed a bug that caused drawbridges to be raised for one player and lowered for the other
* Version 0.2.4
   * EXP statues are now per player: each player breaks their own and gets their own EXP
   * Stat items (health and mana upgrades, attack and defence books, food) are now per player: each player picks up their own
   * Fixed a bug that could leave a frozen copy of a monster in the room for the second player
* Version 0.2.3
   * Fixed a bug that caused bosses to stand still after the player who entered the fight first died
   * Fixed a bug that could cause Agnes to freeze mid-dash after a player died
   * Fixed a bug that caused Mammon's and Dracula's arenas to not be treated as boss rooms (no "Fight together?" prompt, no partner call, no spectating)
   * A boss taken over after a player's death now continues in the same phase
   * Connection help: if you can't connect, the screen now tells you why (both set to Host, mod not running for your friend, different versions, no friend hosting)
   * Disconnecting now resets everything cleanly, nothing carries over into the next session
   * An error in one part of the mod can no longer stop the rest
   * Reconnecting while dead now tells your partner
   * Resting is only shared while you are connected
* Version 0.2.2
   * Fixed a bug that caused the second player to be frozen after joining a boss fight
   * Fixed a bug that caused the player who died to be unable to pick up the boss reward
   * The player who died now respawns at the mirror after a shared win; the boss falls for them when they return, and drops their reward
   * Fixed a bug that caused invisible swords and frozen bosses after casting a spell
   * Fixed a bug that could cause Leon's phase change to fail
   * Chest items now reach your partner instantly, wherever they are
   * Added a bug report key (F10): saves both players' logs to BepInEx\BloodPact-reports
   * Added a warning when the two players run different builds
* Version 0.2.1
   * Steam is now the default connection (Tailscale still supported)
   * Simpler install: extract the zip straight into the game folder
   * Fixed a bug that caused Leon to disappear mid-fight
   * Fixed a bug that caused players to be unable to move when Leon entered his second phase
   * Fixed a bug that caused the boss to freeze when a player died
   * Fixed a bug that caused the boss to come back to life, or drop no reward, after a shared win
   * Fixed the HUD missing after a win
   * Pausing no longer stops the world for both players
   * Fixed a black screen when your partner opened a chest or entered a house
   * Your partner's screen effects (spike fall fade, hit flashes, camera shake) no longer play on your screen
   * Boss calls now only happen within the same area
* Version 0.2.0
   * Initial release (Tailscale)
   * 2 player online co-op, each player in their own game and save
   * Shared monsters, doors, levers and gates
   * Shared resting at mirrors
   * Boss fights together: "Fight together?" prompt, spectate when you fall, shared win and reward
   * Co-op difficulty: monsters x1.3 health, bosses x1.6 while both players are in the room (adjustable in the settings file)
   * Partner shown on the map
