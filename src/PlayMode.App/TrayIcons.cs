using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PlayMode.App;

/// <summary>Round badges with a Windows icon-font glyph: code for dev, controller for play, sync while switching.</summary>
static class TrayIcons
{
    public static Icon Dev { get; } = Make('', Color.FromArgb(0x3B, 0x6F, 0xD4));
    public static Icon Play { get; } = Make('', Color.FromArgb(0x16, 0xA3, 0x4A));
    public static Icon Busy { get; } = Make('', Color.FromArgb(0xD9, 0x77, 0x06));

    static readonly string GlyphFont =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    static Icon Make(char glyph, Color background)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(background);
            g.FillEllipse(brush, 0, 0, 31, 31);
            using var font = new Font(GlyphFont, 17, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph.ToString(), font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();   // own copy, so the GDI handle can be freed
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyIcon(nint handle);
}
