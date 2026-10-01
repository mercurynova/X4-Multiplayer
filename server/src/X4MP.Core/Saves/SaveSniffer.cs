using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using X4MP.Proto;

namespace X4MP.Core.Saves;

/// <summary>Outcome of <see cref="SaveSniffer.Check"/>.</summary>
public enum SniffResult
{
    Ok,

    /// <summary>A save that is not gzip (no 1F 8B magic, or the stream does not decompress).</summary>
    NotGzip,

    /// <summary>gzip, but the root element is not <c>&lt;savegame</c>; or a manifest without the <c>X4MF</c> file identifier.</summary>
    NotASave,
}

/// <summary>
/// Content checks before a file enters the store (server-design 3.1): a save must be gzip whose first 256 KB decompress to XML with a
/// <c>&lt;savegame</c> root, and the <c>&lt;info&gt;</c> block is read for the metadata; a manifest must carry the <c>X4MF</c> FlatBuffers
/// file identifier. Never throws for bad content.
/// </summary>
public static class SaveSniffer
{
    /// <summary>How much of the decompressed save is looked at.</summary>
    public const int PrefixBytes = 256 * 1024;

    public static SniffResult Check(string path, UploadKind kind, out SaveMeta meta)
    {
        meta = SaveMeta.Empty;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            return kind == UploadKind.Manifest ? CheckManifest(file) : CheckSave(file, out meta);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return SniffResult.NotGzip;
        }
    }

    private static SniffResult CheckManifest(Stream file)
    {
        Span<byte> head = stackalloc byte[8];
        if (file.Read(head) < 8)
        {
            return SniffResult.NotASave;
        }

        // FlatBuffers: u32 root offset, then the 4-byte file identifier.
        return head[4] == 'X' && head[5] == '4' && head[6] == 'M' && head[7] == 'F' && BinaryPrimitives.ReadUInt32LittleEndian(head) >= 8
            ? SniffResult.Ok
            : SniffResult.NotASave;
    }

    private static SniffResult CheckSave(Stream file, out SaveMeta meta)
    {
        meta = SaveMeta.Empty;
        Span<byte> magic = stackalloc byte[2];
        if (file.Read(magic) < 2 || magic[0] != 0x1F || magic[1] != 0x8B)
        {
            return SniffResult.NotGzip;
        }

        file.Position = 0;
        var prefix = new byte[PrefixBytes];
        int read = 0;
        using (var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true))
        {
            try
            {
                while (read < prefix.Length)
                {
                    int n = gzip.Read(prefix, read, prefix.Length - read);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }
            }
            catch (InvalidDataException)
            {
                if (read == 0)
                {
                    return SniffResult.NotGzip;
                }
            }
        }

        string text = Encoding.UTF8.GetString(prefix, 0, read);
        if (!RootIsSaveGame(text))
        {
            return SniffResult.NotASave;
        }

        meta = ReadInfo(text);
        return SniffResult.Ok;
    }

    /// <summary>True when the first element of the document (after the XML declaration, comments and whitespace) is <c>savegame</c>.</summary>
    internal static bool RootIsSaveGame(string text)
    {
        int i = 0;
        if (text.Length > 0 && text[0] == '﻿')
        {
            i = 1;
        }

        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] != '<')
            {
                return false;
            }

            if (string.CompareOrdinal(text, i, "<?", 0, 2) == 0)
            {
                int end = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                i = end + 2;
                continue;
            }

            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }

                i = end + 3;
                continue;
            }

            const string root = "<savegame";
            return string.CompareOrdinal(text, i, root, 0, root.Length) == 0
                && (i + root.Length >= text.Length || text[i + root.Length] is '>' or '/' or ' ' or '\t' or '\r' or '\n');
        }

        return false;
    }

    /// <summary>Reads the version, save time, player name and money from the (possibly truncated) document. Best effort.</summary>
    internal static SaveMeta ReadInfo(string text)
    {
        string? version = null;
        string? time = null;
        string? player = null;
        long? money = null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "info")
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.Name)
                {
                    case "save":
                        time ??= reader.GetAttribute("date");
                        break;
                    case "game":
                        version ??= reader.GetAttribute("version");
                        break;
                    case "player":
                        player ??= reader.GetAttribute("name");
                        if (reader.GetAttribute("money") is { } m && long.TryParse(m, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                        {
                            money ??= value;
                        }

                        break;
                }
            }
        }
        catch (XmlException)
        {
            // truncated at the end of the prefix: keep what was read
        }

        return new SaveMeta(version, time, player, money);
    }
}
