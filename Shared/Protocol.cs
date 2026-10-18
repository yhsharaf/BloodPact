using System;
using System.Buffers.Binary;

namespace RoseNet
{
    // Wire format shared by the host plugin and the guest sender. All values little-endian.
    //
    // Input (guest -> host), 28 bytes:
    //   0 u32 magic "RNET"   4 u8 version   5 u8 flags   6 u16 reserved
    //   8 u32 clientId      12 u32 seq
    //  16 u16 XInput buttons 18 u8 left trigger 19 u8 right trigger
    //  20 i16 LX  22 i16 LY  24 i16 RX  26 i16 RY
    //
    // Ack (host -> guest), 12 bytes:
    //   0 u32 magic "RNAK"   4 u8 version   5 u8 slot (0-based, 255 = host full)
    //   6 u16 reserved       8 u32 clientId
    public static class Protocol
    {
        public const int DefaultPort = 27850;
        public const byte Version = 1;
        public const int InputSize = 28;
        public const int AckSize = 12;
        public const byte SlotFull = 255;

        const uint InputMagic = 0x54454E52; // "RNET"
        const uint AckMagic = 0x4B414E52;   // "RNAK"

        public static void WriteInput(Span<byte> b, uint clientId, uint seq, in PadInput p)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, InputMagic);
            b[4] = Version;
            b[5] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(6), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(8), clientId);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(12), seq);
            BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(16), p.Buttons);
            b[18] = p.LeftTrigger;
            b[19] = p.RightTrigger;
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(20), p.LX);
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(22), p.LY);
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(24), p.RX);
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(26), p.RY);
        }

        public static bool TryReadInput(ReadOnlySpan<byte> b, out uint clientId, out uint seq, out PadInput p)
        {
            clientId = 0; seq = 0; p = default;
            if (b.Length < InputSize) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(b) != InputMagic || b[4] != Version) return false;
            clientId = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(8));
            seq = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(12));
            p.Buttons = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(16));
            p.LeftTrigger = b[18];
            p.RightTrigger = b[19];
            p.LX = BinaryPrimitives.ReadInt16LittleEndian(b.Slice(20));
            p.LY = BinaryPrimitives.ReadInt16LittleEndian(b.Slice(22));
            p.RX = BinaryPrimitives.ReadInt16LittleEndian(b.Slice(24));
            p.RY = BinaryPrimitives.ReadInt16LittleEndian(b.Slice(26));
            return true;
        }

        public static void WriteAck(Span<byte> b, uint clientId, byte slot)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, AckMagic);
            b[4] = Version;
            b[5] = slot;
            BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(6), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(8), clientId);
        }

        public static bool TryReadAck(ReadOnlySpan<byte> b, out uint clientId, out byte slot)
        {
            clientId = 0; slot = SlotFull;
            if (b.Length < AckSize) return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(b) != AckMagic || b[4] != Version) return false;
            slot = b[5];
            clientId = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(8));
            return true;
        }
    }

    // Controller state in XInput terms (XINPUT_GAMEPAD).
    public struct PadInput
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;

        public bool IsNeutral => Buttons == 0 && LeftTrigger == 0 && RightTrigger == 0 && LX == 0 && LY == 0 && RX == 0 && RY == 0;
    }

    public static class XInputButtons
    {
        public const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
        public const ushort Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080;
        public const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200;
        public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
    }
}
