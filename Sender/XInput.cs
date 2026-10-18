using System;
using System.Runtime.InteropServices;

namespace RoseNet.Sender
{
    // Minimal XInput binding. XInput keeps reading controllers while another window
    // (the stream viewer) has focus, which is exactly what the guest needs.
    internal static class XInput
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Gamepad
        {
            public ushort wButtons;
            public byte bLeftTrigger, bRightTrigger;
            public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct State
        {
            public uint dwPacketNumber;
            public Gamepad Gamepad;
        }

        const uint ErrorSuccess = 0;

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        static extern uint GetState14(uint index, out State state);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        static extern uint GetState910(uint index, out State state);

        static bool useLegacy;

        public static bool TryGetState(int index, out State state)
        {
            if (!useLegacy)
            {
                try { return GetState14((uint)index, out state) == ErrorSuccess; }
                catch (DllNotFoundException) { useLegacy = true; }
                catch (EntryPointNotFoundException) { useLegacy = true; }
            }
            return GetState910((uint)index, out state) == ErrorSuccess;
        }

        public static PadInput ToPadInput(in Gamepad g) => new PadInput
        {
            Buttons = g.wButtons,
            LeftTrigger = g.bLeftTrigger,
            RightTrigger = g.bRightTrigger,
            LX = g.sThumbLX,
            LY = g.sThumbLY,
            RX = g.sThumbRX,
            RY = g.sThumbRY,
        };
    }
}
