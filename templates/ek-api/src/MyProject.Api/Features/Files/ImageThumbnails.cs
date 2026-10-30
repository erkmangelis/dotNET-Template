using SkiaSharp;

namespace MyProject.Api.Features.Files;

/// <summary>Reads image sizes and makes thumbnails with SkiaSharp (MIT).</summary>
internal static class ImageThumbnails
{
    public const string ContentType = "image/webp";

    /// <summary>The image's width times its height, read from its header without decoding it, or null when it is not an image Skia reads.</summary>
    public static long? PixelCount(Stream image)
    {
        using var codec = SKCodec.Create(new NonDisposingStream(image));
        return codec is null ? null : (long)codec.Info.Width * codec.Info.Height;
    }

    /// <summary>
    /// A WebP no larger than <paramref name="maxSide"/> on its longest side, keeping the proportions,
    /// or null when the image cannot be decoded. Re-encoding also drops the original's metadata, such
    /// as the GPS position a phone writes into a photo.
    /// </summary>
    public static byte[]? Create(Stream image, int maxSide)
    {
        using var codec = SKCodec.Create(new NonDisposingStream(image));
        if (codec is null)
        {
            return null;
        }

        using var original = SKBitmap.Decode(codec);
        if (original is null)
        {
            return null;
        }

        var scale = Math.Min(1.0, (double)maxSide / Math.Max(original.Width, original.Height));
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(original.Width * scale)), Math.Max(1, (int)Math.Round(original.Height * scale)));
        using var resized = original.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var encoded = resized?.Encode(SKEncodedImageFormat.Webp, 80);
        return encoded?.ToArray();
    }

    /// <summary>Skia disposes the stream it reads; the caller still needs this one.</summary>
    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
