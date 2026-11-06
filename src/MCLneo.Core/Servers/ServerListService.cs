using System.Buffers.Binary;
using System.Text;
using MCLneo.Core.Worlds;

namespace MCLneo.Core.Servers;

public sealed record ServerEntry(string Name, string Ip, bool AcceptTextures = true)
{
    public string Address
    {
        get
        {
            var text = Ip.Trim();
            if (text.StartsWith("["))
            {
                var end = text.IndexOf(']');
                return end > 0 ? text[1..end] : text;
            }

            var colon = text.LastIndexOf(':');
            return colon > 0 && text.Count(c => c == ':') == 1 ? text[..colon] : text;
        }
    }

    public int? Port
    {
        get
        {
            var text = Ip.Trim();
            if (text.StartsWith("["))
            {
                var end = text.IndexOf(']');
                if (end > 0 && end + 2 < text.Length && int.TryParse(text[(end + 2)..], out var ipv6Port))
                {
                    return ipv6Port;
                }

                return 25565;
            }

            var colon = text.LastIndexOf(':');
            return colon > 0 && text.Count(c => c == ':') == 1 &&
                   int.TryParse(text[(colon + 1)..], out var port)
                ? port
                : 25565;
        }
    }
}

public static class ServerListService
{
    public static string GetPath(Models.GameInstance instance) =>
        Path.Combine(instance.Directory, "servers.dat");

    public static IReadOnlyList<ServerEntry> Read(Models.GameInstance instance)
    {
        var path = GetPath(instance);
        if (!File.Exists(path))
        {
            return Array.Empty<ServerEntry>();
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var root = NbtReader.Read(stream);
        if (!root.TryGetValue("servers", out var node) || node is not List<object?> list)
        {
            return Array.Empty<ServerEntry>();
        }

        var result = new List<ServerEntry>();
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> server)
            {
                continue;
            }

            var name = server.TryGetValue("name", out var n) ? n as string ?? "" : "";
            var ip = server.TryGetValue("ip", out var i) ? i as string ?? "" : "";
            var accept = !server.TryGetValue("acceptTextures", out var a) || a is sbyte b && b != 0;
            if (!string.IsNullOrWhiteSpace(ip))
            {
                result.Add(new ServerEntry(name, ip, accept));
            }
        }

        return result;
    }

    public static void Write(Models.GameInstance instance, IReadOnlyList<ServerEntry> servers)
    {
        var path = GetPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            WriteRoot(stream, servers);
        }

        File.Move(temp, path, true);
    }

    private static void WriteRoot(Stream stream, IReadOnlyList<ServerEntry> servers)
    {
        WriteByte(stream, 10);
        WriteString(stream, "");
        WriteByte(stream, 9); // TAG_List
        WriteString(stream, "servers");
        WriteByte(stream, 10); // compound elements
        WriteInt(stream, servers.Count);
        foreach (var server in servers)
        {
            WriteByte(stream, 8); WriteString(stream, "name"); WriteString(stream, server.Name);
            WriteByte(stream, 8); WriteString(stream, "ip"); WriteString(stream, server.Ip);
            WriteByte(stream, 1); WriteString(stream, "acceptTextures"); WriteByte(stream, (byte)(server.AcceptTextures ? 1 : 0));
            WriteByte(stream, 0);
        }

        WriteByte(stream, 0);
    }

    private static void WriteByte(Stream stream, byte value) => stream.WriteByte(value);

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)Math.Min(bytes.Length, ushort.MaxValue));
        stream.Write(length);
        stream.Write(bytes, 0, Math.Min(bytes.Length, ushort.MaxValue));
    }
}
