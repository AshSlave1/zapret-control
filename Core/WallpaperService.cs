using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace ZapretControl.Core;

public static class WallpaperService
{
    private const uint SpiGetDeskWallpaper = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

    public static string? GetWallpaperPath()
    {
        // Windows 10 хранит перекодированную копию обоев — её WPF точно умеет читать.
        var transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");

        if (File.Exists(transcoded))
            return transcoded;

        var cachedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "CachedFiles");

        try
        {
            if (Directory.Exists(cachedDir))
            {
                var cached = Directory.EnumerateFiles(cachedDir)
                    .FirstOrDefault(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));
                if (cached is not null)
                    return cached;
            }
        }
        catch
        {
            // Игнорируем.
        }

        var buffer = new StringBuilder(1024);
        if (SystemParametersInfo(SpiGetDeskWallpaper, (uint)buffer.Capacity, buffer, 0))
        {
            var path = buffer.ToString();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;
        }

        return null;
    }

    public static BitmapSource? LoadWallpaper()
    {
        var path = GetWallpaperPath();
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }
}
