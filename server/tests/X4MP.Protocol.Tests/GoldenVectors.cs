using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using X4MP.Proto;

namespace X4MP.Protocol.Tests;

/// <summary>
/// Golden vector generator (M0-06). Produces <c>protocol/testdata/*.bin</c> plus <c>index.json</c> for every
/// message type (as a complete TCP frame), the Replication entry codec (every field mask), quantisation,
/// the UDP datagram codec and a set of must-reject inputs. The C++ and Python implementations decode
/// these and must agree; the C# regeneration test fails if anything drifts.
/// </summary>
public static class GoldenVectors
{
    public sealed record Vector(string File, string Kind, byte[] Bytes, Action<Utf8JsonWriter> Meta);

    public static string TestDataDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "protocol", "schema")))
                dir = dir.Parent;
            return dir is null
                ? throw new InvalidOperationException("Could not locate the repo root (protocol/schema).")
                : Path.Combine(dir.FullName, "protocol", "testdata");
        }
    }

    private static readonly DatagramHeader DatagramHdr = new(0, 0xDEADBEEF, 7, 5, 0b1011);

    public static IReadOnlyList<Vector> Build()
    {
        var list = new List<Vector>();

        // ---- one complete TCP frame per message type / union variant ----
        foreach (var s in SampleMessages.All)
        {
            var payload = s.Encode();
            var frame = FrameCodec.Encode(s.Type, payload);
            var lane = MessageRegistry.Default.GetDescriptor(s.Type).Lane;
            var fields = CanonicalFields.Serialize(UnPackDecoded(s.Type, payload));
            list.Add(new Vector($"{s.Stem}.bin", "frame", frame, w =>
            {
                w.WriteNumber("msgType", (ushort)s.Type);
                w.WriteString("msgName", s.Type.ToString());
                w.WriteString("variant", s.Variant);
                w.WriteNumber("lane", (int)lane);
                w.WriteNumber("payloadOffset", FrameCodec.HeaderSize);
                w.WriteNumber("payloadLength", payload.Length);
                w.WritePropertyName("fields");
                w.WriteRawValue(fields);
            }));
        }

        // ---- Replication entry codec ----
        list.Add(ReplicationVector("repl_moving_ship", ReplicationMask.Pos | ReplicationMask.Vel | ReplicationMask.Time));
        list.Add(ReplicationVector("repl_turning_ship", ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel | ReplicationMask.Time));
        list.Add(ReplicationVector("repl_status_only", ReplicationMask.Status));
        list.Add(ReplicationVector("repl_keyframe", ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel
                                                    | ReplicationMask.Flags | ReplicationMask.Status | ReplicationMask.Time));
        list.Add(ReplicationVector("repl_ext_skip", ReplicationMask.Status | ReplicationMask.Ext, ReplicationMask.Sector));
        list.Add(ReplicationVector("repl_angle_wrap", ReplicationMask.Rot, angleWrap: true));
        list.Add(ReplicationVector("repl_coarse_velocity", ReplicationMask.Vel | ReplicationMask.Flags, coarse: true));

        var allMasks = Enumerable.Range(0, 256).Select(m => MaskEntry((ReplicationMask)m, (uint)(1000 + m))).ToArray();
        var allBytes = ReplicationCodec.Encode(allMasks);
        list.Add(new Vector("repl_all_masks.bin", "replication-entries", allBytes, w =>
        {
            w.WriteNumber("entryCount", allMasks.Length);
            w.WriteStartArray("entries");
            int offset = 0;
            foreach (var e in allMasks)
            {
                int size = ReplicationCodec.GetSize(e);
                w.WriteStartObject();
                w.WriteNumber("netId", e.NetId);
                w.WriteNumber("mask", (byte)e.Mask);
                w.WriteNumber("offset", offset);
                w.WriteNumber("length", size);
                w.WriteEndObject();
                offset += size;
            }
            w.WriteEndArray();
        }));

        // ---- UDP datagrams ----
        list.Add(new Vector("dgram_ack_only.bin", "datagram", new DatagramCodec.Builder(DatagramHdr).Build(), w => DatagramMeta(w, [])));

        var mixed = new DatagramCodec.Builder(DatagramHdr);
        var subs = new[] { MsgType.Ping, MsgType.PlayerState, MsgType.Replication, MsgType.WorldUpdate };
        foreach (var t in subs)
            Require(mixed.TryAdd(t, SampleMessages.All.First(s => s.Type == t).Encode()), $"{t} must fit");
        var mixedBytes = mixed.Build();
        list.Add(new Vector("dgram_mixed.bin", "datagram", mixedBytes, w => DatagramMeta(w, DatagramCodec.ReadSubMessages(mixedBytes))));

        // ---- quantisation (inline in index.json) ----
        // handled in WriteIndex

        // ---- must-reject inputs ----
        var ping = SampleMessages.All.First(s => s.Type == MsgType.Ping).Encode();
        var welcome = FrameCodec.Encode(MsgType.Welcome, SampleMessages.All.First(s => s.Type == MsgType.Welcome).Encode());
        list.Add(Reject("bad_zero_length", Header(0, MsgType.Ping, 0, 0), ViolationCode.ZeroLengthFrame));
        list.Add(Reject("bad_oversized", Header((uint)FrameCodec.DefaultMaxFrameBytes + 1, MsgType.SaveChunk, 0, 2), ViolationCode.FrameTooLarge));
        list.Add(Reject("bad_reserved_flags", Concat(Header(4, MsgType.Ping, 0x02, 0), [1, 2, 3, 4]), ViolationCode.ReservedFlags));
        list.Add(Reject("bad_compressed", Concat(Header(4, MsgType.Ping, 0x01, 0), [1, 2, 3, 4]), ViolationCode.CompressedNotSupported));
        list.Add(Reject("bad_invalid_lane", Concat(Header(4, MsgType.Ping, 0, 3), [1, 2, 3, 4]), ViolationCode.InvalidLane));
        list.Add(Reject("bad_lane_mismatch", Concat(Header((uint)ping.Length, MsgType.Ping, 0, 2), ping), ViolationCode.LaneMismatch));
        list.Add(Reject("bad_unknown_type", Concat(Header((uint)ping.Length, (MsgType)0x7F01, 0, 0), ping), ViolationCode.UnknownMessageType));
        list.Add(Reject("bad_reserved_type", Concat(Header((uint)ping.Length, MsgType.DamageReport, 0, 0), ping), ViolationCode.ReservedMessageType));
        list.Add(Reject("bad_truncated", welcome[..(welcome.Length / 2)], ViolationCode.TruncatedFrame));
        list.Add(Reject("bad_payload_garbage", Concat(Header(8, MsgType.Ping, 0, 0), [0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0]), ViolationCode.MalformedPayload));

        var goodRepl = ReplicationCodec.Encode([MaskEntry(ReplicationMask.Pos | ReplicationMask.Time, 5)]);
        list.Add(RejectReplication("bad_repl_truncated", goodRepl[..^1], 1));
        list.Add(RejectReplication("bad_repl_net_id_zero", [0, 0, 0, 0, (byte)ReplicationMask.Status, 1, 2], 1));
        list.Add(RejectReplication("bad_repl_ext_overrun", [5, 0, 0, 0, (byte)ReplicationMask.Ext, 200, 1, 2, 3], 1));
        list.Add(RejectReplication("bad_repl_trailing", Concat(goodRepl, [0]), 1));

        var dg = new DatagramCodec.Builder(DatagramHdr).Build();
        list.Add(RejectDatagram("bad_dgram_magic", Mutate(dg, 0, 0x59)));
        list.Add(RejectDatagram("bad_dgram_major", Mutate(dg, 2, 1)));
        list.Add(RejectDatagram("bad_dgram_reserved", Mutate(dg, 23, 1)));
        list.Add(RejectDatagram("bad_dgram_short", dg[..23]));
        list.Add(RejectDatagram("bad_dgram_subheader_overrun", Concat(dg, [0x05, 0x00, 0xFF, 0x00, 1, 2])));

        return list;
    }

    // ---------------------------------------------------------------- vector builders

    /// <summary>Decodes a payload through the registry and returns its object-API form (all fields).</summary>
    private static object UnPackDecoded(MsgType type, byte[] payload)
    {
        var decoded = MessageRegistry.Default.Decode(type, payload);
        return decoded.GetType().GetMethod("UnPack")!.Invoke(decoded, null)!;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static byte[] Header(uint len, MsgType type, byte flags, byte lane)
    {
        var h = new byte[FrameCodec.HeaderSize];
        FrameCodec.WriteHeader(h, len, type, (FrameOptions)flags, (Lane)lane);
        return h;
    }

    private static byte[] Concat(byte[] a, byte[] b) => [.. a, .. b];

    private static byte[] Mutate(byte[] data, int index, byte value)
    {
        var copy = (byte[])data.Clone();
        copy[index] = value;
        return copy;
    }

    private static Vector Reject(string name, byte[] bytes, ViolationCode expected) =>
        new($"{name}.bin", "reject-frame", bytes, w => w.WriteString("expect", expected.ToString()));

    private static Vector RejectReplication(string name, byte[] bytes, int entryCount) =>
        new($"{name}.bin", "reject-replication-entries", bytes, w =>
        {
            w.WriteNumber("entryCount", entryCount);
            w.WriteString("expect", ViolationCode.MalformedReplication.ToString());
        });

    private static Vector RejectDatagram(string name, byte[] bytes) =>
        new($"{name}.bin", "reject-datagram", bytes, w => w.WriteString("expect", ViolationCode.MalformedDatagram.ToString()));

    /// <summary>Deterministic entry with distinctive values in exactly the fields named by the mask.</summary>
    public static ReplicationEntry MaskEntry(ReplicationMask mask, uint netId) => new()
    {
        NetId = netId, Mask = mask,
        Sector = (mask & ReplicationMask.Sector) != 0 ? (ushort)(0x1234 ^ netId) : (ushort)0,
        PosX = (mask & ReplicationMask.Pos) != 0 ? 64 + (int)netId : 0,
        PosY = (mask & ReplicationMask.Pos) != 0 ? -64 - (int)netId : 0,
        PosZ = (mask & ReplicationMask.Pos) != 0 ? 1_000_000 + (int)netId : 0,
        Yaw = (mask & ReplicationMask.Rot) != 0 ? (short)(16384 + netId) : (short)0,
        Pitch = (mask & ReplicationMask.Rot) != 0 ? (short)(-16384 - netId) : (short)0,
        Roll = (mask & ReplicationMask.Rot) != 0 ? short.MinValue : (short)0,
        VelX = (mask & ReplicationMask.Vel) != 0 ? (short)(4 + netId) : (short)0,
        VelY = (mask & ReplicationMask.Vel) != 0 ? (short)(-4 - netId) : (short)0,
        VelZ = (mask & ReplicationMask.Vel) != 0 ? short.MaxValue : (short)0,
        StateFlags = (mask & ReplicationMask.Flags) != 0 ? (ushort)(0x8001 ^ netId) : (ushort)0,
        Hull = (mask & ReplicationMask.Status) != 0 ? (byte)255 : (byte)0,
        Shield = (mask & ReplicationMask.Status) != 0 ? (byte)(netId & 0xFF) : (byte)0,
        TimeMs = (mask & ReplicationMask.Time) != 0 ? (short)(-20 - netId) : (short)0,
        Ext = (mask & ReplicationMask.Ext) != 0 ? [0xAA, 0xBB, 0xCC, (byte)netId] : null,
    };

    private static Vector ReplicationVector(string name, ReplicationMask mask, ReplicationMask? followedBy = null, bool angleWrap = false, bool coarse = false)
    {
        var entries = new List<ReplicationEntry>();
        if (angleWrap)
        {
            // pi and -pi (both -32768), 2*pi (0), 3*pi/2 (-16384), pi/2 + 2*pi (16384)
            double[][] sets = [[Math.PI, -Math.PI, 2 * Math.PI], [3 * Math.PI / 2, Math.PI / 2 + 2 * Math.PI, -3 * Math.PI / 2]];
            uint id = 1;
            foreach (var set in sets)
                entries.Add(new ReplicationEntry
                {
                    NetId = id++, Mask = ReplicationMask.Rot,
                    Yaw = Quantize.Rotation(set[0]), Pitch = Quantize.Rotation(set[1]), Roll = Quantize.Rotation(set[2]),
                });
        }
        else if (coarse)
        {
            entries.Add(new ReplicationEntry
            {
                NetId = 1, Mask = mask, StateFlags = (ushort)StateFlags.VelCoarse,
                VelX = Quantize.Velocity(20_000, true), VelY = Quantize.Velocity(-131_068, true), VelZ = Quantize.Velocity(2, true),
            });
            entries.Add(new ReplicationEntry
            {
                NetId = 2, Mask = mask, StateFlags = 0,
                VelX = Quantize.Velocity(8191.75, false), VelY = Quantize.Velocity(-0.125, false), VelZ = Quantize.Velocity(0.25, false),
            });
        }
        else
        {
            entries.Add(MaskEntry(mask, 0x01020304));
            if (followedBy is { } next)
                entries.Add(MaskEntry(next, 0x05060708));
        }

        var bytes = ReplicationCodec.Encode([.. entries]);
        return new Vector($"{name}.bin", "replication-entries", bytes, w =>
        {
            w.WriteNumber("entryCount", entries.Count);
            w.WriteStartArray("entries");
            int offset = 0;
            foreach (var e in entries)
            {
                int size = ReplicationCodec.GetSize(e);
                w.WriteStartObject();
                w.WriteNumber("netId", e.NetId);
                w.WriteNumber("mask", (byte)e.Mask);
                w.WriteNumber("offset", offset);
                w.WriteNumber("length", size);
                w.WriteEndObject();
                offset += size;
            }
            w.WriteEndArray();
        });
    }

    private static void DatagramMeta(Utf8JsonWriter w, List<(MsgType Type, int Offset, int Length)> subs)
    {
        w.WriteNumber("protoMajor", DatagramHdr.ProtoMajor);
        w.WriteNumber("connId", DatagramHdr.ConnId);
        w.WriteNumber("seq", DatagramHdr.Seq);
        w.WriteNumber("ack", DatagramHdr.Ack);
        w.WriteNumber("ackBits", DatagramHdr.AckBits);
        w.WriteStartArray("subMessages");
        foreach (var (type, offset, length) in subs)
        {
            w.WriteStartObject();
            w.WriteNumber("msgType", (ushort)type);
            w.WriteString("msgName", type.ToString());
            w.WriteNumber("offset", offset);
            w.WriteNumber("length", length);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // ---------------------------------------------------------------- quantisation table

    private static string Bits(double d) => "0x" + BitConverter.DoubleToInt64Bits(d).ToString("X16");

    private static void QuantCase(Utf8JsonWriter w, string op, double input, long? output, bool coarse = false)
    {
        w.WriteStartObject();
        w.WriteString("op", op);
        w.WriteString("inBits", Bits(input));
        if (double.IsFinite(input))
            w.WriteString("in", input.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        if (op == "velocity")
            w.WriteBoolean("coarse", coarse);
        if (output is { } o)
            w.WriteNumber("out", o);
        else
            w.WriteString("expect", "reject");
        w.WriteEndObject();
    }

    private static void WriteQuantize(Utf8JsonWriter w)
    {
        w.WriteStartObject("quantize");
        w.WriteString("rounding", "half away from zero, computed in IEEE double (C++: std::llround)");
        w.WriteString("position", "i32 = round(metres * 64)");
        w.WriteString("rotation", "i16 = (int16)(llround(radians * 32768/pi)), modulo-2^16 wrap; pi and -pi both give -32768");
        w.WriteString("velocity", "i16 = round(mps * 4), or round(mps / 4) when coarse");
        w.WriteString("fraction", "u8 = round(clamp(f, 0, 1) * 255)");
        w.WriteString("time", "i16 = round_half_away((sample_us - reference_us) / 1000)");
        w.WriteStartArray("cases");

        foreach (var (v, q) in new (double, long?)[]
                 {
                     (0, 0), (1, 64), (-1, -64), (0.0078125, 1), (-0.0078125, -1), (0.0078124, 0), (500_000, 32_000_000),
                     (33_554_431.984375, int.MaxValue), (-33_554_432.0, int.MinValue), (33_554_432.0, null), (double.NaN, null),
                     (double.PositiveInfinity, null), (double.NegativeInfinity, null),
                 })
            QuantCase(w, "position", v, q);

        foreach (var (v, q) in new (double, long?)[]
                 {
                     (0, 0), (Math.PI / 2, 16384), (-Math.PI / 2, -16384), (Math.PI, -32768), (-Math.PI, -32768),
                     (2 * Math.PI, 0), (-2 * Math.PI, 0), (3 * Math.PI / 2, -16384), (-3 * Math.PI / 2, 16384),
                     (Math.PI / 2 + 2 * Math.PI, 16384), (7 * Math.PI, -32768), (1e-9, 0), (Math.PI / 32768 * 0.5, 1),
                     (-Math.PI / 32768 * 0.5, -1), (double.NaN, null), (double.PositiveInfinity, null),
                 })
            QuantCase(w, "rotation", v, q);

        foreach (var (v, c, q) in new (double, bool, long?)[]
                 {
                     (0, false, 0), (1, false, 4), (-1, false, -4), (0.125, false, 1), (-0.125, false, -1), (8191.75, false, 32767),
                     (-8192, false, -32768), (8192, false, null), (4, true, 1), (2, true, 1), (-2, true, -1), (1.99, true, 0),
                     (131_068, true, 32767), (-131_072, true, -32768), (131_072, true, null), (double.NaN, false, null),
                     (double.NegativeInfinity, true, null),
                 })
            QuantCase(w, "velocity", v, q, c);

        foreach (var (v, q) in new (double, long?)[] { (0, 0), (1, 255), (0.5, 128), (2, 255), (-0.5, 0), (1.0 / 255, 1), (double.NaN, null), (double.PositiveInfinity, null) })
            QuantCase(w, "fraction", v, q);

        foreach (var (d, q) in new (long, long)[] { (0, 0), (499, 0), (500, 1), (-499, 0), (-500, -1), (1_500, 2), (-1_500, -2), (32_767_499, 32767), (-32_768_000, -32768) })
        {
            w.WriteStartObject();
            w.WriteString("op", "time");
            w.WriteNumber("referenceUs", 5_000_000_000);
            w.WriteNumber("sampleUs", 5_000_000_000 + d);
            w.WriteNumber("out", q);
            w.WriteEndObject();
        }
        foreach (long d in new long[] { 32_767_500, -32_768_501 })
        {
            w.WriteStartObject();
            w.WriteString("op", "time");
            w.WriteNumber("referenceUs", 5_000_000_000);
            w.WriteNumber("sampleUs", 5_000_000_000 + d);
            w.WriteString("expect", "reject");
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    // ---------------------------------------------------------------- output

    /// <summary>Everything the generator emits, keyed by file name (including index.json).</summary>
    public static SortedDictionary<string, byte[]> GenerateFiles()
    {
        var vectors = Build();
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var v in vectors)
            files.Add(v.File, v.Bytes);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, IndentSize = 2, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("note", "Generated by X4MP.Protocol.Tests GoldenVectors (set X4MP_UPDATE_GOLDEN=1 and run dotnet test to regenerate). Do not edit.");
            w.WriteNumber("schemaVersion", 1);
            w.WriteStartObject("protocol");
            w.WriteNumber("major", ProtocolConstants.ProtocolMajor);
            w.WriteNumber("minor", ProtocolConstants.ProtocolMinor);
            w.WriteEndObject();
            w.WriteNumber("maxFrameBytes", FrameCodec.DefaultMaxFrameBytes);
            w.WriteNumber("frameHeaderSize", FrameCodec.HeaderSize);
            w.WriteNumber("datagramHeaderSize", DatagramCodec.HeaderSize);
            w.WriteNumber("maxDatagramBytes", DatagramCodec.MaxDatagramBytes);
            w.WriteStartObject("kinds");
            w.WriteString("frame", "complete TCP frame (8-byte header + FlatBuffers payload); decode payload with the table for msgType; fields = canonical dump of every decoded field (see CanonicalFields.cs); re-encode must reproduce the frame semantically");
            w.WriteString("replication-entries", "raw Replication.entries bytes (protocol.md 10.2); decode all entries and re-encode byte-identically");
            w.WriteString("datagram", "complete UDP datagram (24-byte header + padded sub-messages)");
            w.WriteString("reject-frame", "bytes that must be rejected as a frame stream; expect = C# ViolationCode name");
            w.WriteString("reject-replication-entries", "entries bytes that must be rejected when decoded with entryCount");
            w.WriteString("reject-datagram", "bytes that must be rejected as a datagram header/sub-message list");
            w.WriteEndObject();

            w.WriteStartArray("vectors");
            foreach (var v in vectors)
            {
                w.WriteStartObject();
                w.WriteString("file", v.File);
                w.WriteString("kind", v.Kind);
                w.WriteNumber("size", v.Bytes.Length);
                w.WriteString("sha256", Convert.ToHexString(SHA256.HashData(v.Bytes)).ToLowerInvariant());
                v.Meta(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            WriteQuantize(w);
            w.WriteEndObject();
        }

        files.Add("index.json", [.. ms.ToArray(), (byte)'\n']);
        return files;
    }

    public static void WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var existing in Directory.EnumerateFiles(directory).Where(f => f.EndsWith(".bin", StringComparison.Ordinal) || f.EndsWith("index.json", StringComparison.Ordinal)))
            File.Delete(existing);
        foreach (var (name, bytes) in GenerateFiles())
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
    }
}
