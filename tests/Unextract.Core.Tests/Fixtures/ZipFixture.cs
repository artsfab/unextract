using System.IO.Compression;
using System.Text;

namespace Unextract.Core.Tests.Fixtures;

internal sealed record FixtureEntry(
    string Name,
    byte[]? Content = null,
    int ExternalAttributes = 0,
    CompressionLevel Level = CompressionLevel.Optimal);

// テスト専用: 正常な ZIP を ZipArchive で作る。異常な ZIP は ZipPatcher でバイトを書き換えて作る。
internal static class ZipFixture
{
    public static readonly Encoding Cp437 = CodePagesEncodingProvider.Instance.GetEncoding(437)!;

    public static byte[] Create(IEnumerable<FixtureEntry> entries, Encoding? nameEncoding = null, bool nonSeekable = false)
    {
        using var buffer = new MemoryStream();
        Stream output = nonSeekable ? new NonSeekableWriteStream(buffer) : buffer;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, nameEncoding))
        {
            foreach (var fixture in entries)
            {
                var entry = archive.CreateEntry(fixture.Name, fixture.Level);
                entry.ExternalAttributes = fixture.ExternalAttributes;
                using var stream = entry.Open();
                if (fixture.Content is { } content)
                {
                    stream.Write(content);
                }
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Create(params FixtureEntry[] entries) => Create((IEnumerable<FixtureEntry>)entries);

    // 出力先をシーク不可にすると、ZipArchive は Data Descriptor 付きで書く。
    private sealed class NonSeekableWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
