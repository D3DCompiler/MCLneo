using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MCLneo.Core.Worlds;

/// <summary>Minimal big-endian NBT reader, sufficient for level.dat metadata.</summary>
public static class NbtReader
{
    public static Dictionary<string, object?> ReadGzip(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return Read(gzip);
    }

    public static Dictionary<string, object?> Read(Stream stream)
    {
        var type = ReadByte(stream);
        if (type != 10)
        {
            throw new InvalidDataException($"NBT 根标签不是 Compound（type={type}）");
        }

        _ = ReadString(stream);
        return ReadCompound(stream);
    }

    private static Dictionary<string, object?> ReadCompound(Stream stream)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            var type = ReadByte(stream);
            if (type == 0)
            {
                return result;
            }

            var name = ReadString(stream);
            result[name] = ReadPayload(stream, type);
        }
    }

    private static object? ReadPayload(Stream stream, byte type)
    {
        return type switch
        {
            1 => (sbyte)ReadByte(stream),
            2 => ReadInt16(stream),
            3 => ReadInt32(stream),
            4 => ReadInt64(stream),
            5 => BitConverter.Int32BitsToSingle(ReadInt32(stream)),
            6 => BitConverter.Int64BitsToDouble(ReadInt64(stream)),
            7 => ReadBytes(stream),
            8 => ReadString(stream),
            9 => ReadList(stream),
            10 => ReadCompound(stream),
            11 => ReadIntArray(stream),
            12 => ReadLongArray(stream),
            _ => throw new InvalidDataException($"未知 NBT 标签：{type}")
        };
    }

    private static List<object?> ReadList(Stream stream)
    {
        var elementType = ReadByte(stream);
        var length = ReadInt32(stream);
        if (length < 0 || length > 10_000_000)
        {
            throw new InvalidDataException("NBT 列表长度异常");
        }

        var list = new List<object?>(Math.Min(length, 1024));
        for (var i = 0; i < length; i++)
        {
            list.Add(ReadPayload(stream, elementType));
        }

        return list;
    }

    private static byte[] ReadBytes(Stream stream)
    {
        var length = ReadInt32(stream);
        if (length < 0 || length > 512 * 1024 * 1024)
        {
            throw new InvalidDataException("NBT 字节数组长度异常");
        }

        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static int[] ReadIntArray(Stream stream)
    {
        var length = ReadInt32(stream);
        if (length < 0 || length > 10_000_000)
        {
            throw new InvalidDataException("NBT 整型数组长度异常");
        }

        var values = new int[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = ReadInt32(stream);
        }

        return values;
    }

    private static long[] ReadLongArray(Stream stream)
    {
        var length = ReadInt32(stream);
        if (length < 0 || length > 10_000_000)
        {
            throw new InvalidDataException("NBT 长整型数组长度异常");
        }

        var values = new long[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = ReadInt64(stream);
        }

        return values;
    }

    private static string ReadString(Stream stream)
    {
        var length = ReadUInt16(stream);
        if (length == 0)
        {
            return "";
        }

        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value < 0 ? throw new EndOfStreamException("NBT 数据提前结束") : (byte)value;
    }

    private static ushort ReadUInt16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    private static short ReadInt16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt16BigEndian(buffer);
    }

    private static int ReadInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt32BigEndian(buffer);
    }

    private static long ReadInt64(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadInt64BigEndian(buffer);
    }
}
