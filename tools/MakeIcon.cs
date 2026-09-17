using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Genere assets/app.ico, l'icone embarquee dans l'executable.
//
// Le fichier est reconstruit par build.ps1 s'il manque, ce qui evite d'avoir a
// versionner un binaire opaque : le dessin ci-dessous en est la source.
// usage: MakeIcon.exe <chemin du .ico>
static class MakeIcon
{
    static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    static Bitmap Draw(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Proportions exprimees en fraction de la taille, pour rester net
            // a toutes les resolutions.
            RectangleF body = Frac(size, 0.02f, 0.02f, 0.96f, 0.96f);
            RectangleF ring = Frac(size, 0.20f, 0.20f, 0.60f, 0.60f);
            RectangleF lens = Frac(size, 0.33f, 0.33f, 0.34f, 0.34f);
            RectangleF glint = Frac(size, 0.40f, 0.37f, 0.14f, 0.14f);

            using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 44, 52)))
                g.FillEllipse(b, body);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 170, 255)))
                g.FillEllipse(b, ring);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(20, 24, 32)))
                g.FillEllipse(b, lens);
            if (size >= 24)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    g.FillEllipse(b, glint);
        }
        return bmp;
    }

    static RectangleF Frac(int size, float x, float y, float w, float h)
    {
        return new RectangleF(size * x, size * y, size * w, size * h);
    }

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: MakeIcon.exe <chemin du .ico>");
            return 2;
        }

        List<byte[]> images = new List<byte[]>();
        foreach (int s in Sizes)
        {
            using (Bitmap bmp = Draw(s))
            using (MemoryStream ms = new MemoryStream())
            {
                // Entrees PNG : acceptees par Windows a toutes les tailles depuis Vista.
                bmp.Save(ms, ImageFormat.Png);
                images.Add(ms.ToArray());
            }
        }

        string path = args[0];
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((short)0);                  // reserve
            w.Write((short)1);                  // type : 1 = icone
            w.Write((short)Sizes.Length);

            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));   // 0 signifie 256
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);                    // couleurs de la palette
                w.Write((byte)0);                    // reserve
                w.Write((short)1);                   // plans
                w.Write((short)32);                  // bits par pixel
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] png in images) w.Write(png);
        }

        Console.WriteLine("icone ecrite : " + Path.GetFullPath(path) + " (" + Sizes.Length + " tailles)");
        return 0;
    }
}
