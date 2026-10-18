# Blood Pact

**Online co-op for Castlevania: Belmont's Curse.**
Made by **TheCodesAreReal**.

Blood Pact is a BepInEx mod that lets two players play together online. Each player runs their own game and
their own save; the mod keeps the two worlds in step:

- **See each other:** the other player's Rose appears in your game, fully animated, with their weapon,
  spells, slashes, flying projectiles and explosions. Their name floats above them, and their Rose is tinted
  so you can tell the two apart.
- **Free roam:** each player explores on their own; you meet whenever you are in the same room.
- **Fight together:** in a shared room the player who entered first runs the monsters. The other player sees
  the same monsters doing the same things, can hit and kill them, and gets hit by them. Each monster goes after
  whichever player is closer.
- **Shared resting:** when either player rests at a mirror, the monsters respawn for both of you, Souls style
  (bosses stay dead).

> Status: early and in active development. Everything above is tested with two copies of the game on one PC.
> Boss fights and the Steam connection have not been tested yet.

## Requirements

- Castlevania: Belmont's Curse **1.1.3** on Steam, for both players.
- [BepInEx 6.0.0-be.789](https://builds.bepinex.dev/projects/bepinex_be) (IL2CPP, x64). Older 6.0 pre-releases
  can't read this game's metadata.
- Both players on the same Blood Pact version (the mod refuses a mismatched partner and says so).
- To connect: Steam (friends), or [Tailscale](https://tailscale.com) / any network where the guest can reach the
  host's UDP port.

## Install

1. Install BepInEx be.789 into the game folder and start the game once so it generates its files.
2. Copy `RoseNet.dll` into `BepInEx\plugins\RoseNet\`.
3. Start the game once more; the settings file appears at `BepInEx\config\com.rosenet.tailscale.cfg`.

## Playing

One player hosts, the other joins. Set the role in the settings file (`[Session] Role = Host` or `Guest`) or per
launch with Steam launch options:

| Setting | What it does |
|---|---|
| `--rosenet-role host\|guest` | Who you are this session |
| `--rosenet-transport steam\|udp` | Steam (default) or direct UDP |
| `--rosenet-host <address>` | UDP only: the host's address (for example their Tailscale `100.x.y.z`) |
| `--rosenet-port <port>` | UDP only: session port (default 27852) |

Over Steam the guest finds a hosting friend on their own. Over UDP the guest needs the host's address, and the
host's firewall must allow inbound UDP on the session port.

Each player keeps their own world and save file. (When two copies of the game run on one PC, for testing,
they share one save folder, so the guest copy doesn't write its save there.)

### Display settings

| Setting | Default | |
|---|---|---|
| `[Display] OtherRoseTint` | `#B8CCFF` | Tint of the other player's Rose |
| `[Display] ShowNameTag` | `true` | Name above the other Rose |
| `[Display] ShowStatus` | `true` | Connection line in the top-left corner |

## Building from source

Needs the .NET 9 SDK and the game with BepInEx be.789 installed (the project references the game's generated
interop assemblies; none of them are in this repository).

```
cd Plugin
dotnet build -c Release -p:GameDir="<path to the game folder>"
```

## Credits

- **TheCodesAreReal**: Blood Pact.
- Inspired by **RoseCoop (Shared Curse)**, the local co-op mod for the game. Blood Pact contains none of its
  code; the optional remote-controller mode in [`docs/REMOTE_CONTROLLER.md`](docs/REMOTE_CONTROLLER.md) was built
  to work alongside it.
- Built on [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1),
  [HarmonyX](https://github.com/BepInEx/HarmonyX) (MIT) and
  [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) (LGPL-3.0).

Blood Pact is a fan-made mod, not affiliated with or endorsed by Konami. Castlevania is a trademark of Konami
Digital Entertainment. You need your own copy of the game.
