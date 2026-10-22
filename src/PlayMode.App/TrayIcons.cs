using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PlayMode.App;

/// <summary>A computer with the mode on its screen: blue DEV, red GAME, amber dots while switching.</summary>
static class TrayIcons
{
    // Drawn at each size rather than shrunk: a 32 px icon squeezed into the 16 px tray slot turns to mush.
    // PlayMode.ico (the exe and installer icon) is Dev saved from here, so it has the large sizes Explorer uses.
    static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    // 3x5 pixel letters, rows top to bottom: a normal font is unreadable at 16 px, and GAME only just fits.
    static readonly Dictionary<char, string> Letters = new()
    {
        ['D'] = "110101101101110",
        ['E'] = "111100110100111",
        ['V'] = "101101101101010",
        ['G'] = "011100101101011",
        ['A'] = "010101111101101",
        ['M'] = "101111111101101",
        ['.'] = "000000010000000",
    };

    public static Icon Dev { get; } = Make(Color.FromArgb(0x25, 0x7C, 0xF0), "DEV");
    public static Icon Play { get; } = Make(Color.FromArgb(0xE5, 0x2E, 0x2E), "GAME");
    public static Icon Busy { get; } = Make(Color.FromArgb(0xF5, 0x9E, 0x0B), "...");

    // The tray takes whatever image an icon's handle gives it, so hand it the small one explicitly.
    public static Icon TrayDev { get; } = new(Dev, SystemInformation.SmallIconSize);
    public static Icon TrayPlay { get; } = new(Play, SystemInformation.SmallIconSize);
    public static Icon TrayBusy { get; } = new(Busy, SystemInformation.SmallIconSize);

    static Icon Make(Color color, string word)
    {
        var frames = Sizes.Select(size => Png(Draw(size, color, word))).ToList();
        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0);   // ICONDIR: reserved, type 1 = icon, count
            w.Write((ushort)1);
            w.Write((ushort)frames.Count);
            var offset = 6 + 16 * frames.Count;
            for (var i = 0; i < frames.Count; i++)
            {
                w.Write((byte)Sizes[i]);   // 256 wraps to 0, which is how ICO spells 256
                w.Write((byte)Sizes[i]);
                w.Write((byte)0);     // palette size
                w.Write((byte)0);     // reserved
                w.Write((ushort)1);   // planes
                w.Write((ushort)32);  // bits per pixel
                w.Write(frames[i].Length);
                w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames)
                w.Write(frame);
        }
        ico.Position = 0;
        return new Icon(ico);
    }

    /// <summary>Monitor on a stand with the word on its screen, laid out on a 16-unit grid and scaled.</summary>
    static Bitmap Draw(int size, Color color, string word)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var k = size / 16f;
        using var body = new SolidBrush(color);
        using (var screen = Rounded(0, 0, 16 * k, 12 * k, 1.5f * k))
            g.FillPath(body, screen);
        g.FillRectangle(body, 6.5f * k, 11.5f * k, 3 * k, 2 * k);
        using (var foot = Rounded(3.5f * k, 13.5f * k, 9 * k, 2.5f * k, 1 * k))
            g.FillPath(body, foot);

        // Letters snap to whole pixels so they stay crisp at sizes that aren't multiples of 16.
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        int Px(int unit) => (int)MathF.Floor(unit * k);
        var left = (16 - (word.Length * 4 - 1)) / 2;
        const int top = 3;
        for (var i = 0; i < word.Length; i++)
        {
            var bits = Letters[word[i]];
            for (var row = 0; row < 5; row++)
                for (var col = 0; col < 3; col++)
                {
                    if (bits[row * 3 + col] != '1')
                        continue;
                    var x = left + i * 4 + col;
                    var y = top + row;
                    g.FillRectangle(Brushes.White, Px(x), Px(y), Px(x + 1) - Px(x), Px(y + 1) - Px(y));
                }
        }
        return bitmap;
    }

    static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static byte[] Png(Bitmap bitmap)
    {
        using (bitmap)
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }
}
