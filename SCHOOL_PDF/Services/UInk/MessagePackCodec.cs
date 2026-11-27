using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SchoolPdf.Services.UInk;

/// <summary>最小 MessagePack 写入器：只实现 UInk 用到的类型，且严格按规范要求的固定位宽输出
/// （TYPE-1：uint16 不得写成 fixint、float32 不得写成 float64、Header.guid 用 str8）。
/// 不引入第三方包：UInk 只用 uint16/uint32/uint64/int32/float32/string/bool/array/map 这几类。</summary>
internal sealed class MpWriter
{
    private readonly MemoryStream _stream = new();

    public byte[] ToArray() => _stream.ToArray();

    public void WriteArray(int count)
    {
        if (count <= 15)
            _stream.WriteByte((byte)(0x90 | count));
        else
        {
            _stream.WriteByte(0xDC);
            WriteUInt16Raw((ushort)count);
        }
    }

    public void WriteMap(int count)
    {
        if (count <= 15)
            _stream.WriteByte((byte)(0x80 | count));
        else
        {
            _stream.WriteByte(0xDE);
            WriteUInt16Raw((ushort)count);
        }
    }

    public void WriteUInt16(ushort value)
    {
        _stream.WriteByte(0xCD);
        WriteUInt16Raw(value);
    }

    public void WriteUInt32(uint value)
    {
        _stream.WriteByte(0xCE);
        WriteUInt32Raw(value);
    }

    public void WriteUInt64(ulong value)
    {
        _stream.WriteByte(0xCF);
        WriteUInt32Raw((uint)(value >> 32));
        WriteUInt32Raw((uint)value);
    }

    public void WriteInt32(int value)
    {
        _stream.WriteByte(0xD2);
        uint raw = (uint)value;
        WriteUInt32Raw(raw);
    }

    /// <summary>MessagePack 一律大端：float32 先取位再按大端写（不能直接用 BitConverter.GetBytes 的小端字节）。</summary>
    public void WriteFloat32(float value)
    {
        _stream.WriteByte(0xCA);
        WriteUInt32Raw((uint)BitConverter.SingleToInt32Bits(value));
    }

    public void WriteBool(bool value) => _stream.WriteByte(value ? (byte)0xC3 : (byte)0xC2);

    /// <summary>字符串：≤15 字符用 fixstr；≤255 用 str8（Header.guid 36 字符正好走 str8）。</summary>
    public void WriteString(string? value)
    {
        value ??= string.Empty;
        int length = Encoding.UTF8.GetByteCount(value);
        if (length <= 15)
            _stream.WriteByte((byte)(0xA0 | length));
        else if (length <= 255)
        {
            _stream.WriteByte(0xD9);
            _stream.WriteByte((byte)length);
        }
        else
        {
            _stream.WriteByte(0xDA);
            WriteUInt16Raw((ushort)length);
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        _stream.Write(bytes, 0, bytes.Length);
    }

    private void WriteUInt16Raw(ushort value)
    {
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }

    private void WriteUInt32Raw(uint value)
    {
        _stream.WriteByte((byte)(value >> 24));
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
    }
}

/// <summary>最小 MessagePack 读取器：解码成通用对象树（null / bool / long / ulong / double / string /
/// List&lt;object?&gt; / Dictionary&lt;string, object?&gt;），未知类型按 EXT 跳过。
/// 解码失败抛出 IOException —— 调用方按 UInk 崩溃恢复规则（FILE-16/17）保留之前的完整对象。</summary>
internal static class MpReader
{
    public static object? Read(byte[] data, ref int pos)
    {
        if (pos >= data.Length)
            throw new IOException("对象流已到末尾");
        byte marker = data[pos++];
        if (marker <= 0x7F)                       // positive fixint
            return (long)marker;
        if (marker >= 0xE0)                        // negative fixint
            return (long)(int)marker;
        if (marker >= 0x80 && marker <= 0x8F)      // fixmap
            return ReadMap(data, ref pos, marker & 0x0F);
        if (marker >= 0x90 && marker <= 0x9F)      // fixarray
            return ReadArray(data, ref pos, marker & 0x0F);
        if (marker >= 0xA0 && marker <= 0xBF)      // fixstr
            return ReadString(data, ref pos, marker & 0x1F);

        switch (marker)
        {
            case 0xC0: return null;
            case 0xC2: return false;
            case 0xC3: return true;
            case 0xC4: { int n = ReadU8(data, ref pos); Skip(data, ref pos, n); return null; }   // bin8
            case 0xC5: { int n = ReadU16(data, ref pos); Skip(data, ref pos, n); return null; }  // bin16
            case 0xC6: { int n = (int)ReadU32(data, ref pos); Skip(data, ref pos, n); return null; }
            case 0xCA: return (double)BitConverter.Int32BitsToSingle((int)ReadU32(data, ref pos)); // float32
            case 0xCB: return BitConverter.Int64BitsToDouble((long)ReadU64(data, ref pos));        // float64
            case 0xCC: return (long)ReadU8(data, ref pos);                                      // uint8
            case 0xCD: return (long)ReadU16(data, ref pos);                                     // uint16
            case 0xCE: return (long)ReadU32(data, ref pos);                                     // uint32
            case 0xCF: return (ulong)ReadU64(data, ref pos);                                    // uint64
            case 0xD0: return (long)(sbyte)ReadU8(data, ref pos);                               // int8
            case 0xD1: return (long)(short)ReadU16(data, ref pos);                              // int16
            case 0xD2: return (long)(int)ReadU32Signed(data, ref pos);                          // int32
            case 0xD3: return (long)(long)ReadU64Signed(data, ref pos);                         // int64
            case 0xD9: return ReadString(data, ref pos, ReadU8(data, ref pos));                  // str8
            case 0xDA: return ReadString(data, ref pos, ReadU16(data, ref pos));                // str16
            case 0xDB: return ReadString(data, ref pos, (int)ReadU32(data, ref pos));           // str32
            case 0xDC: return ReadArray(data, ref pos, ReadU16(data, ref pos));                 // array16
            case 0xDD: return ReadArray(data, ref pos, (int)ReadU32(data, ref pos));            // array32
            case 0xDE: return ReadMap(data, ref pos, ReadU16(data, ref pos));                   // map16
            case 0xDF: return ReadMap(data, ref pos, (int)ReadU32(data, ref pos));              // map32
            case 0xD4: Skip(data, ref pos, 1 + 1); return null;                                 // fixext1
            case 0xD5: Skip(data, ref pos, 1 + 2); return null;                                 // fixext2
            case 0xD6: Skip(data, ref pos, 1 + 4); return null;                                 // fixext4
            case 0xD7: Skip(data, ref pos, 1 + 8); return null;                                 // fixext8
            case 0xD8: Skip(data, ref pos, 1 + 16); return null;                                // fixext16
            case 0xC7: { Skip(data, ref pos, 1); int n = ReadU8(data, ref pos); Skip(data, ref pos, n); return null; }
            case 0xC8: { Skip(data, ref pos, 1); int n = ReadU16(data, ref pos); Skip(data, ref pos, n); return null; }
            case 0xC9: { Skip(data, ref pos, 1); int n = (int)ReadU32(data, ref pos); Skip(data, ref pos, n); return null; }
            default:
                throw new IOException($"未知 MessagePack 标记 0x{marker:X2}");
        }
    }

    private static List<object?> ReadArray(byte[] data, ref int pos, int count)
    {
        var list = new List<object?>(count);
        for (int i = 0; i < count; i++)
            list.Add(Read(data, ref pos));
        return list;
    }

    private static Dictionary<string, object?> ReadMap(byte[] data, ref int pos, int count)
    {
        var map = new Dictionary<string, object?>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            object? key = Read(data, ref pos);
            object? value = Read(data, ref pos);
            if (key is string text)
                map[text] = value;
        }
        return map;
    }

    private static string ReadString(byte[] data, ref int pos, int length)
        => Encoding.UTF8.GetString(ReadBytes(data, ref pos, length));

    private static byte[] ReadBytes(byte[] data, ref int pos, int length)
    {
        if (length < 0 || pos + length > data.Length)
            throw new IOException("对象流越界");
        var bytes = new byte[length];
        Buffer.BlockCopy(data, pos, bytes, 0, length);
        pos += length;
        return bytes;
    }

    private static void Skip(byte[] data, ref int pos, int length)
    {
        if (length < 0 || pos + length > data.Length)
            throw new IOException("对象流越界");
        pos += length;
    }

    private static int ReadU8(byte[] data, ref int pos)
    {
        if (pos >= data.Length)
            throw new IOException("对象流越界");
        return data[pos++];
    }

    private static ushort ReadU16(byte[] data, ref int pos)
    {
        int b0 = ReadU8(data, ref pos);
        int b1 = ReadU8(data, ref pos);
        return (ushort)((b0 << 8) | b1);
    }

    private static uint ReadU32(byte[] data, ref int pos)
    {
        uint hi = ReadU16(data, ref pos);
        uint lo = ReadU16(data, ref pos);
        return (hi << 16) | lo;
    }

    private static int ReadU32Signed(byte[] data, ref int pos)
    {
        uint raw = ReadU32(data, ref pos);
        return (int)raw;
    }

    private static ulong ReadU64(byte[] data, ref int pos)
    {
        ulong hi = ReadU32(data, ref pos);
        ulong lo = ReadU32(data, ref pos);
        return (hi << 32) | lo;
    }

    private static long ReadU64Signed(byte[] data, ref int pos)
    {
        ulong raw = ReadU64(data, ref pos);
        return (long)raw;
    }
}

/// <summary>对象树取值小工具：容错接受整数/浮点的其他编码（UInk TYPE-5）。</summary>
internal static class MpValues
{
    public static long? AsLong(object? value)
    {
        switch (value)
        {
            case long l: return l;
            case ulong u: return u <= long.MaxValue ? (long)u : null;
            case double d: return d >= long.MinValue && d <= long.MaxValue && d == Math.Floor(d) ? (long)d : null;
            default: return null;
        }
    }

    public static int? AsInt(object? value)
    {
        long? l = AsLong(value);
        return l is >= int.MinValue and <= int.MaxValue ? (int)l.Value : null;
    }

    public static double? AsDouble(object? value)
    {
        switch (value)
        {
            case long l: return l;
            case ulong u: return (double)u;
            case double d: return d;
            default: return null;
        }
    }

    public static float? AsFloat(object? value)
    {
        double? d = AsDouble(value);
        if (d is null)
            return null;
        if (double.IsNaN(d.Value) || double.IsInfinity(d.Value))
            return null;
        return (float)d.Value;
    }

    public static string? AsString(object? value) => value as string;

    public static bool? AsBool(object? value) => value as bool?;

    public static List<object?>? AsList(object? value) => value as List<object?>;

    public static Dictionary<string, object?>? AsMap(object? value) => value as Dictionary<string, object?>;
}
