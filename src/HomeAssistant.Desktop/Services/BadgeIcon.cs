using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace HomeAssistant.Desktop.Services;

/// <summary>
/// Draws notification counts as icons.
///
/// Two places need one. The taskbar button takes a small overlay icon that the shell
/// composites into the corner itself. The tray icon has no such facility, so the count
/// has to be drawn onto a copy of the app icon. Both are the same badge, so they are
/// drawn by the same code and look alike.
/// </summary>
public static class BadgeIcon
{
    private static readonly Color Fill = Color.FromArgb(255, 209, 56, 56);
    private static readonly Color Rim = Color.FromArgb(235, 255, 255, 255);

    /// <summary>
    /// A badge on its own, sized to fill the icon, for the taskbar's overlay slot.
    /// </summary>
    public static IntPtr CreateOverlay(int count, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            Draw(graphics, count, new RectangleF(0.5f, 0.5f, size - 1f, size - 1f));
        }

        return bitmap.GetHicon();
    }

    /// <summary>
    /// The app icon with a badge in its corner, for the notification area. Returns
    /// <see cref="IntPtr.Zero"/> if the icon cannot be read, so the caller keeps using
    /// whatever it already had.
    /// </summary>
    public static IntPtr CreateOverIcon(string iconPath, int count, int size)
    {
        if (!File.Exists(iconPath))
        {
            return IntPtr.Zero;
        }

        try
        {
            using var source = new Icon(iconPath, size, size);
            using var baseImage = source.ToBitmap();
            using var bitmap = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(baseImage, 0, 0, size, size);

                // Roughly three fifths of the icon, bottom-right: big enough to read at
                // 16px, small enough to leave the app recognisable underneath.
                var badge = size * 0.6f;
                Draw(graphics, count, new RectangleF(size - badge - 0.5f, size - badge - 0.5f, badge, badge));
            }

            return bitmap.GetHicon();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException)
        {
            Log.Error("badge", "could not draw the tray badge", ex);
            return IntPtr.Zero;
        }
    }

    private static void Draw(Graphics graphics, int count, RectangleF bounds)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var text = count > 99 ? "99+" : count.ToString();

        using (var fill = new SolidBrush(Fill))
        {
            graphics.FillEllipse(fill, bounds);
        }

        // A rim keeps the badge legible against a taskbar of any colour.
        using (var rim = new Pen(Rim, Math.Max(1f, bounds.Width / 14f)))
        {
            graphics.DrawEllipse(rim, bounds);
        }

        // Longer numbers need a smaller face to stay inside the circle.
        var emSize = text.Length switch
        {
            1 => bounds.Height * 0.66f,
            2 => bounds.Height * 0.52f,
            _ => bounds.Height * 0.40f,
        };

        using var font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        using var textBrush = new SolidBrush(Color.White);
        graphics.DrawString(text, font, textBrush, bounds, format);
    }
}
