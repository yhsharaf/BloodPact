# Blood Pact

**Online co-op for Castlevania: Belmont's Curse.**
Made by **TheCodesAreReal**.

Blood Pact is a BepInEx mod that lets two players play the game together online. Each player runs their own
game and their own save; the mod keeps the two worlds in step.

## Features

- **See each other:** the other player's Rose appears in your game, fully animated, with their weapon,
  spells, slashes, projectiles and explosions. Their name floats above them and their Rose is tinted so you can
  tell the two apart. Their screen effects (fades, flashes, shakes) stay on their screen.
- **Free roam:** each player explores on their own; you meet whenever you are in the same room. Your partner is
  shown on the map.
- **Fight together:** in a shared room the player who entered first runs the monsters. The other player sees
  the same monsters doing the same things, can hit and kill them, and gets hit by them. Each monster goes after
  whichever player is closer.
- **Boss fights together:** entering a boss room asks "Fight together?" and brings your partner in. If you fall,
  you watch your partner fight; when the boss falls, the win and its reward count for both of you. If the player
  running the fight falls, the other takes the boss over and it keeps fighting.
- **Shared world:** doors, levers, gates and drawbridges change for both players. Items from a chest your
  partner opens go straight into your inventory, wherever you are.
- **Your own rewards:** EXP statues, health and mana upgrades, stat books and food are per player: each of you
  breaks or picks up your own.
- **Shared resting:** when either player rests at a mirror, the monsters respawn for both of you, Souls style
  (bosses stay dead).
- **Co-op difficulty:** while you are together, monsters have x1.3 health and bosses x1.6 (Elden Ring style,
  adjustable).
- **Help when something goes wrong:** if you can't connect, the screen says why. **F10** saves both players'
  logs in `BepInEx\BloodPact-reports` for a bug report.

See [CHANGELOG.md](CHANGELOG.md) for what changed in each version.

## Requirements

- Castlevania: Belmont's Curse **1.1.3** on Steam, for both players.
- Both players on the same Blood Pact version (the mod tells you if your partner's differs).
- To connect: Steam (be Steam friends), or [Tailscale](https://tailscale.com) as a backup.

## Install

Download the release zip, which contains Blood Pact and the mod loader
([BepInEx 6.0.0-be.789](https://builds.bepinex.dev/projects/bepinex_be), IL2CPP x64; older 6.0 pre-releases
can't read this game).

1. Open the game folder: Steam > right-click the game > Manage > Browse local files.
2. Extract everything in the zip into that folder, next to `Castlevania Belmont's Curse.exe`.
3. The **host** opens `BepInEx\config\com.rosenet.tailscale.cfg` and sets `Role = Host`. The guest changes
   nothing.
4. Start the game. The first start takes a few minutes with a black or frozen window while the mod loader
   prepares itself.

Over Steam, the host loads their save first, then the guest loads theirs; the guest finds the hosting friend on
their own. The top-left corner shows who you are playing with.

## Settings

In `BepInEx\config\com.rosenet.tailscale.cfg`:

| Setting | Default | |
|---|---|---|
| `[Session] Role` | `Guest` in the release | `Host` or `Guest` |
| `[Session] Transport` | `Steam` | `Steam`, or `Udp` for Tailscale |
| `[Session] UdpHostAddress` | | Tailscale only: the host's address (`100.x.y.z`) |
| `[Session] UdpPort` | `27852` | Tailscale only: the host allows this UDP port in its firewall |
| `[Difficulty] Enabled` | `true` | Co-op monster and boss health; the factors are in the same section |
| `[Display] OtherRoseTint` | `#B8CCFF` | Tint of the other player's Rose |
| `[Display] ShowNameTag` | `true` | Name above the other Rose |
| `[Display] ShowStatus` | `true` | Connection line in the top-left corner |

The same can be set per launch: `--rosenet-role host|guest`, `--rosenet-transport steam|udp`,
`--rosenet-host <address>`, `--rosenet-port <port>`.

## Reporting a bug

Press **F10** right after it happens (either player). Open an issue with what happened, which version you both
run, and the folder it created in `BepInEx\BloodPact-reports` (it holds both players' logs).

## Building from source

Needs the .NET 9 SDK and the game with BepInEx be.789 installed and started once (the project references the
game's generated interop assemblies; none of them are in this repository).

```
cd Plugin
dotnet build -c Release -p:GameDir="<path to the game folder>"
```

`-p:Deploy=true` also copies the built DLL into the game's `BepInEx\plugins\RoseNet` folder.

### Self-test

`tools\selftest.ps1` plays scenarios automatically on two copies of the game on one PC (host and guest over
UDP), never writing saves, and prints PASS/FAIL results. For example:

```
powershell -ExecutionPolicy Bypass -File tools\selftest.ps1 -Scenario all -Slot 0
```

Scenarios include boss fights (`both-up-win`, `partner-down-win`, `owner-down-win`, with `-Boss` and `-Level`
to pick the boss), `monsters`, `zombies`, `chest`, `statue`, `bridges` and `doors`. `-GuestFirst` sends the guest
into the boss room first; `-Lag` and `-Loss` simulate an internet connection. Don't touch the game windows
while it runs. Logs of every run are kept in `BepInEx\BloodPact-test-logs`.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Credits

- **TheCodesAreReal**: Blood Pact.
- Inspired by **RoseCoop (Shared Curse)**, the local co-op mod for the game. Blood Pact contains none of its
  code; the optional remote-controller mode in [`docs/REMOTE_CONTROLLER.md`](docs/REMOTE_CONTROLLER.md) was built
  to work alongside it.
- Built on [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1),
  [HarmonyX](https://github.com/BepInEx/HarmonyX) (MIT) and
  [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) (LGPL-3.0).

Blood Pact is released under the [MIT License](LICENSE). It is a fan-made mod, not affiliated with or endorsed
by Konami. Castlevania is a trademark of Konami Digital Entertainment. You need your own copy of the game.
