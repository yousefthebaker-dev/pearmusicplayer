using Windows.Storage.Streams;

namespace AmbientPlayer.Utilities;

/// <summary>
/// Reads a WinRT <see cref="IRandomAccessStreamReference"/> (as returned for
/// an SMTC thumbnail) fully into a byte array.
/// </summary>
public static class StreamReferenceReader
{
    /// <summary>
    /// A single <c>LoadAsync</c> call is not guaranteed to fill the buffer in
    /// one shot; a partial read here silently yields truncated JPEG data
    /// downstream (a known gotcha with SMTC thumbnails), so this loops until
    /// the full declared size has been read or the stream stops giving bytes.
    /// </summary>
    public static async Task<byte[]?> ReadAllBytesAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;

        using var stream = await reference.OpenReadAsync();
        var totalSize = stream.Size;
        if (totalSize == 0) return null;

        using var reader = new DataReader(stream);
        reader.InputStreamOptions = InputStreamOptions.None;

        var buffer = new byte[totalSize];
        ulong totalLoaded = 0;

        while (totalLoaded < totalSize)
        {
            var remaining = (uint)Math.Min(totalSize - totalLoaded, uint.MaxValue);
            var loaded = await reader.LoadAsync(remaining);
            if (loaded == 0) break; // stream exhausted early - return what we have

            var chunk = new byte[loaded];
            reader.ReadBytes(chunk);
            Array.Copy(chunk, 0, buffer, (long)totalLoaded, loaded);
            totalLoaded += loaded;
        }

        return totalLoaded == totalSize ? buffer : buffer[..(int)totalLoaded];
    }
}
