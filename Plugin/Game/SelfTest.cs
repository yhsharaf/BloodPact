using System;
using System.Collections;
using System.IO;
using MotherBase.EntityFramework;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace RoseNet.Game
{
    // Self-test for two copies on one PC: launch both with --rosenet-test all (plus the usual --rosenet-role and
    // --rosenet-transport udp). Each copy loads its last save, goes to Leon's arena, plays the boss scenarios and
    // writes PASS/FAIL lines to BepInEx\BloodPact-test\<role>-results.txt. Nothing is saved while testing (every
    // scenario starts from the same save). The copies keep in step through flag files in that folder.
    internal static class SelfTest
    {
        internal static readonly string Scenario = CommandLine.Value("rosenet-test");
        internal static bool Active => !string.IsNullOrEmpty(Scenario);

        // Set by the session.
        internal static Func<bool> Connected = () => false, PartnerDead = () => false, OwnRoom = () => false;
        internal static bool IsHost;

        const string LeonRoom = "World_Biome_01_Streets_Poor_StreetsPoor_Room_Boss_Leon_MainRoom_uid80";
        static readonly Vector3 LeonArena = new Vector3(176.5f, -37.4f, 0f);

        static string Dir => Path.Combine(BepInEx.Paths.BepInExRootPath, "BloodPact-test");
        static string Role => IsHost ? "host" : "guest";
        static string Other => IsHost ? "guest" : "host";

        static IEnumerator run;
        static Func<bool> waitFor;
        static float waitUntil, waitStarted;
        static string waitWhat;
        static bool started;
        static int passed, failed;

        // ---- results ----

        static void Log(string text)
        {
            Plugin.Logger.LogInfo($"[Test] {text}");
            try { File.AppendAllText(Path.Combine(Dir, $"{Role}-results.txt"), $"{DateTime.Now:HH:mm:ss} {text}\r\n"); } catch { }
        }

        static void Check(string name, bool ok, string detail = "")
        {
            if (ok) passed++; else failed++;
            Log($"{(ok ? "PASS" : "FAIL")} {name}{(string.IsNullOrEmpty(detail) ? "" : ": " + detail)}");
        }

        // ---- waiting ----

        // yield return Until(...): wait for a condition (or the timeout).
        static object Until(Func<bool> cond, float timeout, string what)
        {
            waitFor = cond; waitStarted = Time.unscaledTime; waitUntil = Time.unscaledTime + timeout; waitWhat = what;
            return null;
        }
        static object Seconds(float s) => Until(() => false, s, null);

        static void Flag(string step)
        {
            try { File.WriteAllText(Path.Combine(Dir, $"{Role}.{step}"), DateTime.Now.ToString("o")); } catch { }
        }
        static bool HasFlag(string who, string step) => File.Exists(Path.Combine(Dir, $"{who}.{step}"));
        // Both copies reach the same point before going on.
        static object Barrier(string step, float timeout = 180f) { Flag(step); return Until(() => HasFlag(Other, step), timeout, $"the {Other} to reach '{step}'"); }

        internal static void Tick()
        {
            if (!Active) return;
            if (!started)
            {
                started = true;
                try
                {
                    Directory.CreateDirectory(Dir);
                    foreach (var f in Directory.GetFiles(Dir, Role + ".*")) File.Delete(f);
                    File.Delete(Path.Combine(Dir, $"{Role}-results.txt"));
                    // Both copies share one screen: keep reading our test pad while the other window has focus.
                    var prop = typeof(InputSettings).GetProperty("backgroundBehavior");
                    if (prop != null) prop.SetValue(InputSystem.settings, Enum.ToObject(prop.PropertyType, 2));   // IgnoreFocus
                    Application.runInBackground = true;
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[Test] setup: {e.Message}"); }
                Log($"Self-test '{Scenario}' as {Role}, build {Session.DebugReport.BuildId}.");
                run = Main();
            }
            if (run == null) return;
            if (waitFor != null)
            {
                bool done;
                try { done = waitFor(); } catch { done = false; }
                if (!done && Time.unscaledTime < waitUntil) return;
                if (!done && waitWhat != null) Log($"(timed out after {Time.unscaledTime - waitStarted:0}s waiting for {waitWhat})");
                waitFor = null;
            }
            try
            {
                if (!run.MoveNext()) { run = null; Log($"Done: {passed} passed, {failed} failed."); Flag("finished"); }
            }
            catch (Exception e) { Log($"FAIL test crashed: {e}"); run = null; Flag("finished"); }
        }

        // ---- game helpers ----

        static Entity Me => GameAccess.MainCharacter();
        static string Room => GameAccess.CurrentRoomId();
        static bool InGame() => Me != null && !GameAccess.IsLoadingRoom && !string.IsNullOrEmpty(Room) && !LevelLoading();
        static bool LevelLoading() { try { return MotherBase.ApplicationFlowManager.ApplicationFlowManager._isLoading; } catch { return false; } }
        static bool Alive() { var d = Me != null ? MobControl.DamageableOf(Me) : null; return d != null && d.isAlive && d.currentHealth > 0; }

        static Entity Boss()
        {
            foreach (var kv in MobRegistry.ById)
            {
                var mob = kv.Value;
                try
                {
                    if (mob != null && MobRegistry.InRoom(kv.Key, Room) && mob.GetComponent<BossComponent>() != null) return mob;
                }
                catch { }
            }
            return null;
        }
        static bool BossAlive() { var b = Boss(); return b != null && MobRegistry.IsAlive(b); }
        static bool BossInIntro()
        {
            var list = Boss()?.GetComponent<MotherBase.ActionFramework.Actor>()?.activeActions;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) if (list[i].data != null && list[i].data.name.Contains("Intro")) return true;
            return false;
        }

        static void SkipDialogue()
        {
            try { foreach (var r in Object.FindObjectsOfType<CutsceneLineReader>()) r.HandleUserSkipped(new InputAction.CallbackContext()); } catch { }
        }

        static void Die()
        {
            var me = Me; var d = me != null ? MobControl.DamageableOf(me) : null;
            if (d == null) return;
            d.currentHealth = 0;
            d.Die(me, me, new DamageData());
        }

        static void KillBoss()
        {
            var b = Boss(); var d = b != null ? MobControl.DamageableOf(b) : null;
            if (d == null) { Log("no boss to kill here"); return; }
            if (MobControl.IsPossessed(b))
            {
                if (MobRegistry.IdOf.TryGetValue(b.Pointer, out var id)) MobControl.OutgoingHits.Add((id, d.currentHealth));
            }
            else { d.currentHealth = 0; d.Die(b, Me, new DamageData()); }
        }

        // Can our Rose actually move? Push a virtual gamepad stick right, then left, and see if she went anywhere.
        static InputDevice pad;
        static float probeX;
        static void Stick(short lx)
        {
            try
            {
                if (pad == null || !pad.added) pad = InputSystem.AddDevice("Gamepad", "RoseNet Test Pad", null);
                GamepadEvents.Queue(pad, new PadInput { LX = lx });
            }
            catch (Exception e) { Log($"(virtual pad: {e.Message})"); }
        }

        // Dialogue moves on while the confirm button is held (Space on the keyboard, A on a controller).
        static IEnumerator HoldThroughDialogue()
        {
            if (!StateName().Contains("Dialogue")) yield break;
            float until = Time.unscaledTime + 60f;
            try { if (pad == null || !pad.added) pad = InputSystem.AddDevice("Gamepad", "RoseNet Test Pad", null); } catch { }
            while (Time.unscaledTime < until && StateName().Contains("Dialogue"))
            {
                try { GamepadEvents.Queue(pad, new PadInput { Buttons = XInputButtons.A }); } catch { }
                SkipDialogue();
                yield return Seconds(0.5f);
            }
            try { GamepadEvents.Queue(pad, default); } catch { }
            yield return Seconds(0.5f);
            Log($"Dialogue over: state {StateName()}.");
        }

        static IEnumerator MoveProbe(string name)
        {
            var talk = HoldThroughDialogue();
            while (talk.MoveNext()) yield return talk.Current;
            if (StateName().Contains("Menu"))
            {
                Log($"({name}: skipped, a game menu (the reward popup) waits for a press a background window can't give)");
                yield break;
            }
            if (Me == null) { Check(name, false, "no Rose"); yield break; }
            probeX = Me.transform.position.x;
            Stick(32767); yield return Seconds(0.8f);
            Stick(0); yield return Seconds(0.2f);
            float right = Me != null ? Me.transform.position.x - probeX : 0f;
            probeX = Me != null ? Me.transform.position.x : probeX;
            Stick(-32767); yield return Seconds(0.8f);
            Stick(0); yield return Seconds(0.2f);
            float left = Me != null ? Me.transform.position.x - probeX : 0f;
            bool moved = Mathf.Abs(right) > 0.5f || Mathf.Abs(left) > 0.5f;
            Check(name, moved, $"moved {right:0.0} right, {left:0.0} left; {Diagnose()}");
        }

        static string PathOf(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            for (var p = t.parent; p != null && sb.Length < 200; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        static bool GameplayState()
        {
            try { var st = GameStateManager.GetInstance()?.activeState; return st != null && !st.disablePlayerInput; } catch { return false; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        static void FocusOurWindow()
        {
            try
            {
                var h = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                keybd_event(0x12, 0, 0, UIntPtr.Zero);       // Alt down/up: Windows then lets us take the foreground
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                SetForegroundWindow(h);
            }
            catch { }
        }

        static string StateName() { try { var st = GameStateManager.GetInstance()?.activeState; return st != null ? st.name : "none"; } catch { return "?"; } }

        // The boss reward: the game waits in its absorption state while the player holds a button. Try the face
        // buttons one after another until the state ends.
        static IEnumerator Absorb(string name)
        {
            // A boss's last words: dialogue lines wait for a press.
            var talk = HoldThroughDialogue();
            while (talk.MoveNext()) yield return talk.Current;
            for (int round = 0; round < 6 && (StateName().Contains("Absorption") || StateName().Contains("Menu")); round++)
            {
                var once = AbsorbOnce(name);
                while (once.MoveNext()) yield return once.Current;
            }
            Log($"After the reward: state {StateName()}.");
        }

        static IEnumerator AbsorbOnce(string name)
        {
            if (!StateName().Contains("Absorption") && !StateName().Contains("Menu")) { Log($"(no reward to absorb: state {StateName()})"); yield break; }
            try
            {
                var gsm = GameStateManager.GetInstance();
                var st = gsm.activeState;
                var maps = st.activeActionMaps;
                var names = new System.Collections.Generic.List<string>();
                if (maps != null) foreach (var m in maps) names.Add(m);
                Log($"Absorption state maps: {string.Join(",", names)}");
                var asset = gsm.inputActionAsset;
                foreach (var m in asset.actionMaps)
                {
                    if (!m.enabled) continue;
                    var acts = new System.Collections.Generic.List<string>();
                    foreach (var a in m.actions)
                    {
                        var b = new System.Collections.Generic.List<string>();
                        foreach (var bind in a.bindings) if (bind.effectivePath != null && bind.effectivePath.Contains("Gamepad")) b.Add(bind.effectivePath);
                        acts.Add($"{a.name}[{string.Join(" ", b)}]");
                    }
                    Log($"  map {m.name}: {string.Join(", ", acts)}");
                }
            }
            catch (Exception e) { Log($"(absorption dump: {e.Message})"); }
            ushort[] buttons = { XInputButtons.RightShoulder };   // "Interact" (hold)
            foreach (var b in buttons)
            {
                if (!StateName().Contains("Absorption")) break;
                try { if (pad == null || !pad.added) pad = InputSystem.AddDevice("Gamepad", "RoseNet Test Pad", null); GamepadEvents.Queue(pad, new PadInput { Buttons = b }); } catch { }
                yield return Until(() => !StateName().Contains("Absorption"), 12f, null);
                try { GamepadEvents.Queue(pad, default); } catch { }
                yield return Seconds(0.5f);
                if (!StateName().Contains("Absorption")) Log($"Absorbed the reward with button {b:X}.");
            }
            if (StateName().Contains("Absorption"))
            {
                // Why not? What there is to absorb around us, and what our Rose is doing.
                try
                {
                    var near = new System.Collections.Generic.List<string>();
                    foreach (var e in Object.FindObjectsOfType<Entity>())
                    {
                        if (e == null || Me == null) continue;
                        string en = e.name;
                        if (en.IndexOf("Loot", StringComparison.OrdinalIgnoreCase) < 0 && en.IndexOf("Absorb", StringComparison.OrdinalIgnoreCase) < 0 && en.IndexOf("Deck", StringComparison.OrdinalIgnoreCase) < 0 && en.IndexOf("Arcana", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        near.Add($"{en} at {Vector3.Distance(e.transform.position, Me.transform.position):0.0} ({(e.gameObject.activeInHierarchy ? "on" : "off")})");
                    }
                    var acts = new System.Collections.Generic.List<string>();
                    var list = Me?.GetComponent<MotherBase.ActionFramework.Actor>()?.activeActions;
                    if (list != null) for (int i = 0; i < list.Count; i++) acts.Add(list[i].data != null ? list[i].data.name : "?");
                    Log($"Still absorbing. Loot near us: {(near.Count == 0 ? "none" : string.Join("; ", near))}. Rose runs: {string.Join(" ", acts)}");
                }
                catch (Exception e) { Log($"(absorb dump: {e.Message})"); }
            }
            yield return Until(() => !StateName().Contains("Absorption"), 10f, "the reward sequence to end");
            if (StateName().Contains("Absorption")) Check(name, false, $"state {StateName()}");
            // The reward screen ("item obtained") waits for a confirm press, and menus only listen to the
            // focused window (two copies share the screen): bring ours to the front first.
            if (StateName().Contains("Menu")) FocusOurWindow();
            for (int i = 0; i < 8 && StateName().Contains("Menu"); i++)
            {
                yield return Seconds(1.5f);
                FocusOurWindow();
                ushort b = i % 2 == 0 ? XInputButtons.A : XInputButtons.B;
                try { GamepadEvents.Queue(pad, new PadInput { Buttons = b }); } catch { }
                yield return Seconds(0.2f);
                try { GamepadEvents.Queue(pad, default); } catch { }
            }
            yield return Seconds(1f);

        }

        static string Diagnose()
        {
            try
            {
                var gsm = GameStateManager.GetInstance();
                var st = gsm?.activeState;
                return $"state {(st != null ? st.name : "none")}, disableInput {st?.disablePlayerInput}, stasis {st?.putPlayerInStasis}, levelLoading {LevelLoading()}, room {Room}";
            }
            catch (Exception e) { return e.Message; }
        }

        // ---- scenarios ----

        static IEnumerator Main()
        {
            // Into the game from the main menu (the most recent save).
            yield return Until(() => Me != null || Object.FindObjectOfType<MainMenuAnimationController>() != null, 180f, "the main menu");
            if (Me == null)
            {
                yield return Seconds(4f);
                try { Object.FindObjectOfType<MainMenuAnimationController>()?.ContinueGameFromMain(); Log("Continue pressed."); }
                catch (Exception e) { Log($"FAIL could not continue from the main menu: {e.Message}"); yield break; }
            }
            yield return Until(InGame, 180f, "the game to load");
            yield return Until(() => Connected(), 60f, "the other copy to connect");
            Check("connected", Connected());
            if (!Connected()) yield break;
            yield return Seconds(2f);

            string[] list = Scenario == "all" ? new[] { "partner-down-win", "owner-down-win", "both-up-win" } : new[] { Scenario };
            for (int i = 0; i < list.Length; i++)
            {
                var sc = RunScenario(list[i], i);
                while (sc.MoveNext()) yield return sc.Current;
            }
        }

        static IEnumerator RunScenario(string name, int n)
        {
            Log($"=== {name} ===");
            yield return Barrier($"start{n}");
            if (n > 0)
            {
                // Back to the same save: Leon alive again.
                Victory.ForgetForTest();
                // Reload the level from the save (what a respawn does): Leon is alive again.
                try
                {
                    var afm = Object.FindObjectOfType<MotherBase.ApplicationFlowManager.ApplicationFlowManager>();
                    if (afm == null) Log("FAIL no flow manager to reload the level");
                    else { afm.ReloadCurrentState(); Log("Reloading the level."); }
                }
                catch (Exception e) { Log($"FAIL reload: {e.Message}"); }
                yield return Seconds(3f);
                yield return Until(InGame, 120f, "the save to load");
                yield return Barrier($"reloaded{n}");
                yield return Seconds(2f);
            }

            // The host walks into the arena first (it runs the room) and calls the guest (auto "yes").
            if (IsHost)
            {
                GameAccess.Teleport(Me, LeonArena);
                yield return Until(() => Room == LeonRoom, 20f, "the arena");
            }
            yield return Until(() => Room == LeonRoom && InGame(), 60f, "both in the arena");
            Check($"{name}: in the arena", Room == LeonRoom);
            // Walk toward Leon (the intro waits for the player), then skip the intro's lines until he fights.
            yield return Until(() => BossAlive(), 15f, "Leon to appear");
            // Our "fight together?" question stops time until the other player is in: walk only after it.
            yield return Until(() => !BossIntro.Frozen, 30f, "the fight-together question to end");
            yield return Seconds(1f);
            for (float t = 0f; t < 5f && Boss() != null && Me != null && Mathf.Abs(Boss().transform.position.x - Me.transform.position.x) > 2.5f; t += 0.25f)
            {
                Stick((short)(Boss().transform.position.x > Me.transform.position.x ? 32767 : -32767));
                yield return Seconds(0.25f);
            }
            Stick(0);
            // Teleporting in skips the arena entrance, where walking through its trigger starts the fight.
            if (IsHost)
            {
                int n2 = 0;
                foreach (var trig in BossSync.SceneObjects<BossFightTrigger>())
                {
                    try { trig.OnPlayerTriggerEncounter(Me); n2++; } catch (Exception e) { Log($"(boss trigger: {e.Message})"); }
                }
                // Leon's intro starts from a one-time trigger zone in his room.
                var fired = new System.Collections.Generic.List<string>();
                foreach (var act in BossSync.SceneObjects<ActivableEntity>())
                {
                    try
                    {
                        string path = PathOf(act.transform);
                        if (path.IndexOf("Boss_Leon", StringComparison.OrdinalIgnoreCase) < 0 || path.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (act.IsActivated()) { fired.Add("(already) " + path); continue; }
                        act.Activate(false);
                        fired.Add(path);
                    }
                    catch (Exception e) { Log($"(trigger zone: {e.Message})"); }
                }
                Log($"Set off {n2} boss trigger(s); trigger zones: {(fired.Count == 0 ? "none" : string.Join(" | ", fired))}.");
                // What is the intro pose waiting for? Its parts, and the cutscene launchers around.
                try
                {
                    var list = Boss()?.GetComponent<MotherBase.ActionFramework.Actor>()?.activeActions;
                    if (list != null)
                        for (int i = 0; i < list.Count; i++)
                        {
                            var d = list[i].data;
                            if (d == null || !d.name.Contains("Intro")) continue;
                            var parts = new System.Collections.Generic.List<string>();
                            if (d.actionFeatures != null) foreach (var f in d.actionFeatures) if (f != null) parts.Add(f.GetIl2CppType().Name);
                            Log($"Intro move {d.name} (end frame {d.endFrame}) parts: {string.Join(", ", parts)}");
                        }
                    // Cutscene triggers in Leon's room: the zone you walk through on the way in starts his intro.
                    var cut = new System.Collections.Generic.List<string>();
                    BossSync.EnterBossIntro(Room, Me);   // the host "walks in" (it teleported past the entrance)
                    cut.Add("via BossSync.EnterBossIntro");
                    Log($"Cutscene triggers fired: {(cut.Count == 0 ? "none" : string.Join(" | ", cut))}");
                    var zones = new System.Collections.Generic.List<string>();
                    foreach (var z in BossSync.SceneObjects<Trigger>())
                    {
                        string path = PathOf(z.transform);
                        if (path.IndexOf("Boss_Leon", StringComparison.OrdinalIgnoreCase) >= 0) zones.Add(path);
                    }
                    Log($"Trigger zones in Leon's room: {(zones.Count == 0 ? "none" : string.Join(" | ", zones))}");
                    var launchers = new System.Collections.Generic.List<string>();
                    foreach (var l in BossSync.SceneObjects<CutsceneLauncher>()) launchers.Add($"{PathOf(l.transform)} (active {l.isActiveAndEnabled})");
                    Log($"Cutscene launchers: {(launchers.Count == 0 ? "none" : string.Join(" | ", launchers))}");
                }
                catch (Exception e) { Log($"(intro dump: {e.Message})"); }
            }
            Log($"Walked to Leon: {(Boss() != null && Me != null ? Mathf.Abs(Boss().transform.position.x - Me.transform.position.x).ToString("0.0") : "?")} away.");
            float until = Time.unscaledTime + 90f;
            float seenFight = -1f;
            while (Time.unscaledTime < until)
            {
                SkipDialogue();
                bool fighting = BossAlive() && !BossInIntro();
                if (!fighting) seenFight = -1f;
                else if (seenFight < 0f) seenFight = Time.unscaledTime;
                else if (Time.unscaledTime - seenFight > 2f) break;
                yield return Seconds(0.4f);
            }
            Check($"{name}: boss fight running", BossAlive() && !BossInIntro(), $"boss alive {BossAlive()}, intro {BossInIntro()}");
            yield return Barrier($"fight{n}");

            bool owner = OwnRoom();
            bool iDie = name == "partner-down-win" ? !owner : name == "owner-down-win" ? owner : false;
            bool iKill = name == "both-up-win" ? owner : !iDie;
            Log($"I run the room: {owner}; I die: {iDie}; I kill: {iKill}.");

            if (iDie)
            {
                Die();
                yield return Seconds(1f);
                Check($"{name}: died", !Alive());
                yield return Until(() => HasFlag(Other, $"killed{n}"), 60f, "the partner to win");
                // The partner won: we respawn at the mirror as normal...
                yield return Until(() => InGame() && Alive() && Room != LeonRoom && GameplayState(), 60f, "respawn after the win");
                Check($"{name}: respawned after partner's win", InGame() && Alive() && Room != LeonRoom, Diagnose());
                yield return Seconds(5f);   // the get-up animation after a respawn
                var probe = MoveProbe($"{name}: can move after respawn");
                while (probe.MoveNext()) yield return probe.Current;
                // ...and the win waits for us in the boss room.
                GameAccess.Teleport(Me, LeonArena);
                yield return Until(() => Room == LeonRoom && InGame(), 30f, "back in the arena");
                yield return Until(() => Boss() != null && !BossAlive(), 20f, "our copy of the boss to fall");
                Check($"{name}: boss falls for us on return", Boss() != null && !BossAlive(), $"boss present {Boss() != null}, alive {BossAlive()}");
                yield return Seconds(4f);
                var absorb = Absorb($"{name}: reward absorbed");
                while (absorb.MoveNext()) yield return absorb.Current;
                if (StateName().Contains("Menu"))
                    Log("(the reward popup waits for a button press; a background window can't press it, so the move check is skipped)");
                else
                {
                    var probe2 = MoveProbe($"{name}: can move after the boss falls");
                    while (probe2.MoveNext()) yield return probe2.Current;
                }
            }
            else
            {
                if (name != "both-up-win") yield return Until(() => PartnerDead(), 30f, "the partner to die");
                yield return Seconds(3f);
                if (iKill) { KillBoss(); Log("Killed the boss."); }
                yield return Until(() => !BossAlive(), 20f, "the boss to fall");
                Check($"{name}: boss fell here", !BossAlive());
                Flag($"killed{n}");
                yield return Seconds(4f);
                var absorb = Absorb($"{name}: reward absorbed");
                while (absorb.MoveNext()) yield return absorb.Current;
                if (StateName().Contains("Menu"))
                    Log("(the reward popup waits for a button press; a background window can't press it, so the move check is skipped)");
                else
                {
                    var probe = MoveProbe($"{name}: can move after the win");
                    while (probe.MoveNext()) yield return probe.Current;
                }
            }
            yield return Barrier($"end{n}", 240f);
        }
    }
}
