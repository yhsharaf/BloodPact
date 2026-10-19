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

        // The boss room under test (--rosenet-test-boss <part of the room id>, Leon by default) and a spot in it.
        static string bossRoom;
        static Vector3 arenaSpot;
        static string LeonRoom { get { ResolveBossRoom(); return bossRoom; } }
        static Vector3 LeonArena { get { ResolveBossRoom(); return arenaSpot; } }

        static void ResolveBossRoom()
        {
            if (bossRoom != null) return;
            string want = CommandLine.Value("rosenet-test-boss") ?? "Boss_Leon";
            foreach (var r in Rooms())
            {
                if (r.Id.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bossRoom = r.Id;
                arenaSpot = r.Origin + new Vector3(r.Size.x * 0.5f, r.Size.y * 0.5f, 0f);
                Log($"Boss room under test: {bossRoom} (spot {arenaSpot.x:0.0},{arenaSpot.y:0.0}).");
                return;
            }
            bossRoom = "(none)";
            Log($"FAIL no boss room matching '{want}' in this level.");
        }

        static string Dir => Path.Combine(BepInEx.Paths.BepInExRootPath, "BloodPact-test");
        static string Role => IsHost ? "host" : "guest";
        static string Other => IsHost ? "guest" : "host";

        static IEnumerator run;
        static Func<bool> waitFor;
        static float waitUntil, waitStarted;
        static string waitWhat;
        static bool started;
        static int passed, failed, fightMaxHealth = -1;

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
            if (Protect) KeepAlive();
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

        static string BossMoves()
        {
            var list = Boss()?.GetComponent<MotherBase.ActionFramework.Actor>()?.activeActions;
            if (list == null) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count; i++) sb.Append(list[i].data != null ? list[i].data.name : "?").Append(' ');
            return sb.ToString().Trim();
        }

        static void SkipDialogue()
        {
            try { foreach (var r in Object.FindObjectsOfType<CutsceneLineReader>()) r.HandleUserSkipped(new InputAction.CallbackContext()); } catch { }
        }

        // The test players stand still in the arena: keep their health up, except for the deliberate death.
        static bool Protect = true;
        static void KeepAlive()
        {
            try
            {
                var d = Me != null ? MobControl.DamageableOf(Me) : null;
                if (d != null && d.isAlive && d.currentHealth > 0 && d.currentHealth < d.maxHealth) d.currentHealth = d.maxHealth;
            }
            catch { }
        }

        static void Die()
        {
            Protect = false;
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
            try
            {
                var who = new System.Collections.Generic.List<string>();
                foreach (var r in Object.FindObjectsOfType<CutsceneLineReader>()) if (r != null && r.isActiveAndEnabled) who.Add(PathOf(r.transform));
                var views = new System.Collections.Generic.List<string>();
                foreach (var v in Object.FindObjectsOfType<Doozy.Runtime.UIManager.Containers.UIView>()) if (v != null && v.isVisible && v.name.IndexOf("HUD", StringComparison.OrdinalIgnoreCase) < 0) views.Add(v.name);
                Log($"Dialogue up in {Room}: line readers {(who.Count == 0 ? "none" : string.Join(" | ", who))}; views {string.Join(", ", views)}");
            }
            catch (Exception e) { Log($"(dialogue dump: {e.Message})"); }
            float until = Time.unscaledTime + 60f;
            try { if (pad == null || !pad.added) pad = InputSystem.AddDevice("Gamepad", "RoseNet Test Pad", null); } catch { }
            float started = Time.unscaledTime;
            while (Time.unscaledTime < until && StateName().Contains("Dialogue"))
            {
                try { GamepadEvents.Queue(pad, new PadInput { Buttons = XInputButtons.A }); } catch { }
                SkipDialogue();
                // Full-screen conversations only listen to the focused window; forcing their lines to end breaks
                // the scene, so those are left waiting (the checks after them are skipped).
                if (FullScreenTalk() && Time.unscaledTime - started > 8f) break;
                yield return Seconds(0.7f);
            }
            try { GamepadEvents.Queue(pad, default); } catch { }
            yield return Seconds(0.5f);
            Log($"Dialogue over: state {StateName()}.");
        }

        static bool FullScreenTalk()
        {
            try
            {
                foreach (var v in Object.FindObjectsOfType<Doozy.Runtime.UIManager.Containers.UIView>())
                    if (v != null && v.isVisible && v.name.Contains("DialogueBoxFullScreen")) return true;
            }
            catch { }
            return false;
        }

        static IEnumerator MoveProbe(string name)
        {
            var talk = HoldThroughDialogue();
            while (talk.MoveNext()) yield return talk.Current;
            if (StateName().Contains("Menu") || (StateName().Contains("Dialogue") && FullScreenTalk()))
            {
                Log($"({name}: skipped, the game's {(StateName().Contains("Menu") ? "reward popup" : "full-screen conversation")} waits for a press a background window can't give)");
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
                try
                {
                    var menu = Object.FindObjectOfType<MainMenuAnimationController>();
                    // --rosenet-test-slot N: play that save (only an existing one: never create a save).
                    if (int.TryParse(CommandLine.Value("rosenet-test-slot"), out int slot))
                    {
                        bool exists = false;
                        try { exists = menu.saveMenuManager != null && menu.saveMenuManager.SaveExists(slot); } catch { }
                        if (!exists) { Log($"FAIL save slot {slot} doesn't exist; not creating one."); yield break; }
                        menu._selectedSaveIndex = slot;
                        Log($"Using save slot {slot}.");
                    }
                    menu?.ContinueGameFromMain();
                    Log("Continue pressed.");
                }
                catch (Exception e) { Log($"FAIL could not continue from the main menu: {e.Message}"); yield break; }
            }
            yield return Until(InGame, 180f, "the game to load");
            yield return Until(() => Connected(), 60f, "the other copy to connect");
            Check("connected", Connected());
            if (!Connected()) yield break;
            yield return Seconds(2f);
            if (CommandLine.Value("rosenet-test-level") != null)
            {
                var sw = SwitchLevel();
                while (sw.MoveNext()) yield return sw.Current;
                yield return Barrier("level", 240f);
                yield return Until(() => Connected(), 60f, "the other copy after the area switch");
                yield return Seconds(2f);
            }

            if (Scenario == "monsters")
            {
                var mo = Monsters();
                while (mo.MoveNext()) yield return mo.Current;
                yield break;
            }
            if (Scenario == "map-bosses")
            {
                var m = MapBosses();
                while (m.MoveNext()) yield return m.Current;
                yield break;
            }
            if (Scenario == "chest")
            {
                var c = Chest();
                while (c.MoveNext()) yield return c.Current;
                yield break;
            }
            if (Scenario == "features")
            {
                var f = Features();
                while (f.MoveNext()) yield return f.Current;
                yield break;
            }
            string[] list = Scenario == "all" ? new[] { "partner-down-win", "owner-down-win", "both-up-win" } : new[] { Scenario };
            for (int i = 0; i < list.Length; i++)
            {
                var sc = RunScenario(list[i], i);
                while (sc.MoveNext()) yield return sc.Current;
            }
        }

        static IEnumerator Features()
        {
            Log("=== features ===");
            yield return Barrier("features");
            // Same build on both sides (the build check message).
            yield return Until(() => Session.DebugReport.PartnerBuild != null, 15f, "the partner's build id");
            Check("features: partner runs the same build", Session.DebugReport.PartnerBuild == Session.DebugReport.BuildId, $"ours {Session.DebugReport.BuildId}, partner {Session.DebugReport.PartnerBuild}");

            // Pause menu online: time must keep running.
            if (IsHost)
            {
                try { if (pad == null || !pad.added) pad = InputSystem.AddDevice("Gamepad", "RoseNet Test Pad", null); GamepadEvents.Queue(pad, new PadInput { Buttons = XInputButtons.Start }); } catch { }
                yield return Seconds(0.3f);
                try { GamepadEvents.Queue(pad, default); } catch { }
                yield return Seconds(2f);
                string st = StateName();
                float t0 = Time.time; yield return Seconds(1f); float advanced = Time.time - t0;
                Check("features: pause menu keeps time running", Time.timeScale > 0.5f && advanced > 0.5f, $"state {st}, timeScale {Time.timeScale:0.00}, game time advanced {advanced:0.00}s in 1s");
                try { GamepadEvents.Queue(pad, new PadInput { Buttons = XInputButtons.Start }); } catch { }
                yield return Seconds(0.3f);
                try { GamepadEvents.Queue(pad, default); } catch { }
                yield return Seconds(1f);
            }
            yield return Barrier("paused");

            // F10 bug report from the guest: both logs land on the guest's PC.
            if (!IsHost)
            {
                Session.DebugReport.StartReport(true, "host");
                string dir = Session.DebugReport.ReportDir;
                yield return Until(() => dir != null && Directory.Exists(dir) && Directory.GetFiles(dir, "partner_*.log").Length > 0, 45f, "the partner's log");
                bool mine = dir != null && Directory.GetFiles(dir, "mine_*.log").Length > 0;
                string[] partner = dir != null ? Directory.GetFiles(dir, "partner_*.log") : new string[0];
                long size = partner.Length > 0 ? new FileInfo(partner[0]).Length : 0;
                Check("features: F10 report has both logs", mine && size > 1000, $"{dir}: ours {mine}, partner's {size} bytes");
            }
            yield return Barrier("reported", 90f);
        }

        // ---- the level's rooms ----

        internal struct RoomInfo { public string Id; public Vector3 Origin; public Vector2 Size; }

        static System.Collections.Generic.List<RoomInfo> Rooms()
        {
            var list = new System.Collections.Generic.List<RoomInfo>();
            var world = GameAccess.RoomSystem?.worldInstance;
            MotherBase.RoomDesigner.WorldSettings settings = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MotherBase.RoomDesigner.WorldSettings>()))
            { settings = o.TryCast<MotherBase.RoomDesigner.WorldSettings>(); if (settings != null) break; }
            if (world == null || settings == null) { Log($"(no room list: world {world != null}, settings {settings != null})"); return list; }
            foreach (var kv in world._roomHandles)
            {
                var r = kv.Key;
                if (r == null || r.roomData == null) continue;
                list.Add(new RoomInfo
                {
                    Id = r.id,
                    Origin = r.GetWorldPosition(settings),
                    Size = new Vector2(r.roomData.GetWidth() * settings.chunkWidth, r.roomData.GetHeight() * settings.chunkHeight),
                });
            }
            return list;
        }

        // Teleport somewhere and stay pinned there until the room under us has loaded.
        static IEnumerator GoTo(Vector3 spot, string room, float timeout = 20f)
        {
            float until = Time.unscaledTime + timeout;
            GameAccess.Teleport(Me, spot);
            while (Time.unscaledTime < until && !(Room == room && InGame()))
            {
                if (Me != null) GameAccess.Pin(Me, spot);
                yield return Seconds(0.1f);
            }
            for (int i = 0; i < 10 && Me != null; i++) { GameAccess.Pin(Me, spot); yield return Seconds(0.1f); }
        }

        // --rosenet-test-level <state name>: switch to that level (area) first. Saves are never written in tests,
        // so this only lasts for the test.
        static IEnumerator SwitchLevel()
        {
            string want = CommandLine.Value("rosenet-test-level");
            var names = new System.Collections.Generic.List<string>();
            MotherBase.ApplicationFlowManager.ApplicationState target = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MotherBase.ApplicationFlowManager.ApplicationState>()))
            {
                var st = o.TryCast<MotherBase.ApplicationFlowManager.ApplicationState>();
                if (st == null) continue;
                names.Add(st.name);
                if (want != null && st.name == want) target = st;
            }
            Log($"Areas the game knows: {string.Join(", ", names)}");
            if (want == null) yield break;
            if (target == null) { Log($"FAIL no area named '{want}'"); yield break; }
            var afm = Object.FindObjectOfType<MotherBase.ApplicationFlowManager.ApplicationFlowManager>();
            Log($"Switching to area '{want}'.");
            afm.LoadState(target);
            yield return Seconds(5f);
            yield return Until(InGame, 180f, "the new area to load");
            Log($"Now in {Room}.");
            yield return Seconds(3f);
        }

        // Which boss rooms does this save have, which bosses live there, and where do they stand?
        static IEnumerator MapBosses()
        {
            Log("=== map-bosses ===");
            if (!IsHost) yield break;
            var rooms = Rooms();
            Log($"{rooms.Count} rooms in this level; current room {Room} at {Me.transform.position.x:0.0},{Me.transform.position.y:0.0}.");
            foreach (var r in rooms) if (r.Id == Room) Log($"  our room origin {r.Origin.x:0.0},{r.Origin.y:0.0} size {r.Size.x:0}x{r.Size.y:0}");
            var lines = new System.Collections.Generic.List<string>();
            foreach (var r in rooms)
            {
                if (r.Id.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // Try the middle of the room, then its lower half (floors are low in arenas).
                var spot = r.Origin + new Vector3(r.Size.x * 0.5f, r.Size.y * 0.5f, 0f);
                var go = GoTo(spot, r.Id);
                while (go.MoveNext()) yield return go.Current;
                bool arrived = Room == r.Id;
                yield return Seconds(2f);
                var b = Boss();
                string bossInfo = b == null ? "no boss" : $"boss '{MobRegistry.PrefabName(b)}' {(BossAlive() ? "alive" : "dead")} at {b.transform.position.x:0.0},{b.transform.position.y:0.0}";
                string line = $"{r.Id}|{(arrived ? "reached" : "NOT reached (now " + Room + ")")}|origin {r.Origin.x:0.0},{r.Origin.y:0.0} size {r.Size.x:0}x{r.Size.y:0}|{bossInfo}";
                Log("  " + line);
                lines.Add(line);
            }
            try { File.WriteAllLines(Path.Combine(Dir, "bosses.txt"), lines); } catch { }
            Log($"Mapped {lines.Count} boss rooms.");
        }

        // ---- monsters and world objects in a shared room ----

        static System.Collections.Generic.List<(uint id, Entity mob)> LiveMonsters()
        {
            var list = new System.Collections.Generic.List<(uint, Entity)>();
            foreach (var kv in MobRegistry.ById)
            {
                try
                {
                    if (kv.Value == null || !MobRegistry.InRoom(kv.Key, Room) || !MobRegistry.IsAlive(kv.Value)) continue;
                    if (kv.Value.GetComponent<BossComponent>() != null) continue;
                    list.Add((kv.Key, kv.Value));
                }
                catch { }
            }
            return list;
        }

        static float F(string t) => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
        static string S(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        static IEnumerator Monsters()
        {
            Log("=== monsters ===");
            yield return Barrier("monsters-start");
            string roomFile = Path.Combine(Dir, "monster-room.txt");
            if (IsHost)
            {
                // A regular room with at least 3 live monsters.
                string found = null; Vector3 at = Vector3.zero;
                foreach (var r in Rooms())
                {
                    if (r.Id.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0 || r.Id.IndexOf("Save", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var spot = r.Origin + new Vector3(r.Size.x * 0.5f, r.Size.y * 0.5f, 0f);
                    var go = GoTo(spot, r.Id, 12f);
                    while (go.MoveNext()) yield return go.Current;
                    yield return Seconds(2.5f);
                    if (Room == r.Id && LiveMonsters().Count >= 3) { found = r.Id; at = spot; break; }
                }
                if (found == null)
                {
                    Log("(no room with 3 live monsters; skipping)");
                    File.WriteAllText(roomFile, "none");
                    Flag("monster-room");
                    yield return Barrier("monsters-end", 120f);
                    yield break;
                }
                File.WriteAllText(roomFile, found + "|" + S(at.x) + "|" + S(at.y));
                Log("Monster room: " + found + " with " + LiveMonsters().Count + " monsters.");
                Flag("monster-room");
            }
            else
            {
                yield return Until(() => HasFlag(Other, "monster-room"), 600f, "the host to find a monster room");
                string[] f = File.ReadAllText(roomFile).Split('|');
                if (f.Length < 3) { yield return Barrier("monsters-end", 120f); yield break; }
                var go = GoTo(new Vector3(F(f[1]), F(f[2]), 0f), f[0], 20f);
                while (go.MoveNext()) yield return go.Current;
            }
            yield return Barrier("monsters-together", 120f);
            yield return Seconds(4f);

            // 1. Same monsters on both sides, in about the same places.
            string listFile = Path.Combine(Dir, "monsters-host.txt");
            if (IsHost)
            {
                var lines = new System.Collections.Generic.List<string>();
                foreach (var (id, mob) in LiveMonsters()) lines.Add(id + "|" + S(mob.transform.position.x) + "|" + S(mob.transform.position.y));
                File.WriteAllLines(listFile, lines);
                Check("monsters: we run the room (entered first)", OwnRoom());
                Flag("monsters-listed");
            }
            else
            {
                yield return Until(() => HasFlag(Other, "monsters-listed"), 30f, "the host's monster list");
                int same = 0, near = 0, total = 0;
                foreach (var line in File.ReadAllLines(listFile))
                {
                    var p2 = line.Split('|'); total++;
                    if (!uint.TryParse(p2[0], out var id) || !MobRegistry.ById.TryGetValue(id, out var mob) || mob == null || !MobRegistry.IsAlive(mob)) continue;
                    same++;
                    if (Vector2.Distance(new Vector2(F(p2[1]), F(p2[2])), mob.transform.position) < 3f) near++;
                }
                Check("monsters: guest sees the host's monsters", total > 0 && same == total, same + " of " + total);
                Check("monsters: in the same places", total > 0 && near >= total - 1, near + " of " + total + " within 3 units");
            }
            yield return Barrier("monsters-compared", 60f);

            // 2. A monster the host kills dies for the guest too.
            string killFile = Path.Combine(Dir, "monster-killed.txt");
            if (IsHost)
            {
                var live = LiveMonsters();
                if (live.Count > 0)
                {
                    var (id, mob) = live[0];
                    var d = MobControl.DamageableOf(mob);
                    try { d.currentHealth = 0; d.Die(mob, Me, new DamageData()); } catch (Exception e) { Log("(kill: " + e.Message + ")"); }
                    File.WriteAllText(killFile, id.ToString());
                    Log("Killed monster " + id + " (" + MobRegistry.PrefabName(mob) + ").");
                }
                Flag("monster-killed");
            }
            else
            {
                yield return Until(() => HasFlag(Other, "monster-killed"), 30f, "the host's kill");
                if (File.Exists(killFile) && uint.TryParse(File.ReadAllText(killFile), out var id))
                {
                    yield return Until(() => !MobRegistry.ById.TryGetValue(id, out var m) || m == null || !MobRegistry.IsAlive(m), 6f, "the monster to die here");
                    bool gone = !MobRegistry.ById.TryGetValue(id, out var mm) || mm == null || !MobRegistry.IsAlive(mm);
                    Check("monsters: host's kill shows for the guest", gone, "monster " + id);
                }
            }
            yield return Barrier("monsters-killed", 60f);

            // 3. The guest's hit reaches the host's monster.
            string hitFile = Path.Combine(Dir, "monster-hit.txt");
            if (!IsHost)
            {
                var live = LiveMonsters();
                if (live.Count > 0)
                {
                    var (id, mob) = live[0];
                    MobControl.OutgoingHits.Add((id, 5));
                    File.WriteAllText(hitFile, id.ToString());
                    Log("Hit monster " + id + " for 5.");
                }
                Flag("monster-hit");
            }
            else
            {
                var before = new System.Collections.Generic.Dictionary<uint, int>();
                foreach (var (id, mob) in LiveMonsters()) before[id] = MobControl.DamageableOf(mob)?.currentHealth ?? -1;
                yield return Until(() => HasFlag(Other, "monster-hit"), 30f, "the guest's hit");
                if (File.Exists(hitFile) && uint.TryParse(File.ReadAllText(hitFile), out var hid) && before.TryGetValue(hid, out var hp0))
                {
                    yield return Until(() => MobRegistry.ById.TryGetValue(hid, out var m) && m != null && (MobControl.DamageableOf(m)?.currentHealth ?? hp0) < hp0, 5f, "the hit to land");
                    int hp1 = MobRegistry.ById.TryGetValue(hid, out var mm) && mm != null ? (MobControl.DamageableOf(mm)?.currentHealth ?? -1) : -1;
                    Check("monsters: guest's hit lands on the host's monster", hp1 < hp0, "health " + hp0 + " -> " + hp1);
                }
            }
            yield return Barrier("monsters-hit", 60f);

            // 4. A switch the host flips flips for the guest (same room).
            string switchFile = Path.Combine(Dir, "switch.txt");
            if (IsHost)
            {
                string done = "none";
                foreach (var act in BossSync.SceneObjects<ActivableEntity>())
                {
                    try
                    {
                        if (act.IsActivated() || act.GetComponentInChildren<ChestItemHolder>(true) != null) continue;
                        var sav = act.GetComponentInChildren<SavableEntityBase>(true) ?? act.GetComponentInParent<SavableEntityBase>();
                        if (sav == null) continue;
                        string path = PathOf(act.transform);
                        if (!Room.EndsWith(path.Split('/')[0], StringComparison.Ordinal)) continue;
                        if (path.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("DestructibleProp", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        act.Activate(false);
                        done = path;
                        break;
                    }
                    catch { }
                }
                File.WriteAllText(switchFile, done);
                Log("Flipped switch: " + done + ".");
                Flag("switch");
            }
            else
            {
                yield return Until(() => HasFlag(Other, "switch"), 30f, "the host's switch");
                string path = File.Exists(switchFile) ? File.ReadAllText(switchFile) : "none";
                if (path == "none") Log("(no switch in this room to test)");
                else
                {
                    ActivableEntity mine = null;
                    foreach (var act in BossSync.SceneObjects<ActivableEntity>()) { try { if (PathOf(act.transform) == path) { mine = act; break; } } catch { } }
                    yield return Until(() => mine != null && mine.IsActivated(), 6f, "the switch to flip here");
                    Check("world: host's switch flips for the guest", mine != null && mine.IsActivated(), path);
                }
            }
            yield return Barrier("monsters-end", 60f);
        }

        static int InventoryCount()
        {
            try { var inv = Me?.GetComponent<InventoryComponent>()?.inventory; return inv?._itemsInInventory?.Count ?? -1; } catch { return -1; }
        }

        // A chest one player opens gives the other its items, wherever they are.
        static IEnumerator Chest()
        {
            Log("=== chest ===");
            yield return Barrier("chest-start");
            int before = InventoryCount();
            if (IsHost)
            {
                // An unopened chest in any loaded room.
                ActivableEntity chest = null; string where = null;
                foreach (var h in BossSync.SceneObjects<ChestItemHolder>())
                {
                    try
                    {
                        var act = h.GetComponentInParent<ActivableEntity>();
                        if (act == null || act.IsActivated() || h.itemGiver == null || h.itemGiver.HasNoItemToGive()) continue;
                        chest = act; where = PathOf(h.transform); break;
                    }
                    catch { }
                }
                if (chest == null)
                {
                    // No chest near the save: send what a chest opening sends, with an item we (and so the
                    // guest, same save) don't own yet.
                    string id = null;
                    try
                    {
                        var have = new System.Collections.Generic.HashSet<string>();
                        var inv = Me?.GetComponent<InventoryComponent>()?.inventory;
                        if (inv?._itemsInInventory != null) foreach (var it in inv._itemsInInventory) if (it != null) have.Add(it.itemID);
                        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<ItemData>()))
                        {
                            var d = o.TryCast<ItemData>();
                            if (d == null || d.abstractItem || d.isDeprecated || string.IsNullOrEmpty(d.itemID) || have.Contains(d.itemID)) continue;
                            if (d.TryCast<CharmItemData>() == null && d.TryCast<SpellItemData>() == null && d.TryCast<WeaponItemData>() == null) continue;
                            id = d.itemID; break;
                        }
                    }
                    catch (Exception e) { Log($"(item pick: {e.Message})"); }
                    if (id == null) { Log("(no chest and no spare item to send; skipping)"); Flag("chest-none"); }
                    else
                    {
                        Log($"No chest loaded near the save; sending a chest opening with item '{id}'.");
                        WorldSync.OutgoingLoot.Add(("selftest-chest", new[] { id }));
                        Flag("chest-opened");
                    }
                }
                else
                {
                    GameAccess.Teleport(Me, chest.transform.position + new Vector3(-1f, 0.3f, 0f));
                    yield return Seconds(3f);
                    Log($"Opening chest {where}.");
                    try { chest.Activate(false); } catch (Exception e) { Log($"(open: {e.Message})"); }
                    yield return Seconds(1f);
                    Flag("chest-opened");
                }
            }
            else
            {
                yield return Until(() => HasFlag(Other, "chest-opened") || HasFlag(Other, "chest-none"), 60f, "the host to open a chest");
                if (HasFlag(Other, "chest-opened"))
                {
                    yield return Until(() => InventoryCount() > before, 15f, "the chest's items");
                    Check("chest: partner's chest items reach us in another room", InventoryCount() > before, $"items {before} -> {InventoryCount()}, our room {Room}");
                }
            }
            yield return Barrier("chest-end", 60f);
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
                var go = GoTo(LeonArena, LeonRoom);
                while (go.MoveNext()) yield return go.Current;
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
                // The boss's intro talk comes first (Carmilla, Agnes...): hold through it, like a player would.
                if (StateName().Contains("Dialogue")) { var talk0 = HoldThroughDialogue(); while (talk0.MoveNext()) yield return talk0.Current; }
                SkipDialogue();
                // Running = the game's own fight has started here (not just a boss moving about before its intro).
                bool fighting = BossAlive() && !BossInIntro() && BossSync.LocalFightRoom == Room;
                if (!fighting) seenFight = -1f;
                else if (seenFight < 0f) seenFight = Time.unscaledTime;
                else if (Time.unscaledTime - seenFight > 2f) break;
                yield return Seconds(0.4f);
            }
            Check($"{name}: boss fight running", BossAlive() && !BossInIntro() && BossSync.LocalFightRoom == Room, $"boss alive {BossAlive()}, intro {BossInIntro()}, game's fight started {BossSync.LocalFightRoom == Room}");
            yield return Barrier($"fight{n}");

            { var bb = Boss(); var dd = bb != null ? MobControl.DamageableOf(bb) : null; fightMaxHealth = dd != null ? dd.maxHealth : -1; }
            bool owner = OwnRoom();
            bool iDie = name == "partner-down-win" ? !owner : name == "owner-down-win" ? owner : false;
            bool iKill = name == "both-up-win" ? owner : !iDie;
            Log($"I run the room: {owner}; I die: {iDie}; I kill: {iKill}.");

            if (iDie)
            {
                if (owner)
                {
                    var ob = Boss(); var od = ob != null ? MobControl.DamageableOf(ob) : null;
                    File.WriteAllText(Path.Combine(Dir, "owner-boss-max.txt"), (od != null ? od.maxHealth : -1).ToString());
                }
                Die();
                yield return Seconds(1f);
                Check($"{name}: died", !Alive());
                yield return Until(() => HasFlag(Other, $"killed{n}"), 60f, "the partner to win");
                // The partner won: we respawn at the mirror as normal...
                yield return Until(() => InGame() && Alive() && Room != LeonRoom && GameplayState(), 60f, "respawn after the win");
                Check($"{name}: respawned after partner's win", InGame() && Alive() && Room != LeonRoom, Diagnose());
                Protect = true;
                yield return Seconds(5f);   // the get-up animation after a respawn
                Check($"{name}: no dialogue stuck after the respawn", !StateName().Contains("Dialogue"), $"state {StateName()}");
                var probe = MoveProbe($"{name}: can move after respawn");
                while (probe.MoveNext()) yield return probe.Current;
                // ...and the win waits for us in the boss room. The mirror can be in another area (slot 2 saves
                // in the Streets): go back to the boss's area first, the way a player would walk back.
                bool arenaHere = false;
                foreach (var r in Rooms()) if (r.Id == LeonRoom) { arenaHere = true; break; }
                if (!arenaHere && CommandLine.Value("rosenet-test-level") != null)
                {
                    var sw = SwitchLevel();
                    while (sw.MoveNext()) yield return sw.Current;
                }
                var back = GoTo(LeonArena, LeonRoom);
                while (back.MoveNext()) yield return back.Current;
                yield return Until(() => Room == LeonRoom && InGame(), 30f, "back in the arena");
                yield return Seconds(1.5f);
                BossSync.EnterBossIntro(Room, Me);   // as if we had walked in through the entrance
                // Coming back the normal way: the boss's intro plays (hold through its lines), its fight starts,
                // then it falls for us and leaves its reward.
                for (float t = 0f; t < 60f && BossAlive(); t += 0.5f)
                {
                    if (StateName().Contains("Dialogue")) { var talk = HoldThroughDialogue(); while (talk.MoveNext()) yield return talk.Current; }
                    SkipDialogue();
                    yield return Seconds(0.5f);
                }
                Check($"{name}: boss falls for us on return", Boss() != null && !BossAlive(), $"boss present {Boss() != null}, alive {BossAlive()}");
                yield return Until(() => BossMoves().IndexOf("Death", StringComparison.OrdinalIgnoreCase) < 0, 40f, "the boss's death scene to end");
                yield return Until(() => StateName().Contains("Absorption") || StateName().Contains("Dialogue"), 20f, "the reward to be offered");
                if (StateName().Contains("Dialogue")) { var talk2 = HoldThroughDialogue(); while (talk2.MoveNext()) yield return talk2.Current; }
                yield return Until(() => StateName().Contains("Absorption"), 15f, "the reward to be offered");
                Check($"{name}: reward offered to us on return", StateName().Contains("Absorption"), $"state {StateName()}");
                var absorb = Absorb($"{name}: reward absorbed");
                while (absorb.MoveNext()) yield return absorb.Current;
                yield return Until(() => !StateName().Contains("Absorption"), 20f, "the reward pickup to finish");
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
                if (name != "both-up-win")
                {
                    // The boss's health just before the takeover (co-op scaling lands a moment into the fight).
                    for (float t = 0f; t < 30f && !PartnerDead(); t += 0.1f)
                    {
                        var bb = Boss(); var dd = bb != null ? MobControl.DamageableOf(bb) : null;
                        if (dd != null) fightMaxHealth = dd.maxHealth;
                        yield return Seconds(0.1f);
                    }
                }
                yield return Seconds(3f);
                if (name == "owner-down-win")
                {
                    // We took the boss over from the fallen player: it must fight on, and its co-op health must
                    // not have been raised a second time.
                    var b = Boss(); var d = b != null ? MobControl.DamageableOf(b) : null;
                    int max = d != null ? d.maxHealth : -1;
                    // Compare with the fallen owner's own copy (ours only mirrored it, at its solo health).
                    try { var f = Path.Combine(Dir, "owner-boss-max.txt"); if (File.Exists(f) && int.TryParse(File.ReadAllText(f), out var om) && om > 0) fightMaxHealth = om; } catch { }
                    Check($"{name}: we run the room after the partner fell", OwnRoom());
                    Check($"{name}: boss health raised once after takeover", max > 0 && max == fightMaxHealth, $"max health {max}, during the fight {fightMaxHealth}");
                    string before = BossMoves(); Vector3 at = b != null ? b.transform.position : Vector3.zero;
                    bool acted = false;
                    for (float t = 0f; t < 10f && !acted; t += 0.5f)
                    {
                        yield return Seconds(0.5f);
                        var bb = Boss();
                        if (bb != null && (BossMoves() != before || Vector3.Distance(bb.transform.position, at) > 0.5f)) acted = true;
                    }
                    Check($"{name}: boss keeps fighting after takeover", acted, $"moves before '{before}', now '{BossMoves()}'");
                }
                if (iKill) { KillBoss(); Log("Killed the boss."); }
                yield return Until(() => !BossAlive(), 20f, "the boss to fall");
                Check($"{name}: boss fell here", !BossAlive());
                Flag($"killed{n}");
                yield return Seconds(4f);
                // Some deaths are a scene of their own (Agnes holds Rose): let it play out first.
                yield return Until(() => BossMoves().IndexOf("Death", StringComparison.OrdinalIgnoreCase) < 0, 40f, "the boss's death scene to end");
                var absorb = Absorb($"{name}: reward absorbed");
                while (absorb.MoveNext()) yield return absorb.Current;
                yield return Until(() => !StateName().Contains("Absorption"), 20f, "the reward pickup to finish");
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
