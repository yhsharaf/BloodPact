using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace RoseNet
{
    // Runs on Unity's main thread: turns each guest slot into a virtual Gamepad device that RoseCoop
    // sees like any locally plugged-in controller.
    public class RemotePadDriver : MonoBehaviour
    {
        public RemotePadDriver(IntPtr ptr) : base(ptr) { }

        InputDevice[] devices;
        uint[] appliedVersion;
        bool[] neutralSent;
        ushort[] loggedButtons;
        bool failed;

        // Read back what the Input System currently holds for the pad (state from the previous event).
        static string Describe(InputDevice device)
        {
            var pad = device.TryCast<Gamepad>();
            if (pad == null) return "not a Gamepad";
            return $"A={pad.buttonSouth.isPressed} B={pad.buttonEast.isPressed} LX={pad.leftStick.x.ReadValue():0.00}";
        }

        void Update()
        {
            var host = Plugin.Host;
            if (host == null || failed) return;
            if (devices == null)
            {
                devices = new InputDevice[host.SlotCount];
                appliedVersion = new uint[host.SlotCount];
                neutralSent = new bool[host.SlotCount];
                loggedButtons = new ushort[host.SlotCount];
            }

            long now = Stopwatch.GetTimestamp();
            long neutralAfter = (long)(Plugin.NeutralAfterSeconds.Value * Stopwatch.Frequency);

            try
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    var s = host.Snapshot(i);
                    if (!s.Used || s.Version == 0) continue;

                    // Created on the first packet and kept afterwards: removing it would drop the guest's Rose.
                    if (devices[i] == null || !devices[i].added)
                    {
                        devices[i] = InputSystem.AddDevice("Gamepad", $"RoseNet Guest {i + 1}", null);
                        appliedVersion[i] = 0;
                        Plugin.Logger.LogInfo($"Added virtual gamepad '{devices[i].name}' (device id {devices[i].deviceId}).");
                    }

                    bool stale = now - s.LastPacketTicks > neutralAfter;
                    if (stale)
                    {
                        if (!neutralSent[i])
                        {
                            GamepadEvents.Queue(devices[i], default);
                            neutralSent[i] = true;
                            Plugin.Logger.LogInfo($"Remote pad {i + 1} went quiet; released its buttons.");
                        }
                    }
                    else if (s.Version != appliedVersion[i])
                    {
                        if (Plugin.LogInput.Value && s.Input.Buttons != loggedButtons[i])
                        {
                            loggedButtons[i] = s.Input.Buttons;
                            Plugin.Logger.LogInfo($"Pad {i + 1} sent buttons {s.Input.Buttons:X4} LX {s.Input.LX}; device reads {Describe(devices[i])}");
                        }
                        GamepadEvents.Queue(devices[i], s.Input);
                        appliedVersion[i] = s.Version;
                        neutralSent[i] = false;
                    }
                }
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Logger.LogError($"Remote pads stopped after an error: {e}");
            }
        }
    }

    // Builds a StateEvent ('STAT') carrying a GamepadState ('GPAD') by hand and queues it with the
    // non-generic InputSystem.QueueEvent, so no generic IL2CPP instantiation is needed.
    internal static unsafe class GamepadEvents
    {
        // InputEvent header is 20 bytes; StateEvent adds a 4-byte format; GamepadState is 28 bytes.
        const int HeaderSize = 20;
        const int StateOffset = HeaderSize + 4;
        const int GamepadStateSize = 28;
        const int EventSize = StateOffset + GamepadStateSize;

        static readonly IntPtr buffer = Marshal.AllocHGlobal(64);
        static readonly int StateEventType = FourCC('S', 'T', 'A', 'T');
        static readonly int GamepadFormat = FourCC('G', 'P', 'A', 'D');

        // UnityEngine.InputSystem.LowLevel.GamepadButton bit indices.
        const int DpadUp = 0, DpadDown = 1, DpadLeft = 2, DpadRight = 3;
        const int North = 4, East = 5, South = 6, West = 7;
        const int LeftStick = 8, RightStick = 9, LeftShoulder = 10, RightShoulder = 11, Start = 12, Select = 13;

        static int FourCC(char a, char b, char c, char d) => (a << 24) | (b << 16) | (c << 8) | d;

        public static void Queue(InputDevice device, in PadInput p)
        {
            byte* e = (byte*)buffer;
            new Span<byte>(e, EventSize).Clear();
            *(int*)(e + 0) = StateEventType;
            *(ushort*)(e + 4) = EventSize;
            *(ushort*)(e + 6) = (ushort)device.deviceId;
            *(int*)(e + HeaderSize) = GamepadFormat;

            byte* s = e + StateOffset;
            *(uint*)(s + 0) = MapButtons(p.Buttons);
            *(float*)(s + 4) = Axis(p.LX);
            *(float*)(s + 8) = Axis(p.LY);
            *(float*)(s + 12) = Axis(p.RX);
            *(float*)(s + 16) = Axis(p.RY);
            *(float*)(s + 20) = p.LeftTrigger / 255f;
            *(float*)(s + 24) = p.RightTrigger / 255f;

            var ptr = new InputEventPtr((InputEvent*)e);
            ptr.time = InputState.currentTime;
            InputSystem.QueueEvent(ptr);
        }

        static float Axis(short v) => Math.Max(-1f, v / 32767f);

        static uint MapButtons(ushort x)
        {
            uint b = 0;
            void Map(ushort xinput, int bit) { if ((x & xinput) != 0) b |= 1u << bit; }
            Map(XInputButtons.DpadUp, DpadUp);
            Map(XInputButtons.DpadDown, DpadDown);
            Map(XInputButtons.DpadLeft, DpadLeft);
            Map(XInputButtons.DpadRight, DpadRight);
            Map(XInputButtons.Y, North);
            Map(XInputButtons.B, East);
            Map(XInputButtons.A, South);
            Map(XInputButtons.X, West);
            Map(XInputButtons.LeftThumb, LeftStick);
            Map(XInputButtons.RightThumb, RightStick);
            Map(XInputButtons.LeftShoulder, LeftShoulder);
            Map(XInputButtons.RightShoulder, RightShoulder);
            Map(XInputButtons.Start, Start);
            Map(XInputButtons.Back, Select);
            return b;
        }
    }
}
