using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace ZapretControl.Core;

public static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Drawing.Icon CreateTrayIcon(bool active, int size = 32)
    {
        using var bitmap = new Drawing.Bitmap(size, size);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Drawing.Color.Transparent);

        var neon = active
            ? Drawing.Color.FromArgb(255, 45, 111)
            : Drawing.Color.FromArgb(130, 110, 122);

        using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(235, 7, 4, 10));
        graphics.FillEllipse(background, 1, 1, size - 2, size - 2);

        using var ring = new Drawing.Pen(neon, Math.Max(1.6f, size / 18f));
        graphics.DrawEllipse(ring, 2.5f, 2.5f, size - 5, size - 5);

        // Крупный значок — «DPI», мелкий (трей 16–32 px) — «D», иначе
        // три буквы превращаются в нечитаемое пятно.
        var mark = size >= 48 ? "DPI" : "D";
        var emSize = size >= 48 ? size * 0.34f : size * 0.55f;

        using var font = CreateFont(emSize);
        using var brush = new Drawing.SolidBrush(neon);
        using var path = BuildMarkPath(mark, font, size);

        if (size >= 48)
        {
            // Мини-глитч: хроматическая аберрация — циановый «призрак»
            // букв чуть левее и выше основного контура.
            using var cyan = new Drawing.SolidBrush(Drawing.Color.FromArgb(150, 0, 229, 255));
            using var ghost = (Drawing.Drawing2D.GraphicsPath)path.Clone();
            using var ghostMatrix = new Drawing.Drawing2D.Matrix();
            ghostMatrix.Translate(-size * 0.014f, size * 0.006f);
            ghost.Transform(ghostMatrix);
            graphics.FillPath(cyan, ghost);

            // Тонкие разрывы сигнала над и под надписью.
            var thickness = Math.Max(1f, size / 96f);
            using var cyanLine = new Drawing.Pen(Drawing.Color.FromArgb(180, 0, 229, 255), thickness);
            using var pinkLine = new Drawing.Pen(Drawing.Color.FromArgb(160, 255, 45, 111), thickness);

            graphics.DrawLine(cyanLine, size * 0.18f, size * 0.30f, size * 0.42f, size * 0.30f);
            graphics.DrawLine(cyanLine, size * 0.52f, size * 0.30f, size * 0.80f, size * 0.30f);
            graphics.DrawLine(pinkLine, size * 0.22f, size * 0.71f, size * 0.40f, size * 0.71f);
            graphics.DrawLine(pinkLine, size * 0.58f, size * 0.71f, size * 0.76f, size * 0.71f);

            // Битый пиксель на кольце.
            using var pixel = new Drawing.SolidBrush(Drawing.Color.FromArgb(190, 0, 229, 255));
            graphics.FillRectangle(pixel, size * 0.70f, size * 0.56f, size * 0.05f, size * 0.015f);
        }

        graphics.FillPath(brush, path);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static ImageSource CreateImageSource(bool active, int size = 64)
    {
        using var icon = CreateTrayIcon(active, size);
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static Drawing.Drawing2D.GraphicsPath BuildMarkPath(string mark, Drawing.Font font, int size)
    {
        var glyphs = new List<Drawing.Drawing2D.GraphicsPath>();
        var widths = new List<float>();
        var gap = mark.Length > 1 ? size * 0.018f : 0f;

        foreach (var ch in mark)
        {
            var glyph = new Drawing.Drawing2D.GraphicsPath();
            glyph.AddString(
                ch.ToString(),
                font.FontFamily,
                (int)font.Style,
                font.Size,
                Drawing.PointF.Empty,
                Drawing.StringFormat.GenericTypographic);

            widths.Add(glyph.GetBounds().Width);
            glyphs.Add(glyph);
        }

        // Собираем буквы по их реальным контурам, без широких боковых
        // отступов шрифта — иначе «DPI» выглядит как «D P I».
        var totalWidth = widths.Sum() + gap * (mark.Length - 1);
        var cursor = (size - totalWidth) / 2f;
        var union = Drawing.RectangleF.Empty;

        for (var i = 0; i < glyphs.Count; i++)
        {
            var bounds = glyphs[i].GetBounds();
            using var move = new Drawing.Drawing2D.Matrix();
            move.Translate(cursor - bounds.X, 0);
            glyphs[i].Transform(move);

            var moved = glyphs[i].GetBounds();
            union = union.IsEmpty ? moved : Drawing.RectangleF.Union(union, moved);
            cursor += widths[i] + gap;
        }

        using var center = new Drawing.Drawing2D.Matrix();
        center.Translate(0, (size - union.Height) / 2f - union.Y);

        var result = new Drawing.Drawing2D.GraphicsPath();
        foreach (var glyph in glyphs)
        {
            glyph.Transform(center);
            result.AddPath(glyph, false);
            glyph.Dispose();
        }

        return result;
    }

    private static Drawing.Font CreateFont(float pixelSize)
    {
        try
        {
            return new Drawing.Font("Bahnschrift", pixelSize, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        }
        catch
        {
            return new Drawing.Font("Segoe UI", pixelSize, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        }
    }
}
