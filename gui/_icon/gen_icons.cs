// gen_icons.cs - 独立小工具: 生成 SRT 工具候选图标预览 PNG (256px)
// 用法: csc /nologo /out:gen_icons.exe gen_icons.cs && gen_icons.exe
// (System.Drawing, .NET Framework 4.8, 不参与主工程编译)

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class GenIcons
{
    static void Main()
    {
        string dir = @"E:\MyDoc\project\SRTproject\_transub_tmp\icon_preview";
        Directory.CreateDirectory(dir);
        int px = 256;
        DrawA(Path.Combine(dir, "A_字幕对勾.png"), px);
        DrawB(Path.Combine(dir, "B_播放字幕.png"), px);
        DrawC(Path.Combine(dir, "C_绿勾白底.png"), px);
        DrawD(Path.Combine(dir, "D_蓝底SRT.png"), px);
        BuildIco(Path.Combine(dir, "srt.ico"));
        Console.WriteLine("预览已生成: " + dir);
    }

    // 圆角矩形路径
    static GraphicsPath RoundedRect(RectangleF r, float rad)
    {
        GraphicsPath p = new GraphicsPath();
        float d = rad * 2f;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // A: 深底 + 双层白色字幕条 + 右上角绿勾
    static void DrawA(string path, int px)
    {
        using (Bitmap bmp = new Bitmap(px, px))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (GraphicsPath bg = RoundedRect(new RectangleF(6, 6, px - 12, px - 12), 46))
            using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 31, 41, 55)))
            {
                g.FillPath(br, bg);
            }
            // 主字幕条 (实心白)
            using (GraphicsPath b1 = RoundedRect(new RectangleF(px * 0.13f, px * 0.30f, px * 0.74f, px * 0.17f), 14))
            using (SolidBrush w = new SolidBrush(Color.White))
            {
                g.FillPath(w, b1);
            }
            // 第二字幕条 (半透明白)
            using (GraphicsPath b2 = RoundedRect(new RectangleF(px * 0.13f, px * 0.55f, px * 0.60f, px * 0.15f), 12))
            using (SolidBrush w2 = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            {
                g.FillPath(w2, b2);
            }
            // 右上角绿色圆底白勾
            float cx = px * 0.79f, cy = px * 0.24f, r = px * 0.105f;
            using (SolidBrush gr = new SolidBrush(Color.FromArgb(255, 34, 197, 94)))
            {
                g.FillEllipse(gr, cx - r, cy - r, r * 2, r * 2);
            }
            using (Pen wp = new Pen(Color.White, px * 0.045f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLines(wp, new PointF[] {
                    new PointF(cx - r * 0.42f, cy), new PointF(cx - r * 0.10f, cy + r * 0.34f), new PointF(cx + r * 0.48f, cy - r * 0.36f) });
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    // B: 蓝底 + 白色播放三角 + 底部白色字幕条
    static void DrawB(string path, int px)
    {
        using (Bitmap bmp = new Bitmap(px, px))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (GraphicsPath bg = RoundedRect(new RectangleF(6, 6, px - 12, px - 12), 46))
            using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 37, 99, 235)))
            {
                g.FillPath(br, bg);
            }
            // 播放三角
            using (SolidBrush w = new SolidBrush(Color.White))
            {
                PointF[] tri = new PointF[] {
                    new PointF(px * 0.40f, px * 0.24f),
                    new PointF(px * 0.72f, px * 0.435f),
                    new PointF(px * 0.40f, px * 0.63f) };
                g.FillPolygon(w, tri);
            }
            // 底部字幕条 (三条白短线)
            using (SolidBrush w2 = new SolidBrush(Color.White))
            {
                g.FillPath(w2, RoundedRect(new RectangleF(px * 0.17f, px * 0.70f, px * 0.66f, px * 0.075f), 8));
                g.FillPath(w2, RoundedRect(new RectangleF(px * 0.17f, px * 0.80f, px * 0.52f, px * 0.075f), 8));
                g.FillPath(w2, RoundedRect(new RectangleF(px * 0.17f, px * 0.90f, px * 0.38f, px * 0.075f), 8));
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    // C: 深底 + 白色双字幕条 (上长下短, 像 SRT 行) + 底部绿色对勾
    static void DrawC(string path, int px)
    {
        using (Bitmap bmp = new Bitmap(px, px))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (GraphicsPath bg = RoundedRect(new RectangleF(6, 6, px - 12, px - 12), 46))
            using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 15, 23, 42)))
            {
                g.FillPath(br, bg);
            }
            // 上方白色字幕条 (长)
            using (GraphicsPath b1 = RoundedRect(new RectangleF(px * 0.13f, px * 0.26f, px * 0.74f, px * 0.16f), 13))
            using (SolidBrush w = new SolidBrush(Color.White))
            {
                g.FillPath(w, b1);
            }
            // 下方字幕条 (短)
            using (GraphicsPath b2 = RoundedRect(new RectangleF(px * 0.13f, px * 0.50f, px * 0.55f, px * 0.15f), 12))
            using (SolidBrush w2 = new SolidBrush(Color.FromArgb(215, 255, 255, 255)))
            {
                g.FillPath(w2, b2);
            }
            // 底部绿色对勾 (无圆底)
            using (Pen gp = new Pen(Color.FromArgb(255, 34, 197, 94), px * 0.085f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float ox = px * 0.31f, oy = px * 0.80f;
                g.DrawLines(gp, new PointF[] {
                    new PointF(ox, oy + px * 0.05f),
                    new PointF(ox + px * 0.12f, oy + px * 0.17f),
                    new PointF(ox + px * 0.34f, oy - px * 0.13f) });
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    // D: 蓝底 + 上部白色 "SRT" 文字 + 下方双字幕条 (上短下长)
    static void DrawD(string path, int px)
    {
        using (Bitmap bmp = new Bitmap(px, px))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            DrawDTo(g, px);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    // D 设计矢量绘制 (多尺寸复用)
    // 小尺寸用简化版: 16px 单条 / 32px 双条(无文字, 文字会糊) / 48px+ 完整 (SRT+双条)
    static void DrawDTo(Graphics g, int px)
    {
        float m = px * 0.055f;                    // 边距
        float side = px - 2 * m;                  // 主体边长
        float rad = Math.Min(side * 0.20f, px * 0.09f);   // 圆角半径 (防小尺寸超界)
        float fs, bh;
        RectangleF rt;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using (GraphicsPath bg = RoundedRect(new RectangleF(m, m, side, side), rad))
        using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 37, 99, 235)))
        {
            g.FillPath(br, bg);
        }
        if (px >= 48)
        {
            // 48/256: SRT 文字 + 双条 (上短下长); 48 字号略小
            fs = px >= 256 ? px * 0.30f : px * 0.26f;
            using (Font f = new Font("Arial", fs, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush w = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                rt = new RectangleF(m, px * 0.16f, side, px * 0.46f);
                g.DrawString("SRT", f, w, rt, sf);
            }
            bh = px >= 256 ? px * 0.08f : px * 0.09f;
            using (SolidBrush w2 = new SolidBrush(Color.White))
            {
                g.FillPath(w2, RoundedRect(new RectangleF(px * 0.29f, px * 0.70f, px * 0.42f, bh), bh * 0.5f));
                g.FillPath(w2, RoundedRect(new RectangleF(px * 0.16f, px * 0.81f, px * 0.68f, bh), bh * 0.5f));
            }
        }
        else
        {
            // 16/32 小尺寸: 无文字, 仅双条 (上短下长, 加粗); 整体上移, 上条垂直居中
            float y1 = px >= 32 ? px * 0.46f : px * 0.44f;
            float y2 = px >= 32 ? px * 0.66f : px * 0.64f;
            float bh2 = px * 0.13f;
            if (px < 32) bh2 = px * 0.15f;
            using (SolidBrush w2 = new SolidBrush(Color.White))
            {
                g.FillPath(w2, RoundedRect(new RectangleF(px * 0.28f, y1, px * 0.44f, bh2), bh2 * 0.35f));
                g.FillPath(w2, RoundedRect(new RectangleF(px * 0.15f, y2, px * 0.70f, bh2), bh2 * 0.35f));
            }
        }
    }

    // PNG-in-ICO 封装: 16/32/48 用 BMP 条目 + 256 用 PNG 条目 (System.Drawing 与 shell 全兼容)
    static void BuildIco(string path)
    {
        int[] sizes = new int[] { 16, 32, 48, 256 };
        byte[][] datas = new byte[sizes.Length][];
        int count = sizes.Length;
        int i;
        for (i = 0; i < count; i++)
        {
            using (Bitmap bmp = new Bitmap(sizes[i], sizes[i]))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                DrawDTo(g, sizes[i]);
                datas[i] = BmpIconData(bmp);
            }
        }
        using (FileStream fs = new FileStream(path, FileMode.Create))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);                 // reserved
            bw.Write((short)1);                 // type = icon
            bw.Write((short)count);
            int offset = 6 + 16 * count;
            for (i = 0; i < count; i++)
            {
                int sz = sizes[i];
                bw.Write((byte)(sz >= 256 ? 0 : sz));   // width (0=256)
                bw.Write((byte)(sz >= 256 ? 0 : sz));   // height
                bw.Write((byte)0);                       // color count
                bw.Write((byte)0);                       // reserved
                bw.Write((short)1);                      // planes
                bw.Write((short)32);                     // bit count
                bw.Write(datas[i].Length);
                bw.Write(offset);
                offset += datas[i].Length;
            }
            for (i = 0; i < count; i++) bw.Write(datas[i]);
        }
    }

    // 传统 ICO BMP 条目: BITMAPINFOHEADER + BGRA 像素 (自下而上) + AND 掩码
    static byte[] BmpIconData(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write(40);                    // biSize
            bw.Write(w);                     // biWidth
            bw.Write(h * 2);                 // biHeight (XOR+AND)
            bw.Write((short)1);              // biPlanes
            bw.Write((short)32);             // biBitCount
            bw.Write(0);                     // biCompression
            bw.Write(w * h * 4);             // biSizeImage
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            int x, y;
            for (y = h - 1; y >= 0; y--)
                for (x = 0; x < w; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                }
            int andStride = ((w + 31) / 32) * 4;
            for (y = 0; y < h; y++) for (x = 0; x < andStride; x++) bw.Write((byte)0);
            return ms.ToArray();
        }
    }
}
