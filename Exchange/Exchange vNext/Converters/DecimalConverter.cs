﻿using System;

namespace Mikodev.Binary.Converters
{
    internal sealed class DecimalConverter : Converter<decimal>
    {
        private static readonly bool origin = BitConverter.IsLittleEndian == UseLittleEndian;

        public DecimalConverter() : base(sizeof(decimal)) { }

        public override unsafe void ToBytes(Allocator allocator, decimal value)
        {
            var source = decimal.GetBits(value);
            fixed (byte* dstptr = &allocator.Allocate(sizeof(decimal)))
            fixed (int* srcptr = &source[0])
            {
                if (origin)
                    Unsafe.Copy(dstptr, srcptr, sizeof(decimal));
                else
                    Endian.SwapCopy(dstptr, srcptr, sizeof(decimal));
            }
        }

        public override unsafe decimal ToValue(ReadOnlyMemory<byte> memory)
        {
            if (memory.Length < sizeof(decimal))
                ThrowHelper.ThrowOverflow();
            var bits = new int[4];
            fixed (byte* srcptr = memory.Span)
            fixed (int* dstptr = &bits[0])
            {
                if (origin)
                    Unsafe.Copy(dstptr, srcptr, sizeof(decimal));
                else
                    Endian.SwapCopy(dstptr, srcptr, sizeof(decimal));
            }
            return new decimal(bits);
        }
    }
}
