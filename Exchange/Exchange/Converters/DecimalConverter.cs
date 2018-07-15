﻿using System;
using System.Runtime.CompilerServices;

namespace Mikodev.Network.Converters
{
    [Converter(typeof(decimal))]
    internal sealed class DecimalConverter : PacketConverter<decimal>
    {
        private static readonly bool origin = BitConverter.IsLittleEndian == PacketConvert.UseLittleEndian;

        private static byte[] ToBytes(decimal value)
        {
            var target = new byte[sizeof(decimal)];
            var source = decimal.GetBits(value);
            if (origin)
                Unsafe.CopyBlockUnaligned(ref target[0], ref Unsafe.As<int, byte>(ref source[0]), sizeof(decimal));
            else
                Endian.SwapRange<int>(ref target[0], ref Unsafe.As<int, byte>(ref source[0]), sizeof(decimal));
            return target;
        }

        private static decimal ToValue(byte[] buffer, int offset, int length)
        {
            if (buffer == null || offset < 0 || length < sizeof(decimal) || buffer.Length - offset < length)
                throw PacketException.Overflow();
            var target = new int[sizeof(decimal) / sizeof(int)];
            if (origin)
                Unsafe.CopyBlockUnaligned(ref Unsafe.As<int, byte>(ref target[0]), ref buffer[offset], sizeof(decimal));
            else
                Endian.SwapRange<int>(ref Unsafe.As<int, byte>(ref target[0]), ref buffer[offset], sizeof(decimal));
            var result = new decimal(target);
            return result;
        }

        public DecimalConverter() : base(sizeof(decimal)) { }

        public override byte[] GetBytes(decimal value) => ToBytes(value);

        public override decimal GetValue(byte[] buffer, int offset, int length) => ToValue(buffer, offset, length);

        public override byte[] GetBytes(object value) => ToBytes((decimal)value);

        public override object GetObject(byte[] buffer, int offset, int length) => ToValue(buffer, offset, length);
    }
}
