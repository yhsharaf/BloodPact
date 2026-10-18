# RoseNet 0.1: Online Co-op over Tailscale (input layer)

RoseNet sends your friend's controller over Tailscale into **your** game. RoseCoop then sees it as
an extra local controller, so your friend joins as player 2, 3 or 4 just as if the pad were plugged into your PC.
Your friend watches the game through a stream (see step 3).

> This is milestone 1. Your friend's own copy of the game isn't used yet. Letting each of you run
> your own copy is the next milestone (see `..\NETCODE_FEASIBILITY.md`).

## What goes where

| PC | Needs |
|---|---|
| **Host** (you, runs the game) | BepInEx 6 **be.789** + RoseCoop + `RoseNet.dll` · Tailscale · a streaming host (Sunshine) |
| **Guest** (your friend) | `RoseNetSender.exe` · Tailscale · a streaming client (Moonlight) · an Xbox-compatible controller |

## 1. Host setup (already done on this PC)
1. BepInEx must be **6.0.0-be.789 or newer** from <https://builds.bepinex.dev/projects/bepinex_be>
   (`BepInEx-Unity.IL2CPP-win-x64-…zip`). The GitHub release 6.0.0-pre.2 can't read this game.
2. Plugins in `<game>\BepInEx\plugins\`:
   - `RoseCoop\RoseCoop.dll`
   - `RoseNet\RoseNet.dll` (from `dist\Host\BepInEx\plugins\RoseNet\`)
3. Start the game. `<game>\BepInEx\LogOutput.log` should contain:
   `Listening for guests on UDP port 27850 … Tailscale IPs of this PC: 100.x.y.z`
   **Give your friend that 100.x.y.z address.**
4. **Firewall.** Windows may ask whether to allow the game on networks the first time. If it doesn't,
   or your friend gets stuck on "waiting for host", run this once in an **admin** PowerShell:
   ```powershell
   New-NetFirewallRule -DisplayName "RoseNet (Tailscale)" -Direction Inbound -Protocol UDP -LocalPort 27850 -RemoteAddress 100.64.0.0/10 -Action Allow
   ```

## 2. Tailscale
Both PCs must be on the same tailnet. Either:
- you both sign in to the same Tailscale account, or
- you share your PC with your friend's account: Tailscale admin console → Machines → your PC → **Share…**

Check it from the guest PC with `tailscale ping 100.x.y.z`.

## 3. Streaming the picture (pick one)
- **Sunshine + Moonlight (recommended, free, low latency).** Install Sunshine on the host and Moonlight
  on the guest. In Moonlight, add the host by its Tailscale IP. Turn **off** Moonlight's own gamepad
  passthrough (Settings → Input), otherwise your friend's pad arrives twice.
- **Parsec.** Also works; again, turn off Parsec's controller forwarding for the guest.
- **Discord screen share.** Works for testing, but it's laggy.

## 4. Guest: send the controller
1. Plug in an Xbox (or other XInput) controller. **PlayStation / Switch pads** need an XInput wrapper,
   for example DS4Windows.
2. Run `RoseNetSender.exe` and type the host's Tailscale IP, or start it with the IP directly:
   ```
   RoseNetSender.exe 100.x.y.z
   ```
   Options: `--port 27850` (must match the host) and `--pad 2` (use the 2nd controller instead of the first one found).
3. When it says **"Connected! You are remote pad 1"**, keep the window open. It keeps working while
   Moonlight has focus.

## 5. Play
On the host: main menu → **Multiplayer** → **Co-op**. Your friend presses **A** on their own controller
to join, picks a look, then presses A again to get ready. Everything else works the way the RoseCoop README describes.

If the sender is restarted within a few seconds, the guest gets the same pad back and keeps their Rose.

## Settings (`<game>\BepInEx\config\com.rosenet.tailscale.cfg`)
| Key | Default | Meaning |
|---|---|---|
| `[Network] Enabled` | true | Turn RoseNet off without uninstalling it |
| `[Network] Port` | 27850 | UDP port; the sender's `--port` must match |
| `[Network] AllowNonTailscale` | false | Only accepts Tailscale addresses and this PC. Leave off. |
| `[Remote] MaxRemotePads` | 3 | Number of guests (RoseCoop maxes out at 4 players) |
| `[Remote] NeutralAfterSeconds` | 0.5 | Releases a guest's buttons when their packets stop |
| `[Remote] ReconnectTakeoverSeconds` | 3 | A reconnecting guest reclaims a pad silent for this long |
| `[Debug] LogInput` | false | Logs every button change for bug reports |

## Troubleshooting
| Sender says | Fix |
|---|---|
| `waiting for host...` | Is the game running with RoseNet (check the log line in step 1.3)? Right IP? Firewall rule (step 1.4)? Does `tailscale ping` work? |
| `Host is full` | Three guests are already connected, or raise `MaxRemotePads`. |
| `No Xbox-compatible controller found` | Plug in an XInput pad, or run DS4Windows for PlayStation pads. |
| Connected, but nothing happens in game | Players can only join from the RoseCoop join screen (Multiplayer → Co-op). |

## Known limits of 0.1
- No rumble back to the guest.
- Keyboard and mouse on the guest side aren't sent; only a controller.
- Input travels about one streaming-latency hop. Fine for co-op, noticeable in Versus.

## Building from source
```
dotnet build RoseNet\Plugin -c Release -p:Deploy=true
dotnet publish RoseNet\Sender -c Release -o RoseNet\dist\Guest
```
The plugin compiles against the game's generated interop assemblies (`-p:GameDir=...` if the game
is installed somewhere else). The sender is a self-contained single exe.
