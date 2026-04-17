using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingColor = System.Drawing.Color;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace CLCKTY.App.Services;

public static class IconAssetLoader
{
    private const int TaskbarIconSize = 128;
    private const int TrayIconSize = 32;
    private const double LogoPaddingRatio = 0.05;

    public static ImageSource? LoadTaskbarLogo()
    {
        using var prepared = LoadPreparedLogoBitmap(TaskbarIconSize);
        if (prepared is null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        prepared.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static Icon? LoadTrayLogo(out IntPtr iconHandle)
    {
        iconHandle = IntPtr.Zero;

        using var prepared = LoadPreparedLogoBitmap(TrayIconSize);
        if (prepared is null)
        {
            return null;
        }

        iconHandle = prepared.GetHicon();
        using var iconFromHandle = Icon.FromHandle(iconHandle);
        return (Icon)iconFromHandle.Clone();
    }

    private static Bitmap? LoadPreparedLogoBitmap(int targetSize)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var pngPath = Path.Combine(baseDir, "Assets", "Icons", "clckty-app.png");
        if (!File.Exists(pngPath))
        {
            return null;
        }

        using var source = new Bitmap(pngPath);
        var sourceRect = GetOpaqueBounds(source);
        if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
        {
            sourceRect = new Rectangle(0, 0, source.Width, source.Height);
        }

        var iconBitmap = new Bitmap(targetSize, targetSize, DrawingPixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(iconBitmap);
        graphics.Clear(DrawingColor.Transparent);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;

        var padding = Math.Max(1, (int)Math.Round(targetSize * LogoPaddingRatio));
        var drawableSize = Math.Max(1, targetSize - (padding * 2));
        var scale = Math.Min(drawableSize / (double)sourceRect.Width, drawableSize / (double)sourceRect.Height);
        var drawWidth = Math.Max(1, (int)Math.Round(sourceRect.Width * scale));
        var drawHeight = Math.Max(1, (int)Math.Round(sourceRect.Height * scale));
        var drawX = (targetSize - drawWidth) / 2;
        var drawY = (targetSize - drawHeight) / 2;

        graphics.DrawImage(
            source,
            new Rectangle(drawX, drawY, drawWidth, drawHeight),
            sourceRect,
            GraphicsUnit.Pixel);

        return iconBitmap;
    }

    private static Rectangle GetOpaqueBounds(Bitmap bitmap)
    {
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A <= 8)
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return Rectangle.Empty;
        }

        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
}
