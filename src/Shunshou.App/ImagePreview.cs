using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Shunshou.App;

/// <summary>
/// Loads a local picture into a BitmapImage through an in-memory stream. This is the same pattern the
/// search preview uses; wrapping a FileStream directly can stall in unpackaged WinUI hosts.
/// </summary>
internal static class ImagePreview
{
    public static async Task<BitmapImage?> FromFileAsync(string path, int decodeWidth = 0)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
        }
        stream.Seek(0);
        var image = new BitmapImage();
        if (decodeWidth > 0)
        {
            image.DecodePixelType = DecodePixelType.Physical;
            image.DecodePixelWidth = decodeWidth;
        }
        await image.SetSourceAsync(stream);
        return image;
    }
}
