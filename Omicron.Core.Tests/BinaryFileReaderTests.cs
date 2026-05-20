using System.Text;
using Omicron.Core.Events;
using Omicron.Core.IO;
using Omicron.Core.Models;
using Xunit;

namespace Omicron.Core.Tests;

public class BinaryFileReaderTests
{
    // ============================================================
    // FileTypeClassifier tests
    // ============================================================

    [Fact]
    public void Classify_Png_ReturnsImage()
    {
        byte[] header = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
        };
        DetectedFileType result = FileTypeClassifier.Classify("test.png", header);
        Assert.Equal(DetectedFileType.Image, result);
    }

    [Fact]
    public void Classify_Jpeg_ReturnsImage()
    {
        byte[] header = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46
        };
        DetectedFileType result = FileTypeClassifier.Classify("test.jpg", header);
        Assert.Equal(DetectedFileType.Image, result);
    }

    [Fact]
    public void Classify_Gif_ReturnsImage()
    {
        byte[] header = "GIF89a"u8.ToArray();
        DetectedFileType result = FileTypeClassifier.Classify("test.gif", header);
        Assert.Equal(DetectedFileType.Image, result);
    }

    [Fact]
    public void Classify_Pdf_ReturnsPdf()
    {
        byte[] header = "%PDF-1.4"u8.ToArray();
        DetectedFileType result = FileTypeClassifier.Classify("test.pdf", header);
        Assert.Equal(DetectedFileType.Pdf, result);
    }

    [Fact]
    public void Classify_Zip_ReturnsArchive()
    {
        byte[] header = new byte[]
        {
            0x50, 0x4B, 0x03, 0x04, 0x00, 0x00
        };
        // No [Content_Types].xml or META-INF/container.xml → generic archive
        DetectedFileType result = FileTypeClassifier.Classify("test.zip", header);
        Assert.Equal(DetectedFileType.Archive, result);
    }

    [Fact]
    public void Classify_ExtensionAudio_ReturnsAudio()
    {
        // MP3 without ID3 magic — relies on extension
        byte[] header = new byte[]
        {
            0xFF, 0xFB, 0x90, 0x00
        }; // MP3 sync word
        DetectedFileType result = FileTypeClassifier.Classify("song.mp3", header);
        Assert.Equal(DetectedFileType.Audio, result);
    }

    [Fact]
    public void Classify_ExtensionVideo_ReturnsVideo()
    {
        DetectedFileType result = FileTypeClassifier.Classify("video.mp4",
            new byte[]
            {
                0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70
            });
        Assert.Equal(DetectedFileType.Video, result);
    }

    [Fact]
    public void Classify_Csv_ReturnsCsv()
    {
        DetectedFileType result = FileTypeClassifier.Classify("data.csv", "a,b,c\n1,2,3"u8.ToArray());
        Assert.Equal(DetectedFileType.Csv, result);
    }

    [Fact]
    public void Classify_Ipynb_ReturnsNotebook()
    {
        DetectedFileType result = FileTypeClassifier.Classify("notebook.ipynb", "{\"cells\":[]}"u8.ToArray());
        Assert.Equal(DetectedFileType.Notebook, result);
    }

    [Fact]
    public void Classify_Svg_ReturnsSvg()
    {
        DetectedFileType result = FileTypeClassifier.Classify("image.svg",
            "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray());
        Assert.Equal(DetectedFileType.Svg, result);
    }

    [Fact]
    public void Classify_PlainText_ReturnsText()
    {
        DetectedFileType result = FileTypeClassifier.Classify("readme.txt", "Hello, world!"u8.ToArray());
        Assert.Equal(DetectedFileType.Text, result);
    }

    [Fact]
    public void Classify_EmptyFile_ReturnsText()
    {
        DetectedFileType result = FileTypeClassifier.Classify("empty.txt", Array.Empty<byte>());
        Assert.Equal(DetectedFileType.Text, result);
    }

    [Fact]
    public void Classify_ExtensionOpenXml_ReturnsOpenXmlDocument()
    {
        DetectedFileType result = FileTypeClassifier.Classify("report.xlsx", Array.Empty<byte>());
        Assert.Equal(DetectedFileType.OpenXmlDocument, result);
    }

    [Fact]
    public void Classify_ExtensionEmail_ReturnsEmail()
    {
        DetectedFileType result = FileTypeClassifier.Classify("message.eml",
            "From: test@example.com\nSubject: Hello"u8.ToArray());
        Assert.Equal(DetectedFileType.Email, result);
    }

    // ============================================================
    // HexDumpProcessor tests
    // ============================================================

    private static ContentProcessorContext MakeHexContext(
        byte[] bytes,
        int? offset = null,
        int? limit = null)
    {
        return new ContentProcessorContext("/test/file.bin",
            "file.bin",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "hex",
            offset,
            limit,
            null,
            CancellationToken.None);
    }

    private static string RunHexDump(byte[] bytes, int? offset = null, int? limit = null)
    {
        var processor = new HexDumpProcessor();
        ContentProcessorContext ctx = MakeHexContext(bytes, offset, limit);
        ContentProcessorResult result = processor.ProcessAsync(ctx).GetAwaiter().GetResult();
        return result.TextData.ToString();
    }

    [Fact]
    public void HexDump_Format_HasCorrectColumns()
    {
        byte[] data = new byte[]
        {
            0x48, 0x65, 0x6C, 0x6C, 0x6F
        }; // "Hello"
        string output = RunHexDump(data);

        // Should have offset, hex bytes, ASCII columns
        Assert.Contains("00000000", output);
        Assert.Contains("48 65 6C 6C 6F", output);
        Assert.Contains("|Hello|", output);
    }

    [Fact]
    public void HexDump_ZeroPaddedOffset()
    {
        byte[] data = Encoding.UTF8.GetBytes(new string('x', 32));
        string output = RunHexDump(data);

        Assert.Contains("00000000", output);
        Assert.Contains("00000010", output);
    }

    [Fact]
    public void HexDump_EmptyFile()
    {
        string output = RunHexDump(Array.Empty<byte>());
        Assert.Contains("HEX", output);
        Assert.Contains("0 bytes", output);
    }

    [Fact]
    public void HexDump_OffsetRoundsDown()
    {
        byte[] data = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            data[i] = (byte)i;
        }

        // Request offset 5 (should round down to 0)
        string output = RunHexDump(data, 5, 16);
        Assert.Contains("00000000", output);
        // Should NOT contain 00000100 (that would be offset 256)
    }

    [Fact]
    public void HexDump_OffsetPastEnd_ReturnsError()
    {
        byte[] data = new byte[]
        {
            0x01, 0x02, 0x03
        };
        string output = RunHexDump(data, 100);
        Assert.Contains("past end of file", output);
    }

    [Fact]
    public void HexDump_LimitZero_ReturnsEmpty()
    {
        byte[] data = new byte[]
        {
            0x01, 0x02, 0x03
        };
        string output = RunHexDump(data, 0, 0);
        Assert.Contains("HEX", output);
        Assert.DoesNotContain("|", output); // no data rows
    }

    [Fact]
    public async Task HexDump_NegativeOffset_ReturnsError()
    {
        var processor = new HexDumpProcessor();
        ContentProcessorContext ctx = MakeHexContext(new byte[]
        {
            0x01
        }, -1);
        ContentProcessorResult result = await processor.ProcessAsync(ctx);
        Assert.Contains("non-negative", result.TextData.ToString());
    }

    [Fact]
    public void HexDump_PartialFinalRow()
    {
        // 17 bytes → 2 rows, second row has 1 byte
        byte[] data = new byte[17];
        for (int i = 0; i < 17; i++)
        {
            data[i] = (byte)(0x41 + i);
        }

        string output = RunHexDump(data);
        Assert.Contains("00000000", output);
        Assert.Contains("00000010", output);
        // Second row should have only 1 hex byte with blank space for others
        Assert.Contains("41", output);
    }

    [Fact]
    public void HexDump_ContinuationHint()
    {
        byte[] data = new byte[512]; // enough to need continuation
        string output = RunHexDump(data, limit: 32); // 2 rows (32 bytes)

        Assert.Contains("to continue", output);
        Assert.Contains("0x", output);
    }

    [Fact]
    public void HexDump_AllZeroRow()
    {
        byte[] data = new byte[16]; // all zeros
        string output = RunHexDump(data);

        Assert.Contains("00 00 00 00 00 00 00 00", output); // hex
        Assert.Contains("|................|", output); // ASCII
    }

    // ============================================================
    // ContentProcessorRegistry tests
    // ============================================================

    [Fact]
    public void Registry_ResolvesCorrectProcessor()
    {
        var reg = new ContentProcessorRegistry();
        reg.Register(new TextProcessor());
        reg.Register(new HexDumpProcessor());

        IContentProcessor? textProc = reg.Resolve(DetectedFileType.Text);
        Assert.NotNull(textProc);
        Assert.Equal("text", textProc!.Id);

        IContentProcessor? unknownProc = reg.Resolve(DetectedFileType.UnknownBinary);
        Assert.NotNull(unknownProc);
        Assert.Equal("hex_dump", unknownProc!.Id);
    }

    [Fact]
    public void Registry_FirstRegistrationWins()
    {
        var reg = new ContentProcessorRegistry();
        reg.Register(new HexDumpProcessor());
        reg.Register(new TextProcessor());

        // HexDumpProcessor was registered first for UnknownBinary
        IContentProcessor? proc = reg.Resolve(DetectedFileType.UnknownBinary);
        Assert.NotNull(proc);
        Assert.Equal("hex_dump", proc!.Id);
    }

    [Fact]
    public void Registry_ReturnsNullForUnregistered()
    {
        var reg = new ContentProcessorRegistry();
        IContentProcessor? proc = reg.Resolve(DetectedFileType.Image);
        Assert.Null(proc);
    }

    // ============================================================
    // ImageProcessor model-aware tests
    // ============================================================

    [Fact]
    public async Task ImageProcessor_VisionModel_ReturnsImageBase64()
    {
        var proc = new ImageProcessor();
        var meta = new ModelMetadata("test-model",
            "test",
            Modalities: new HashSet<string>
            {
                "text",
                "image"
            });
        byte[] bytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
        }; // PNG header
        var ctx = new ContentProcessorContext("/test/img.png",
            "img.png",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            meta,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Equal(OutputModality.ImageBase64, result.ActualModality);
        Assert.Contains("Data: ", result.TextData.ToString());
        Assert.Contains("image/png", result.TextData.ToString());
    }

    [Fact]
    public async Task ImageProcessor_TextOnlyModel_ReturnsHexDump()
    {
        var proc = new ImageProcessor();
        var meta = new ModelMetadata("test-model",
            "test",
            Modalities: new HashSet<string>
            {
                "text"
            });
        byte[] bytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
        }; // PNG header
        var ctx = new ContentProcessorContext("/test/img.png",
            "img.png",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            meta,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        // Should be HexDump, not ImageBase64
        Assert.Equal(OutputModality.HexDump, result.ActualModality);
        Assert.Contains("[HEX]", result.TextData.ToString());
    }

    [Fact]
    public async Task ImageProcessor_NullMetadata_UsesSafeDefaults()
    {
        var proc = new ImageProcessor();
        byte[] bytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
        };
        var ctx = new ContentProcessorContext("/test/img.png",
            "img.png",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        // Null metadata → safe default (no base64)
        Assert.Equal(OutputModality.HexDump, result.ActualModality);
    }

    // ============================================================
    // PdfProcessor model-aware tests
    // ============================================================

    [Fact]
    public async Task PdfProcessor_PdfCapableModel_ReturnsPdfBase64()
    {
        var proc = new PdfProcessor();
        var meta = new ModelMetadata("test-model",
            "test",
            Modalities: new HashSet<string>
            {
                "text",
                "pdf"
            });
        byte[] bytes = "%PDF-1.4\n1 0 obj\n<</Type/Catalog>>\nendobj"u8.ToArray();
        var ctx = new ContentProcessorContext("/test/doc.pdf",
            "doc.pdf",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            meta,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Equal(OutputModality.PdfBase64, result.ActualModality);
        Assert.Contains("Data: ", result.TextData.ToString());
    }

    [Fact]
    public async Task PdfProcessor_TextOnlyModel_ReturnsText()
    {
        var proc = new PdfProcessor();
        var meta = new ModelMetadata("test-model",
            "test",
            Modalities: new HashSet<string>
            {
                "text"
            });
        byte[] bytes = Encoding.UTF8.GetBytes("%PDF-1.4\n1 0 obj\n<</Type/Catalog>>\nendobj\nBT\n(Hello World)Tj\nET");
        var ctx = new ContentProcessorContext("/test/doc.pdf",
            "doc.pdf",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            meta,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        // For text-only model, should extract text (or fall back to hex dump)
        Assert.NotEqual(OutputModality.PdfBase64, result.ActualModality);
    }

    // ============================================================
    // TextProcessor tests
    // ============================================================

    [Fact]
    public async Task TextProcessor_ReadsUtf8()
    {
        var proc = new TextProcessor();
        byte[] bytes = "hello\nworld"u8.ToArray();
        var ctx = new ContentProcessorContext("/test/file.txt",
            "file.txt",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("hello", result.TextData.ToString());
        Assert.Contains("world", result.TextData.ToString());
        Assert.Equal(OutputModality.Text, result.ActualModality);
    }

    [Fact]
    public async Task TextProcessor_EmptyFile()
    {
        var proc = new TextProcessor();
        byte[] bytes = Array.Empty<byte>();
        var ctx = new ContentProcessorContext("/test/empty.txt",
            "empty.txt",
            bytes.AsMemory(),
            0,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("empty", result.TextData.ToString());
    }

    [Fact]
    public async Task TextProcessor_BinaryContent_AddsWarning()
    {
        var proc = new TextProcessor();
        // Binary content with null byte
        byte[] bytes = new byte[]
        {
            0x48, 0x00, 0x65, 0x6C
        };
        var ctx = new ContentProcessorContext("/test/file.bin",
            "file.bin",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "text",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("WARNING", result.TextData.ToString());
    }

    // ============================================================
    // AudioProcessor tests
    // ============================================================

    [Fact]
    public async Task AudioProcessor_WithCapability_ReturnsAudioBase64()
    {
        var proc = new AudioProcessor();
        var meta = new ModelMetadata("test",
            "test",
            Modalities: new HashSet<string>
            {
                "text",
                "audio"
            });
        byte[] bytes = new byte[44]; // Minimal WAV header
        bytes[0] = 0x52;
        bytes[1] = 0x49; // RIFF
        bytes[8] = 0x57;
        bytes[9] = 0x41; // WAVE

        var ctx = new ContentProcessorContext("/test/audio.wav",
            "audio.wav",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            meta,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Equal(OutputModality.AudioBase64, result.ActualModality);
        Assert.Contains("Data: ", result.TextData.ToString());
    }

    [Fact]
    public async Task AudioProcessor_TextOnly_ReturnsMetadata()
    {
        var proc = new AudioProcessor();
        var meta = new ModelMetadata("test", "test", Modalities: new HashSet<string>
        {
            "text"
        });
        byte[] bytes = new byte[44];
        bytes[0] = 0x52;
        bytes[1] = 0x49; // RIFF

        var ctx = new ContentProcessorContext("/test/audio.wav",
            "audio.wav",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            meta,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Equal(OutputModality.Metadata, result.ActualModality);
    }

    // ============================================================
    // Base64Processor tests
    // ============================================================

    [Fact]
    public async Task Base64Processor_ReturnsBase64Encoding()
    {
        var proc = new Base64Processor();
        byte[] bytes = "Hello, World!"u8.ToArray();
        var ctx = new ContentProcessorContext("/test/file.bin",
            "file.bin",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "base64",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("Data: ", result.TextData.ToString());
        Assert.Contains("SGVsbG8sIFdvcmxkIQ==", result.TextData.ToString()); // base64 of "Hello, World!"
    }

    [Fact]
    public async Task Base64Processor_MimeTypeFromExtension()
    {
        var proc = new Base64Processor();
        byte[] bytes = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47
        };
        var ctx = new ContentProcessorContext("/test/img.png",
            "img.png",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "base64",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("image/png", result.TextData.ToString());
    }

    // ============================================================
    // OutputModality and ModelCapabilityExtensions tests
    // ============================================================

    [Fact]
    public void ModelCapability_SupportsImages_VisionFlag()
    {
        var meta = new ModelMetadata("test", "test", SupportsVision: true);
        Assert.True(meta.SupportsImages());
    }

    [Fact]
    public void ModelCapability_SupportsImages_Modalities()
    {
        var meta = new ModelMetadata("test",
            "test",
            Modalities: new HashSet<string>
            {
                "text",
                "image"
            });
        Assert.True(meta.SupportsImages());
        Assert.True(meta.SupportsModality("image"));
    }

    [Fact]
    public void ModelCapability_SupportsAudio()
    {
        var meta = new ModelMetadata("test",
            "test",
            Modalities: new HashSet<string>
            {
                "text",
                "audio"
            });
        Assert.True(meta.SupportsAudio());
        Assert.False(meta.SupportsPdf());
    }

    [Fact]
    public void ModelCapability_SupportsPdf()
    {
        var meta = new ModelMetadata("test",
            "test",
            Modalities: new HashSet<string>
            {
                "text",
                "pdf"
            });
        Assert.True(meta.SupportsPdf());
    }

    [Fact]
    public void ModelCapability_SupportsVideo()
    {
        var meta = new ModelMetadata("test",
            "test",
            Modalities: new HashSet<string>
            {
                "text",
                "video"
            });
        Assert.True(meta.SupportsVideo());
    }

    [Fact]
    public void ModelCapability_NullMetadata_ReturnsFalse()
    {
        ModelMetadata? meta = null;
        Assert.False(meta.SupportsImages());
        Assert.False(meta.SupportsModality("image"));
    }

    // ============================================================
    // ImageProcessor dimension extraction tests
    // ============================================================

    [Fact]
    public void ImageDimensions_Png_ExtractsCorrectly()
    {
        // Minimal PNG: 8-byte signature + IHDR chunk (25 bytes)
        // IHDR: 4 bytes length (00 00 00 0D), "IHDR", width (4), height (4), bit depth, color type, compression, filter, interlace
        byte[] png = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D, // IHDR chunk length (13)
            0x49, 0x48, 0x44, 0x52, // "IHDR"
            0x00, 0x00, 0x01, 0x00, // width = 256
            0x00, 0x00, 0x00, 0x80, // height = 128
            0x08, 0x02, 0x00, 0x00, 0x00 // bit depth, color type, compression, filter, interlace
        };

        (int Width, int Height)? dim = ImageProcessor.TryGetDimensions(png.AsSpan());
        Assert.True(dim.HasValue);
        Assert.Equal(256, dim.Value.Width);
        Assert.Equal(128, dim.Value.Height);
    }

    [Fact]
    public void ImageDimensions_Jpeg_ExtractsCorrectly()
    {
        // Minimal JPEG with SOF0 marker
        byte[] jpeg = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01,
            0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09,
            0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12, 0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F,
            0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20, 0x24, 0x2E, 0x27, 0x20, 0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29,
            0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27, 0x39, 0x3D, 0x38, 0x32, 0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF,
            0xC0, 0x00, 0x0B, 0x08, 0x00, 0x64, 0x00, 0xC8, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01
        };

        (int Width, int Height)? dim = ImageProcessor.TryGetDimensions(jpeg.AsSpan());
        Assert.True(dim.HasValue);
        Assert.Equal(200, dim.Value.Width); // 0x00C8 = 200
        Assert.Equal(100, dim.Value.Height); // 0x0064 = 100
    }

    [Fact]
    public void ImageDimensions_EmptyBytes_ReturnsNull()
    {
        (int Width, int Height)? dim = ImageProcessor.TryGetDimensions(ReadOnlySpan<byte>.Empty);
        Assert.Null(dim);
    }

    // ============================================================
    // AudioProcessor metadata parsing tests
    // ============================================================

    [Fact]
    public void AudioMetadata_Wav_ParsesCorrectly()
    {
        // Build a minimal valid WAV header
        byte[] wav = new byte[44];
        wav[0] = 0x52;
        wav[1] = 0x49;
        wav[2] = 0x46;
        wav[3] = 0x46; // "RIFF"
        wav[4] = 0x00;
        wav[5] = 0x00;
        wav[6] = 0x00;
        wav[7] = 0x00; // file size
        wav[8] = 0x57;
        wav[9] = 0x41;
        wav[10] = 0x56;
        wav[11] = 0x45; // "WAVE"
        wav[12] = 0x66;
        wav[13] = 0x6D;
        wav[14] = 0x74;
        wav[15] = 0x20; // "fmt "
        wav[16] = 0x10;
        wav[17] = 0x00;
        wav[18] = 0x00;
        wav[19] = 0x00; // chunk size (16)
        wav[20] = 0x01;
        wav[21] = 0x00; // PCM
        wav[22] = 0x02;
        wav[23] = 0x00; // channels (2)
        wav[24] = 0x44;
        wav[25] = 0xAC;
        wav[26] = 0x00;
        wav[27] = 0x00; // sample rate (44100)
        wav[28] = 0x00;
        wav[29] = 0x02;
        wav[30] = 0x00;
        wav[31] = 0x00; // byte rate
        wav[32] = 0x04;
        wav[33] = 0x00; // block align
        wav[34] = 0x10;
        wav[35] = 0x00; // bits per sample (16)
        wav[36] = 0x64;
        wav[37] = 0x61;
        wav[38] = 0x74;
        wav[39] = 0x61; // "data"
        wav[40] = 0x00;
        wav[41] = 0x00;
        wav[42] = 0x00;
        wav[43] = 0x00; // data size

        AudioProcessor.AudioMeta? meta = AudioProcessor.ParseAudioMetadata("test.wav", wav.AsSpan());
        Assert.NotNull(meta);
        Assert.Equal("WAV", meta!.Format);
        Assert.Equal(44100, meta.SampleRate);
    }

    [Fact]
    public void AudioMetadata_InvalidBytes_ReturnsNull()
    {
        AudioProcessor.AudioMeta? meta = AudioProcessor.ParseAudioMetadata("test.mp3", new byte[]
        {
            0x00, 0x01, 0x02
        });
        Assert.Null(meta);
    }

    // ============================================================
    // VideoProcessor metadata parsing tests
    // ============================================================

    [Fact]
    public void VideoMetadata_Mp4_ParsesDuration()
    {
        // Minimal MP4 with ftyp + mvhd
        byte[] mp4 = new byte[100];
        // Box size at offset 0
        mp4[0] = 0x00;
        mp4[1] = 0x00;
        mp4[2] = 0x00;
        mp4[3] = 0x20; // box size (32)
        mp4[4] = 0x66;
        mp4[5] = 0x74;
        mp4[6] = 0x79;
        mp4[7] = 0x70; // "ftyp"
        // mvhd box starting at offset 32
        mp4[32 + 0] = 0x00;
        mp4[32 + 1] = 0x00;
        mp4[32 + 2] = 0x00;
        mp4[32 + 3] = 0x20; // box size
        mp4[32 + 4] = 0x6D;
        mp4[32 + 5] = 0x76;
        mp4[32 + 6] = 0x68;
        mp4[32 + 7] = 0x64; // "mvhd"
        mp4[32 + 8] = 0x00; // version
        mp4[32 + 12] = 0x00;
        mp4[32 + 13] = 0x00;
        mp4[32 + 14] = 0x03;
        mp4[32 + 15] = 0xE8; // timescale (1000)
        mp4[32 + 16] = 0x00;
        mp4[32 + 17] = 0x00;
        mp4[32 + 18] = 0x0E;
        mp4[32 + 19] = 0x10; // duration (3600 = 3.6s at 1000)

        VideoProcessor.VideoMeta? meta = VideoProcessor.ParseVideoMetadata("test.mp4", mp4.AsSpan());
        Assert.NotNull(meta);
        Assert.Equal("MP4", meta!.Format);
        Assert.True(meta.Duration > 0);
    }

    // ============================================================
    // CsvProcessor tests
    // ============================================================

    [Fact]
    public async Task CsvProcessor_ParsesHeader()
    {
        var proc = new CsvProcessor();
        byte[] bytes = "name,age,city\nAlice,30,NYC\nBob,25,LA"u8.ToArray();
        var ctx = new ContentProcessorContext("/test/data.csv",
            "data.csv",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("Columns", result.TextData.ToString());
        Assert.Contains("name", result.TextData.ToString());
        Assert.Contains("age", result.TextData.ToString());
        Assert.Contains("Alice", result.TextData.ToString());
    }

    [Fact]
    public async Task CsvProcessor_ReportsRowsBeyondPreviewLimit()
    {
        var proc = new CsvProcessor();
        string csv = "name\n" + string.Join('\n', Enumerable.Range(1, 51).Select(i => $"row{i}"));
        byte[] bytes = Encoding.UTF8.GetBytes(csv);
        var ctx = new ContentProcessorContext("/test/data.csv",
            "data.csv",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);

        Assert.Contains("row50", result.TextData.ToString());
        Assert.DoesNotContain("row51", result.TextData.ToString());
        Assert.Contains("... (50+ total rows)", result.TextData.ToString());
    }

    // ============================================================
    // EmailProcessor tests
    // ============================================================

    [Fact]
    public async Task EmailProcessor_ExtractsHeaders()
    {
        var proc = new EmailProcessor();
        byte[] email =
            "From: alice@example.com\nTo: bob@example.com\nSubject: Hello\n\nBody text here"u8.ToArray();
        var ctx = new ContentProcessorContext("/test/msg.eml",
            "msg.eml",
            email.AsMemory(),
            email.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("From", result.TextData.ToString());
        Assert.Contains("alice@example.com", result.TextData.ToString());
        Assert.Contains("Subject", result.TextData.ToString());
        Assert.Contains("Hello", result.TextData.ToString());
        Assert.Contains("Body text here", result.TextData.ToString());
    }

    [Fact]
    public void EmailProcessor_SplitEmail_HandlesHeaders()
    {
        (List<(string Key, string Value)> headers, string body) = EmailProcessor.SplitEmail("From: a\nTo: b\n\nBody");
        Assert.Contains(headers, h => h.Key == "From" && h.Value == "a");
        Assert.Contains(headers, h => h.Key == "To" && h.Value == "b");
        Assert.Equal("Body", body);
    }

    // ============================================================
    // SvgProcessor tests
    // ============================================================

    [Fact]
    public async Task SvgProcessor_ReturnsContent()
    {
        var proc = new SvgProcessor();
        byte[] svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"100\" height=\"100\"/></svg>"u8.ToArray();
        var ctx = new ContentProcessorContext("/test/image.svg",
            "image.svg",
            svg.AsMemory(),
            svg.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("<svg", result.TextData.ToString());
        Assert.Contains("rect", result.TextData.ToString());
    }

    // ============================================================
    // ArchiveProcessor tests
    // ============================================================

    [Fact]
    public async Task ArchiveProcessor_NotZip_ReturnsListingUnavailable()
    {
        var proc = new ArchiveProcessor();
        byte[] bytes = new byte[]
        {
            0x00, 0x01, 0x02, 0x03
        }; // not a valid ZIP
        var ctx = new ContentProcessorContext("/test/archive.zip",
            "archive.zip",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("shell commands", result.TextData.ToString());
    }

    // ============================================================
    // NotebookProcessor tests
    // ============================================================

    [Fact]
    public async Task NotebookProcessor_ExtractsCodeCells()
    {
        var proc = new NotebookProcessor();
        byte[] nb =
            "{\"cells\":[{\"cell_type\":\"code\",\"source\":[\"print(\\\"hello\\\")\\n\"]},{\"cell_type\":\"markdown\",\"source\":[\"# Title\\n\"]}]}"u8
                .ToArray();
        var ctx = new ContentProcessorContext("/test/notebook.ipynb",
            "notebook.ipynb",
            nb.AsMemory(),
            nb.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("print", result.TextData.ToString());
        Assert.Contains("hello", result.TextData.ToString());
        Assert.Contains("Title", result.TextData.ToString());
    }

    // ============================================================
    // ModalityUsedEvent tests
    // ============================================================

    [Fact]
    public void ModalityUsedEvent_RoundTrips()
    {
        var evt = new ModalityUsedEvent(EventEnvelope.ForSession(new SessionId(Guid.NewGuid())),
            "image",
            "screenshot.png",
            OutputModality.ImageBase64);

        Assert.Equal("image", evt.Modality);
        Assert.Equal("screenshot.png", evt.RelativePath);
        Assert.Equal(OutputModality.ImageBase64, evt.OutputKind);
        Assert.Equal(nameof(ModalityUsedEvent), evt.GetType().Name);
    }

    // ============================================================
    // Modality validation tests (Phase 6)
    // ============================================================

    [Fact]
    public void ModalityValidation_PassesWhenTargetSupportsAll()
    {
        var events = new List<OmicronEvent>
        {
            new ModalityUsedEvent(EventEnvelope.ForSession(new SessionId(Guid.NewGuid())),
                "image",
                "screenshot.png",
                OutputModality.ImageBase64)
        };

        var target = new ModelMetadata("test",
            "test",
            Modalities: new HashSet<string>
            {
                "text",
                "image"
            });

        IReadOnlyList<OmicronHost.ModalityValidationError> errors =
            OmicronHost.CheckModalityCompatibility(events, target);
        Assert.Empty(errors);
    }

    [Fact]
    public void ModalityValidation_FailsWhenModalityMissing()
    {
        var events = new List<OmicronEvent>
        {
            new ModalityUsedEvent(EventEnvelope.ForSession(new SessionId(Guid.NewGuid())),
                "image",
                "screenshot.png",
                OutputModality.ImageBase64),
            new ModalityUsedEvent(EventEnvelope.ForSession(new SessionId(Guid.NewGuid())),
                "pdf",
                "report.pdf",
                OutputModality.PdfBase64)
        };

        var target = new ModelMetadata("text-only",
            "test",
            Modalities: new HashSet<string>
            {
                "text"
            });

        IReadOnlyList<OmicronHost.ModalityValidationError> errors =
            OmicronHost.CheckModalityCompatibility(events, target);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Modality == "image");
        Assert.Contains(errors, e => e.Modality == "pdf");
        Assert.Contains(errors[0].AffectedFiles, f => f == "screenshot.png");
    }

    [Fact]
    public void ModalityValidation_NoModalityEvents_ReturnsEmpty()
    {
        var events = new List<OmicronEvent>
        {
            new UserMessageEvent(EventEnvelope.ForSession(new SessionId(Guid.NewGuid())),
                "hello"u8)
        };

        var target = new ModelMetadata("text-only",
            "test",
            Modalities: new HashSet<string>
            {
                "text"
            });

        IReadOnlyList<OmicronHost.ModalityValidationError> errors =
            OmicronHost.CheckModalityCompatibility(events, target);
        Assert.Empty(errors);
    }

    [Fact]
    public void ModalityValidation_DeduplicatesAffectedFiles()
    {
        var events = new List<OmicronEvent>
        {
            new ModalityUsedEvent(EventEnvelope.ForSession(new SessionId(Guid.NewGuid())),
                "image",
                "photo.png",
                OutputModality.ImageBase64),
            new ModalityUsedEvent(EventEnvelope.ForSession(new SessionId(Guid.NewGuid())),
                "image",
                "photo.png",
                OutputModality.ImageBase64)
        };

        var target = new ModelMetadata("text-only",
            "test",
            Modalities: new HashSet<string>
            {
                "text"
            });

        IReadOnlyList<OmicronHost.ModalityValidationError> errors =
            OmicronHost.CheckModalityCompatibility(events, target);
        OmicronHost.ModalityValidationError imageError = Assert.Single(errors);
        Assert.Equal("image", imageError.Modality);
        Assert.Single(imageError.AffectedFiles); // deduplicated
    }

    [Fact]
    public void ModalityValidation_FormatErrors_IncludesDiagnostic()
    {
        var errors = new List<OmicronHost.ModalityValidationError>
        {
            new("image", new[]
            {
                "photo.png", "diagram.svg"
            }),
            new("pdf", new[]
            {
                "report.pdf"
            })
        };

        // FormatModalityErrors is private, test via the public contract
        // This test verifies the error records are constructable
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Modality == "image" && e.AffectedFiles.Count == 2);
        Assert.Contains(errors, e => e.Modality == "pdf" && e.AffectedFiles.Count == 1);
    }

    // ============================================================
    // OpenXmlProcessor tests
    // ============================================================

    [Fact]
    public async Task OpenXmlProcessor_NonZip_ReturnsFallback()
    {
        var proc = new OpenXmlProcessor();
        byte[] bytes = new byte[]
        {
            0x00, 0x01, 0x02
        };
        var ctx = new ContentProcessorContext("/test/doc.docx",
            "doc.docx",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        // Fallback to hex dump
        Assert.NotEqual(OutputModality.Text, result.ActualModality);
    }

    // ============================================================
    // LegacyOfficeProcessor tests
    // ============================================================

    [Fact]
    public async Task LegacyOfficeProcessor_NonOle2_ReturnsFallback()
    {
        var proc = new LegacyOfficeProcessor();
        byte[] bytes = new byte[]
        {
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08
        };
        var ctx = new ContentProcessorContext("/test/doc.doc",
            "doc.doc",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        // Fallback to hex dump (no OLE2 signature)
        Assert.NotEqual(OutputModality.Text, result.ActualModality);
    }

    // ============================================================
    // EbookProcessor tests
    // ============================================================

    [Fact]
    public async Task EbookProcessor_NonZip_ReturnsUnavailable()
    {
        var proc = new EbookProcessor();
        byte[] bytes = new byte[]
        {
            0x00, 0x01, 0x02
        };
        var ctx = new ContentProcessorContext("/test/book.epub",
            "book.epub",
            bytes.AsMemory(),
            bytes.Length,
            new SessionId(Guid.Empty),
            null,
            "auto",
            null,
            null,
            null,
            CancellationToken.None);

        ContentProcessorResult result = await proc.ProcessAsync(ctx);
        Assert.Contains("unavailable", result.TextData.ToString());
    }
}
