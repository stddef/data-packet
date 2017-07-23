﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Mikodev.Network
{
    /// <summary>
    /// 扩展方法模块
    /// </summary>
    public static partial class PacketExtensions
    {
        internal static bool _IsValueType(this Type type) => type.GetTypeInfo().IsValueType;

        internal static bool _IsGenericEnumerable(this Type type) => type.GetTypeInfo().IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>);

        internal static bool _IsEnumerable(this Type typ, out Type inn)
        {
            foreach (var i in typ.GetTypeInfo().GetInterfaces())
            {
                if (i._IsGenericEnumerable())
                {
                    var som = i.GetGenericArguments();
                    if (som.Length != 1)
                        continue;
                    inn = som[0];
                    return true;
                }
            }
            inn = null;
            return false;
        }

        internal static byte[] _Merge(this byte[] buffer, params byte[][] values)
        {
            var str = new MemoryStream();
            str.Write(buffer, 0, buffer.Length);
            foreach (var v in values)
                str.Write(v, 0, v.Length);
            return str.ToArray();
        }

        internal static byte[] _Split(this byte[] buffer, int offset, int length)
        {
            if (length > buffer.Length)
                throw new PacketException(PacketError.Overflow);
            var buf = new byte[length];
            Array.Copy(buffer, offset, buf, 0, length);
            return buf;
        }

        internal static void _Write(this Stream stream, byte[] buffer) => stream.Write(buffer, 0, buffer.Length);

        internal static void _Write<T>(this Stream stream, T value) where T : struct => stream._Write(value._GetBytes());

        internal static void _WriteExt(this Stream stream, byte[] buffer)
        {
            var len = BitConverter.GetBytes(buffer.Length);
            stream.Write(len, 0, len.Length);
            stream.Write(buffer, 0, buffer.Length);
        }

        internal static byte[] _Read(this Stream stream, int length)
        {
            if (length < 0 || stream.Position + length > stream.Length)
                return null;
            var buf = new byte[length];
            stream.Read(buf, 0, length);
            return buf;
        }

        internal static byte[] _ReadExt(this Stream stream)
        {
            var hdr = stream._Read(sizeof(int));
            if (hdr == null)
                return null;
            var len = BitConverter.ToInt32(hdr, 0);
            var res = stream._Read(len);
            return res;
        }

        internal static bool _GetConverter(Type type, out PacketConverter value)
        {
            if (type._IsValueType() == false || type.GetTypeInfo().IsGenericType == true)
            {
                value = null;
                return false;
            }

            value = new PacketConverter(
                obj => obj._GetBytes(type),
                (buf, off, len) => buf._GetValue(off, len, type),
                Marshal.SizeOf(type));
            return true;
        }

        internal static readonly string[] s_Separators = new string[] { "/", @"\" };

        internal static readonly Dictionary<Type, PacketConverter> s_Converters = new Dictionary<Type, PacketConverter>()
        {
            [typeof(byte[])] = new PacketConverter(
                obj => (byte[])obj,
                _Split,
                null),

            [typeof(string)] = new PacketConverter(
                obj => Encoding.UTF8.GetBytes((string)obj),
                Encoding.UTF8.GetString,
                null),

            [typeof(DateTime)] = new PacketConverter(
                obj => ((DateTime)obj).ToBinary()._GetBytes(),
                (buf, off, len) => DateTime.FromBinary(buf._GetValue<long>(off, len)),
                sizeof(long)),

            [typeof(IPAddress)] = new PacketConverter(
                obj => ((IPAddress)obj).GetAddressBytes(),
                (buf, off, len) => new IPAddress(buf._Split(off, len)),
                null),

            [typeof(IPEndPoint)] = new PacketConverter(
                obj => _EndPointToBinary((IPEndPoint)obj),
                _BinaryToEndPoint,
                null),
        };

        /// <summary>
        /// 默认的路径分隔符
        /// <para>Default path separators</para>
        /// </summary>
        public static IReadOnlyList<string> Separators => s_Separators;

        /// <summary>
        /// Default binary converters
        /// </summary>
        public static IReadOnlyDictionary<Type, PacketConverter> Converters => s_Converters;
    }
}
