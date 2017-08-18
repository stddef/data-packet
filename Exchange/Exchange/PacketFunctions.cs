﻿using System;
using System.Net;
using System.Runtime.InteropServices;
using static System.BitConverter;

namespace Mikodev.Network
{
    public static partial class PacketExtensions
    {
        internal static unsafe byte[] _GetBytes(this object str)
        {
            var len = Marshal.SizeOf(str);
            var buf = new byte[len];
            fixed (byte* ptr = buf)
            {
                Marshal.StructureToPtr(str, (IntPtr)ptr, true);
            }
            return buf;
        }

        internal static unsafe object _GetValue(this byte[] buffer, int offset, int length, Type type)
        {
            var len = Marshal.SizeOf(type);
            if (len > length)
                throw new PacketException(PacketError.Overflow);
            if (offset < 0 || offset + len > buffer.Length)
                throw new PacketException(PacketError.AssertFailed);
            fixed (byte* ptr = buffer)
            {
                return Marshal.PtrToStructure((IntPtr)(ptr + offset), type);
            }
        }

        internal static byte[] _EndPointToBinary(IPEndPoint value)
        {
            var add = value.Address.GetAddressBytes();
            var pot = GetBytes((ushort)value.Port);
            return add._Merge(pot);
        }

        internal static IPEndPoint _BinaryToEndPoint(byte[] buffer, int offset, int length)
        {
            var add = new IPAddress(buffer._Split(offset, length - sizeof(ushort)));
            var pot = ToUInt16(buffer, offset + length - sizeof(ushort));
            return new IPEndPoint(add, pot);
        }
    }
}
