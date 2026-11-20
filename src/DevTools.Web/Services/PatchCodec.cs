using System.IO.Compression;
using System.Text.Json;
using DevTools.Shared.Models;

namespace DevTools.Web.Services;

/// <summary>Gzip JSON encoding for snapshot manifests and stored patch change lists.</summary>
public static class PatchCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Write<T>(T value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            JsonSerializer.Serialize(gzip, value, Json);
        return output.ToArray();
    }

    public static T? Read<T>(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(input, Json);
    }

    public static List<PatchModuleManifest> ReadManifest(byte[] data) => Read<List<PatchModuleManifest>>(data) ?? [];

    public static string WriteNames(IEnumerable<string> names) => JsonSerializer.Serialize(names.ToList(), Json);

    public static IReadOnlyList<string> ReadNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }
}
