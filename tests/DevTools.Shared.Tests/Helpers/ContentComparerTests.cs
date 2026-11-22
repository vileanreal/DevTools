using System.Text;
using DevTools.Shared.Helpers;

namespace DevTools.Shared.Tests.Helpers;

public sealed class ContentComparerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devtools-compare-tests", Guid.NewGuid().ToString("N"));

    public ContentComparerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private string File(string name, string content) { var path = Path.Combine(_root, name); System.IO.File.WriteAllText(path, content); return path; }

    [Fact]
    public void IdenticalFiles_AreEqual() => Assert.True(ContentComparer.FilesEqual(File("a", "same text"), File("b", "same text")));

    [Fact]
    public void SameSize_DifferentContent_IsNotEqual() => Assert.False(ContentComparer.FilesEqual(File("a", "aaaa"), File("b", "aaab")));

    [Fact]
    public void DifferentSize_IsNotEqual() => Assert.False(ContentComparer.FilesEqual(File("a", "short"), File("b", "longer text")));

    [Fact]
    public void MissingFile_IsNotEqual() => Assert.False(ContentComparer.FilesEqual(File("a", "x"), Path.Combine(_root, "missing")));

    [Fact]
    public void LargeFiles_DifferingAtTheEnd_AreNotEqual()
    {
        var content = new string('x', 300_000);
        Assert.False(ContentComparer.FilesEqual(File("a", content + "1"), File("b", content + "2")));
        Assert.True(ContentComparer.FilesEqual(File("c", content), File("d", content)));
    }

    [Fact]
    public void Streams_ThatReturnFewBytesPerRead_AreComparedCorrectly()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('z', 200_000));
        Assert.True(ContentComparer.StreamsEqual(new TrickleStream(bytes), new MemoryStream(bytes)));
        Assert.False(ContentComparer.StreamsEqual(new TrickleStream(bytes), new MemoryStream(bytes[..^1])));
    }

    /// <summary>Returns at most 7 bytes per Read, like some decompression streams.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 7));
    }
}
