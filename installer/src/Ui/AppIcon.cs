// The CK3 x Bannerlord roundel (two crossed swords in gold on a crimson field), drawn in code: the same design as the bridge's
// tray icon. assets/CK3xBL.ico is generated from this by "CK3xBL.Setup.Tests.exe icon".
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace CK3xBL.Setup.Ui;

internal static class AppIcon
{
    public static readonly Color Crimson = Color.FromArgb(122, 30, 30);
    public static readonly Color Gold = Color.FromArgb(217, 183, 107);
    public static readonly Color Steel = Color.FromArgb(244, 225, 196);

    /// <summary>The roundel at any size (designed on a 32 px grid).</summary>
    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float k = size / 32f;
        using var field = new SolidBrush(Crimson);
        using var rim = new Pen(Gold, Math.Max(1f, 2f * k));
        g.FillEllipse(field, 1 * k, 1 * k, 29.5f * k, 29.5f * k);
        g.DrawEllipse(rim, 1 * k, 1 * k, 29.5f * k, 29.5f * k);
        using var blade = new Pen(Steel, Math.Max(1.2f, 2.6f * k)) { StartCap = LineCap.Triangle, EndCap = LineCap.Round };
        using var hilt = new Pen(Gold, Math.Max(1.2f, 2.4f * k));
        g.DrawLine(blade, 9 * k, 9 * k, 23 * k, 23 * k);
        g.DrawLine(blade, 23 * k, 9 * k, 9 * k, 23 * k);
        g.DrawLine(hilt, 18 * k, 24 * k, 24 * k, 18 * k);
        g.DrawLine(hilt, 8 * k, 18 * k, 14 * k, 24 * k);
        return bmp;
    }

    /// <summary>A multi-size .ico (PNG-compressed entries, Windows Vista and later).</summary>
    public static byte[] BuildIco(IEnumerable<int> sizes)
    {
        var images = new List<(int size, byte[] png)>();
        foreach (var s in sizes)
        {
            using var bmp = Draw(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            images.Add((s, ms.ToArray()));
        }
        using var output = new MemoryStream();
        using var w = new BinaryWriter(output);
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type: icon
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);            // palette
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
        w.Flush();
        return output.ToArray();
    }

    /// <summary>The window icon: the exe's own icon, else the drawn one.</summary>
    public static Icon WindowIcon()
    {
        try
        {
            var exe = System.Windows.Forms.Application.ExecutablePath;
            var icon = Icon.ExtractAssociatedIcon(exe);
            if (icon is not null) return icon;
        }
        catch (Exception e) when (e is ArgumentException or IOException) { }
        using var bmp = Draw(32);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
