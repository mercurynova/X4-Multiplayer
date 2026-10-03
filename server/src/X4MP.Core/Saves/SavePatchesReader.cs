using System.IO.Compression;
using System.Text;
using System.Xml;

namespace X4MP.Core.Saves;

/// <summary>One extension a save depends on (an entry of the save's <c>&lt;patches&gt;</c> block).</summary>
public sealed record SavePatch(string Extension, string Name, string Version);

/// <summary>
/// Reads the <c>&lt;patches&gt;</c> block (the extensions a save needs, mod-management 1.4) from the head of a gzip'd save: only the first
/// <see cref="PrefixBytes"/> of the decompressed XML are looked at, so a multi-hundred-MB save costs a few milliseconds. Best effort: a
/// file that is not a save, or a head that ends before the block, yields an empty list. Never throws for bad content.
/// </summary>
public static class SavePatchesReader
{
    /// <summary>How much of the decompressed save is looked at.</summary>
    public const int PrefixBytes = 64 * 1024;

    public static IReadOnlyList<SavePatch> Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var prefix = new byte[PrefixBytes];
            int read = 0;
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
                // a damaged tail: parse what was read
            }

            return Parse(Encoding.UTF8.GetString(prefix, 0, read));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Parses the <c>&lt;patch extension= name= version=&gt;</c> children of the first <c>&lt;patches&gt;</c> element in <paramref name="text"/> (which may be cut off).</summary>
    internal static IReadOnlyList<SavePatch> Parse(string text)
    {
        var list = new List<SavePatch>();
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            int patchesDepth = -1;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "patches" && reader.Depth == patchesDepth)
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (patchesDepth < 0)
                {
                    if (reader.Name == "patches")
                    {
                        if (reader.IsEmptyElement)
                        {
                            break;
                        }

                        patchesDepth = reader.Depth;
                    }
                }
                else if (reader.Name == "patch" && reader.Depth == patchesDepth + 1 && reader.GetAttribute("extension") is { Length: > 0 } id)
                {
                    list.Add(new SavePatch(id, reader.GetAttribute("name") ?? string.Empty, reader.GetAttribute("version") ?? string.Empty));
                }
            }
        }
        catch (XmlException)
        {
            // cut off inside the document: keep what was read
        }

        return list;
    }
}
