Add-Type -AssemblyName System.Drawing

$code = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

public static class IconGen {
    public static Bitmap CreateBitmap(int size) {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        // Drop shadow
        SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(65, 0, 0, 0));
        g.FillEllipse(shadowBrush, 5, 8, size - 8, size - 8);
        shadowBrush.Dispose();

        // Red gradient background
        GraphicsPath bgPath = new GraphicsPath();
        bgPath.AddEllipse(2, 2, size - 4, size - 4);
        PathGradientBrush gradBrush = new PathGradientBrush(bgPath);
        gradBrush.CenterPoint = new PointF(size * 0.38f, size * 0.35f);
        gradBrush.CenterColor = Color.FromArgb(255, 230, 68, 48);
        gradBrush.SurroundColors = new Color[] { Color.FromArgb(255, 118, 12, 12) };
        g.FillPath(gradBrush, bgPath);
        gradBrush.Dispose();
        bgPath.Dispose();

        // Top highlight glow
        GraphicsPath hlPath = new GraphicsPath();
        hlPath.AddEllipse(size * 0.18f, size * 0.08f, size * 0.64f, size * 0.38f);
        PathGradientBrush hlBrush = new PathGradientBrush(hlPath);
        hlBrush.CenterColor = Color.FromArgb(75, 255, 255, 255);
        hlBrush.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
        g.FillPath(hlBrush, hlPath);
        hlBrush.Dispose();
        hlPath.Dispose();

        // Outer gold ring
        float ringW = Math.Max(1.5f, size / 17f);
        float ringOff = ringW / 2f + 2f;
        Pen ringPen = new Pen(Color.FromArgb(255, 210, 162, 42), ringW);
        g.DrawEllipse(ringPen, ringOff, ringOff, size - ringOff * 2, size - ringOff * 2);
        ringPen.Dispose();

        // Inner thin ring
        if (size >= 32) {
            float innerW = Math.Max(0.8f, size / 36f);
            float pad = size / 9f;
            Pen innerPen = new Pen(Color.FromArgb(150, 255, 230, 115), innerW);
            g.DrawEllipse(innerPen, pad, pad, size - pad * 2, size - pad * 2);
            innerPen.Dispose();
        }

        // Character: xiang (U+8C61)
        string chessChar = "\u8C61";
        float fontSize = size * 0.52f;
        Font font = new Font("Microsoft YaHei", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        StringFormat sf = new StringFormat();
        sf.Alignment = StringAlignment.Center;
        sf.LineAlignment = StringAlignment.Center;

        // Text shadow
        if (size >= 32) {
            float sOff = Math.Max(1f, size / 32f);
            SolidBrush tsShadow = new SolidBrush(Color.FromArgb(115, 70, 0, 0));
            g.DrawString(chessChar, font, tsShadow, new RectangleF(sOff, sOff, size, size), sf);
            tsShadow.Dispose();
        }

        // Text body - gold
        SolidBrush textBrush = new SolidBrush(Color.FromArgb(255, 255, 238, 148));
        g.DrawString(chessChar, font, textBrush, new RectangleF(0, 0, size, size), sf);
        textBrush.Dispose();
        font.Dispose();
        sf.Dispose();
        g.Dispose();

        return bmp;
    }

    public static void WriteIco(string outputPath, int[] sizes) {
        MemoryStream[] streams = new MemoryStream[sizes.Length];
        for (int i = 0; i < sizes.Length; i++) {
            Bitmap bmp = CreateBitmap(sizes[i]);
            streams[i] = new MemoryStream();
            bmp.Save(streams[i], ImageFormat.Png);
            bmp.Dispose();
        }

        FileStream fs = File.Create(outputPath);
        BinaryWriter bw = new BinaryWriter(fs);

        // ICONDIR
        bw.Write((short)0);
        bw.Write((short)1);
        bw.Write((short)sizes.Length);

        // Compute offsets
        int headerSize = 6 + sizes.Length * 16;
        int[] offsets = new int[sizes.Length];
        int cur = headerSize;
        for (int i = 0; i < sizes.Length; i++) {
            offsets[i] = cur;
            cur += (int)streams[i].Length;
        }

        // ICONDIRENTRY
        for (int i = 0; i < sizes.Length; i++) {
            int s = sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write((int)streams[i].Length);
            bw.Write(offsets[i]);
        }

        // PNG data
        foreach (MemoryStream ms in streams) {
            bw.Write(ms.ToArray());
            ms.Dispose();
        }

        bw.Flush();
        bw.Dispose();
        fs.Dispose();
    }
}
'@

Add-Type -TypeDefinition $code -ReferencedAssemblies 'System.Drawing'

$outputIco = Join-Path $PSScriptRoot "app.ico"
[IconGen]::WriteIco($outputIco, [int[]]@(256, 128, 64, 48, 32, 16))
Write-Host "Icon generated: $outputIco"
