using System.Text;
using Cysharp.Text;
using Omicron.Core.Content;
using Omicron.Core.Models;

namespace Omicron.Core.IO;

/// <summary>
/// Processes audio and video files. Parses container headers for metadata
/// (duration, codec, sample rate, bitrate). Returns base64 for capable models,
/// metadata-only for text-only models. No transcoding.
/// </summary>
public sealed class AudioProcessor : IContentProcessor
{
    public string Id => "audio";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.Audio };
    public OutputModality OutputModality => OutputModality.AudioBase64 | OutputModality.Metadata;

    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var meta = ParseAudioMetadata(context.RelativePath, context.Bytes.Span);
        var mimeType = GuessAudioMimeType(context.RelativePath);

        bool audioCapable = context.ModelMetadata?.SupportsModality("audio") == true;

        if (audioCapable)
        {
            var b64 = Convert.ToBase64String(context.Bytes.Span);
            using var output = ZString.CreateUtf8StringBuilder();
            output.AppendFormat("[FILE] {0}  ({1}, {2})", context.RelativePath, FormatSize.Format(context.Bytes.Length), mimeType);
            output.AppendLine();
            if (meta is not null)
            {
                output.AppendFormat("Format: {0}, Duration: {1:F1}s, Bitrate: {2} kbps, Sample Rate: {3} Hz", meta.Format, meta.Duration, meta.Bitrate, meta.SampleRate);
                output.AppendLine();
            }
            output.AppendLiteral("Data: "u8);
            output.Append(b64);

            return ValueTask.FromResult(new ContentProcessorResult(
                output.ToString(), OutputModality.AudioBase64, MimeType: mimeType,
                Utf8Data: output.AsSpan().ToArray()));
        }

        // Text-only model: metadata only
        using var audioMeta = ZString.CreateUtf8StringBuilder();
        audioMeta.AppendFormat("[FILE] {0}  ({1}, {2})", context.RelativePath, FormatSize.Format(context.Bytes.Length), mimeType);
        audioMeta.AppendLine();
        if (meta is not null)
        {
            audioMeta.AppendFormat("Format: {0}", meta.Format);
            audioMeta.AppendLine();
            if (meta.Duration > 0)
            {
                audioMeta.AppendFormat("Duration: {0:F1}s", meta.Duration);
                audioMeta.AppendLine();
            }
            if (meta.Bitrate > 0)
            {
                audioMeta.AppendFormat("Bitrate: {0} kbps", meta.Bitrate);
                audioMeta.AppendLine();
            }
            if (meta.SampleRate > 0)
            {
                audioMeta.AppendFormat("Sample Rate: {0} Hz", meta.SampleRate);
                audioMeta.AppendLine();
            }
        }
        else
        {
            audioMeta.AppendLiteral("(metadata extraction unavailable)"u8);
            audioMeta.AppendLine();
        }

        return ValueTask.FromResult(new ContentProcessorResult(
            audioMeta.ToString().TrimEnd(), OutputModality.Metadata,
            Utf8Data: audioMeta.AsSpan().ToArray()));
    }

    internal static AudioMeta? ParseAudioMetadata(string path, ReadOnlySpan<byte> bytes)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";

        return ext switch
        {
            ".mp3" => ParseMp3Header(bytes),
            ".wav" => ParseWavHeader(bytes),
            ".flac" => ParseFlacHeader(bytes),
            ".ogg" or ".opus" => ParseOggHeader(bytes),
            ".aac" or ".m4a" => ParseMp4AudioHeader(bytes),
            _ => null
        };
    }

    private static AudioMeta? ParseMp3Header(ReadOnlySpan<byte> bytes)
    {
        // MP3: ID3v2 header or sync word (0xFF 0xFB / 0xFF 0xF3 / 0xFF 0xF2)
        if (bytes.Length < 4) return null;

        // Find first MP3 frame sync
        for (int i = 0; i < Math.Min(bytes.Length, 4096); i++)
        {
            if (i + 2 < bytes.Length && bytes[i] == 0xFF && (bytes[i + 1] & 0xE0) == 0xE0)
            {
                var hdr = (bytes[i + 1] << 8) | bytes[i + 2];

                int bitrateIndex = (hdr >> 4) & 0x0F;
                int sampleRateIndex = (hdr >> 2) & 0x03;

                var bitrates = new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
                var sampleRates = new[] { 44100, 48000, 32000, 0 };

                int bitrate = bitrateIndex < bitrates.Length ? bitrates[bitrateIndex] : 0;
                int sampleRate = sampleRateIndex < sampleRates.Length ? sampleRates[sampleRateIndex] : 0;

                // Estimate duration from file size
                if (bitrate > 0)
                {
                    double duration = (bytes.Length * 8.0) / (bitrate * 1000.0);
                    return new AudioMeta("MP3", duration, bitrate, sampleRate);
                }
                break;
            }
        }

        // Check for ID3v2 header for duration (rare without full scan)
        // Return a basic metadata with file size info
        return new AudioMeta("MP3", 0, 0, 0);
    }

    private static AudioMeta? ParseWavHeader(ReadOnlySpan<byte> bytes)
    {
        // RIFF/WAVE: 12-byte RIFF header + fmt chunk
        if (bytes.Length < 44) return null;
        if (bytes[0] != 0x52 || bytes[1] != 0x49) return null; // "RI"

        int sampleRate = bytes[24] | (bytes[25] << 8) | (bytes[26] << 16) | (bytes[27] << 24);
        int byteRate = bytes[28] | (bytes[29] << 8) | (bytes[30] << 16) | (bytes[31] << 24);
        int bitsPerSample = bytes[34] | (bytes[35] << 8);
        int dataSize = 0;

        // Find "data" chunk
        for (int i = 36; i < bytes.Length - 8; i++)
        {
            if (bytes[i] == 0x64 && bytes[i + 1] == 0x61 && bytes[i + 2] == 0x74 && bytes[i + 3] == 0x61)
            {
                dataSize = bytes[i + 4] | (bytes[i + 5] << 8) | (bytes[i + 6] << 16) | (bytes[i + 7] << 24);
                break;
            }
        }

        double duration = byteRate > 0 ? dataSize / (double)byteRate : 0;
        int bitrate = byteRate * 8 / 1000;

        return new AudioMeta("WAV", duration, bitrate, sampleRate);
    }

    private static AudioMeta? ParseFlacHeader(ReadOnlySpan<byte> bytes)
    {
        // FLAC: starts with "fLaC", then METADATA_BLOCK_STREAMINFO
        if (bytes.Length < 42) return null;
        if (bytes[0] != 0x66 || bytes[1] != 0x4C) return null; // "fL"

        // Streaminfo block: after 4-byte "fLaC" + 1-byte block header + 3-byte length
        // Sample rate: bits 100-119 (20 bits)
        // Total samples: bits 120-159 (40 bits or 36 bits)
        if (bytes.Length < 42) return null;

        int sampleRate = ((bytes[27] & 0x0F) << 16) | (bytes[28] << 8) | bytes[29];
        long totalSamples = (long)(bytes[30] & 0x0F) << 32
                          | (long)bytes[31] << 24
                          | (long)bytes[32] << 16
                          | (long)bytes[33] << 8
                          | (long)bytes[34];
        int bitsPerSample = ((bytes[27] & 0xF0) >> 4) + 1;

        double duration = sampleRate > 0 ? totalSamples / (double)sampleRate : 0;
        int bitrate = duration > 0 ? (int)(bytes.Length * 8.0 / duration / 1000.0) : 0;

        return new AudioMeta("FLAC", duration, bitrate, sampleRate);
    }

    private static AudioMeta? ParseOggHeader(ReadOnlySpan<byte> bytes)
    {
        // OGG: starts with "OggS"
        if (bytes.Length < 28) return null;
        if (bytes[0] != 0x4F || bytes[1] != 0x67) return null; // "Og"

        // Vorbis identification header at page 0, segment 0
        // Sample rate at offset 28-31 of the Vorbis header
        // The Vorbis header starts after the Ogg page header (27 bytes) + segment table
        if (bytes.Length < 60) return null;

        // Find "vorbis" magic at offset 29
        if (bytes[29] == 0x76 && bytes[30] == 0x6F) // "vo"
        {
            int sampleRate = bytes[40] | (bytes[41] << 8) | (bytes[42] << 16) | (bytes[43] << 24);
            int bitrateNominal = bytes[48] | (bytes[49] << 8) | (bytes[50] << 16) | (bytes[51] << 24);

            // Estimate duration
            double duration = 0;
            if (bitrateNominal > 0)
                duration = (bytes.Length * 8.0) / (bitrateNominal * 1000.0);

            return new AudioMeta("OGG", duration, bitrateNominal / 1000, sampleRate);
        }

        return null;
    }

    private static AudioMeta? ParseMp4AudioHeader(ReadOnlySpan<byte> bytes)
    {
        // MP4/M4A: ftyp box + moov box
        // Check for ftyp at offset 4
        if (bytes.Length < 32) return null;
        if (bytes[4] != 0x66 || bytes[5] != 0x74) return null; // "ft"

        // Look for mvhd box (media header) for duration
        for (int i = 0; i < Math.Min(bytes.Length, 4096) - 16; i++)
        {
            if (bytes[i + 4] == 0x6D && bytes[i + 5] == 0x76 && bytes[i + 6] == 0x68 && bytes[i + 7] == 0x64)
            {
                // mvhd header: version(1) + flags(3) + timescale(4) + duration(4)
                if (i + 20 < bytes.Length)
                {
                    int timescale = (bytes[i + 12] << 24) | (bytes[i + 13] << 16) | (bytes[i + 14] << 8) | bytes[i + 15];
                    int duration = (bytes[i + 16] << 24) | (bytes[i + 17] << 16) | (bytes[i + 18] << 8) | bytes[i + 19];
                    if (timescale > 0)
                    {
                        double durSec = duration / (double)timescale;
                        // Also look for sample rate in stsd box (simplified)
                        int sampleRate = 44100; // Typical default
                        return new AudioMeta("AAC", durSec, 0, sampleRate);
                    }
                }
                break;
            }
        }

        return null;
    }

    private static string GuessAudioMimeType(string path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
        return ext switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".aac" => "audio/aac",
            ".m4a" => "audio/mp4",
            ".wma" => "audio/x-ms-wma",
            _ => "application/octet-stream"
        };
    }

    internal sealed record AudioMeta(string Format, double Duration, int Bitrate, int SampleRate);
}

/// <summary>
/// Processes video files. Similar to AudioProcessor but for video containers.
/// Parses headers for metadata (duration, codec, dimensions, bitrate).
/// </summary>
public sealed class VideoProcessor : IContentProcessor
{
    public string Id => "video";
    public IReadOnlySet<DetectedFileType> SupportedTypes { get; }
        = new HashSet<DetectedFileType> { DetectedFileType.Video };
    public OutputModality OutputModality => OutputModality.VideoBase64 | OutputModality.Metadata;

    public ValueTask<ContentProcessorResult> ProcessAsync(
        ContentProcessorContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var meta = ParseVideoMetadata(context.RelativePath, context.Bytes.Span);
        var mimeType = GuessVideoMimeType(context.RelativePath);

        bool videoCapable = context.ModelMetadata?.SupportsModality("video") == true;

        if (videoCapable)
        {
            var b64 = Convert.ToBase64String(context.Bytes.Span);
            using var output = ZString.CreateUtf8StringBuilder();
            output.AppendFormat("[FILE] {0}  ({1}, {2})", context.RelativePath, FormatSize.Format(context.Bytes.Length), mimeType);
            output.AppendLine();
            if (meta is not null)
            {
                output.AppendFormat("Format: {0}, Duration: {1:F1}s", meta.Format, meta.Duration);
                output.AppendLine();
                if (meta.Width > 0)
                {
                    output.AppendFormat("Resolution: {0}×{1}", meta.Width, meta.Height);
                    output.AppendLine();
                }
                if (meta.Bitrate > 0)
                {
                    output.AppendFormat("Bitrate: {0} kbps", meta.Bitrate);
                    output.AppendLine();
                }
            }
            output.AppendLiteral("Data: "u8);
            output.Append(b64);

            return ValueTask.FromResult(new ContentProcessorResult(
                output.ToString(), OutputModality.VideoBase64, MimeType: mimeType,
                Utf8Data: output.AsSpan().ToArray()));
        }

        // Text-only model: metadata only
        using var videoMeta = ZString.CreateUtf8StringBuilder();
        videoMeta.AppendFormat("[FILE] {0}  ({1}, {2})", context.RelativePath, FormatSize.Format(context.Bytes.Length), mimeType);
        videoMeta.AppendLine();
        if (meta is not null)
        {
            videoMeta.AppendFormat("Format: {0}", meta.Format);
            videoMeta.AppendLine();
            if (meta.Duration > 0)
            {
                videoMeta.AppendFormat("Duration: {0:F1}s", meta.Duration);
                videoMeta.AppendLine();
            }
            if (meta.Width > 0)
            {
                videoMeta.AppendFormat("Resolution: {0}×{1}", meta.Width, meta.Height);
                videoMeta.AppendLine();
            }
            if (meta.Bitrate > 0)
            {
                videoMeta.AppendFormat("Bitrate: {0} kbps", meta.Bitrate);
                videoMeta.AppendLine();
            }
        }
        else
        {
            videoMeta.AppendLiteral("(metadata extraction unavailable)"u8);
            videoMeta.AppendLine();
        }

        return ValueTask.FromResult(new ContentProcessorResult(
            videoMeta.ToString().TrimEnd(), OutputModality.Metadata,
            Utf8Data: videoMeta.AsSpan().ToArray()));
    }

    internal static VideoMeta? ParseVideoMetadata(string path, ReadOnlySpan<byte> bytes)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";

        return ext switch
        {
            ".mp4" or ".mov" or ".m4v" => ParseMp4Header(bytes),
            ".avi" => ParseAviHeader(bytes),
            ".webm" or ".mkv" => ParseWebmHeader(bytes),
            ".wmv" => null, // Complex ASF parsing, skip for MVP
            ".flv" => ParseFlvHeader(bytes),
            _ => null
        };
    }

    private static VideoMeta? ParseMp4Header(ReadOnlySpan<byte> bytes)
    {
        // Look for mvhd box
        if (bytes.Length < 32) return null;
        if (bytes[4] != 0x66 || bytes[5] != 0x74) return null; // "ft"

        for (int i = 0; i < Math.Min(bytes.Length, 4096) - 24; i++)
        {
            if (bytes[i + 4] == 0x6D && bytes[i + 5] == 0x76 && bytes[i + 6] == 0x68 && bytes[i + 7] == 0x64)
            {
                if (i + 20 < bytes.Length)
                {
                    int timescale = (bytes[i + 12] << 24) | (bytes[i + 13] << 16) | (bytes[i + 14] << 8) | bytes[i + 15];
                    int duration = (bytes[i + 16] << 24) | (bytes[i + 17] << 16) | (bytes[i + 18] << 8) | bytes[i + 19];
                    if (timescale > 0)
                    {
                        double durSec = duration / (double)timescale;
                        return new VideoMeta("MP4", durSec, 0, 0, 0);
                    }
                }
                break;
            }
        }

        return null;
    }

    private static VideoMeta? ParseAviHeader(ReadOnlySpan<byte> bytes)
    {
        // AVI: RIFF + AVI header
        if (bytes.Length < 64) return null;
        if (bytes[0] != 0x52 || bytes[1] != 0x49) return null; // "RI"

        // avih chunk: at offset 36 (simplified)
        if (bytes[36] == 0x61 && bytes[37] == 0x76 && bytes[38] == 0x69 && bytes[39] == 0x68)
        {
            int microsecPerFrame = bytes[44] | (bytes[45] << 8) | (bytes[46] << 16) | (bytes[47] << 24);
            int totalFrames = bytes[56] | (bytes[57] << 8) | (bytes[58] << 16) | (bytes[59] << 24);
            int width = bytes[68] | (bytes[69] << 8);
            int height = bytes[70] | (bytes[71] << 8);

            double duration = microsecPerFrame > 0 ? totalFrames * microsecPerFrame / 1_000_000.0 : 0;
            return new VideoMeta("AVI", duration, width, height, 0);
        }

        return null;
    }

    private static VideoMeta? ParseWebmHeader(ReadOnlySpan<byte> bytes)
    {
        // WebM/MKV: EBML header starts with 0x1A 0x45 0xDF 0xA3
        if (bytes.Length < 64) return null;
        if (bytes[0] != 0x1A || bytes[1] != 0x45) return null; // EBML

        // Look for Segment > Info > Duration (simplified)
        // Use a basic scan for the Duration element (0x44 0x89)
        for (int i = 0; i < Math.Min(bytes.Length, 4096) - 8; i++)
        {
            if (bytes[i] == 0x44 && bytes[i + 1] == 0x89) // Duration
            {
                // Next bytes: length + float (4 or 8 bytes)
                // Simplified: skip to value
                double duration = 0;
                if (i + 6 < bytes.Length)
                {
                    // Float typically starts at i+2 or i+3 depending on encoding
                    uint rawDuration = (uint)(bytes[i + 2] << 24) | (uint)(bytes[i + 3] << 16)
                                     | (uint)(bytes[i + 4] << 8) | bytes[i + 5];
                    duration = rawDuration / 1000.0; // milliseconds to seconds
                }
                return new VideoMeta("WebM", duration, 0, 0, 0);
            }
        }

        return null;
    }

    private static VideoMeta? ParseFlvHeader(ReadOnlySpan<byte> bytes)
    {
        // FLV: starts with "FLV\x01"
        if (bytes.Length < 9) return null;
        if (bytes[0] != 0x46 || bytes[1] != 0x4C || bytes[2] != 0x56) return null; // "FLV"

        // Minimum metadata extraction — duration typically in onMetaData tag
        // Return basic info
        return new VideoMeta("FLV", 0, 0, 0, 0);
    }

    private static string GuessVideoMimeType(string path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".wmv" => "video/x-ms-wmv",
            ".flv" => "video/x-flv",
            _ => "application/octet-stream"
        };
    }

    internal sealed record VideoMeta(string Format, double Duration, int Width, int Height, int Bitrate);
}
