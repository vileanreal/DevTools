namespace DevTools.Shared.Helpers;

/// <summary>
/// Decides whether two files (or a file and an archive entry) have identical content. Sizes are compared first; equal
/// sizes are then compared byte by byte, which gives the same answer as comparing SHA-256 hashes but stops at the first
/// difference instead of reading both files completely.
/// </summary>
public static class ContentComparer
{
    private const int BufferSize = 128 * 1024;

    public static bool FilesEqual(string first, string second)
    {
        var a = new FileInfo(first);
        var b = new FileInfo(second);
        if (!a.Exists || !b.Exists || a.Length != b.Length) return false;
        using var left = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
        using var right = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
        return StreamsEqual(left, right);
    }

    /// <summary>Reads both streams to the end (or to the first difference). Works with non-seekable archive streams.</summary>
    public static bool StreamsEqual(Stream first, Stream second)
    {
        var left = new byte[BufferSize];
        var right = new byte[BufferSize];
        while (true)
        {
            var read = Fill(first, left);
            if (Fill(second, right) != read) return false;
            if (read == 0) return true;
            if (!left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read))) return false;
        }
    }

    // Stream.Read may return fewer bytes than asked (common for decompression streams), so fill the buffer fully.
    private static int Fill(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
