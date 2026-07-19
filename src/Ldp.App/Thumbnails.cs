using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Ldp.Engine;
using System.Runtime.InteropServices;

namespace Ldp.App;

public static class Thumbnails
{
    /// <summary>Converts a decoded frame into a downscaled bitmap (nearest-neighbor).</summary>
    public static WriteableBitmap FromFrame(FrameImage image, int targetWidth = 240)
    {
        int factor = System.Math.Max(1, image.Width / targetWidth);
        int w = image.Width / factor;
        int h = image.Height / factor;

        var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                         PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using ILockedFramebuffer fb = bitmap.Lock();
        unsafe
        {
            byte* dst = (byte*)fb.Address;
            fixed (byte* src = image.Bgra)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* srcRow = src + (long)(y * factor) * image.Stride;
                    uint* dstRow = (uint*)(dst + (long)y * fb.RowBytes);
                    for (int x = 0; x < w; x++)
                        dstRow[x] = *(uint*)(srcRow + (long)(x * factor) * 4);
                }
            }
        }
        return bitmap;
    }
}
