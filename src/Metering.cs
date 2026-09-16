using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WebcamControl
{
    // Mesure de la luminance moyenne d'une image, ponderee vers le centre :
    // c'est le visage qui doit etre correctement expose, pas le mur du fond.
    public static class Metering
    {
        public static double Luma(Bitmap bmp)
        {
            if (bmp == null) return -1;

            Rectangle all = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = null;
            try
            {
                data = bmp.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            }
            catch
            {
                return -1;
            }

            try
            {
                int w = data.Width, h = data.Height, stride = data.Stride;
                // Zone centrale : moitie de la largeur et de la hauteur, comptee double.
                int cx0 = w / 4, cx1 = w - w / 4, cy0 = h / 4, cy1 = h - h / 4;
                const int stepX = 4, stepY = 4;   // echantillonnage : 1 pixel sur 16

                double sum = 0, weight = 0;
                byte[] row = new byte[stride];

                for (int y = 0; y < h; y += stepY)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), row, 0, stride);
                    bool centerRow = y >= cy0 && y < cy1;
                    for (int x = 0; x < w; x += stepX)
                    {
                        int i = x * 3;
                        // LockBits en 24bpp fournit les octets dans l'ordre B, G, R
                        double lum = 0.114 * row[i] + 0.587 * row[i + 1] + 0.299 * row[i + 2];
                        double wgt = (centerRow && x >= cx0 && x < cx1) ? 2.0 : 1.0;
                        sum += lum * wgt;
                        weight += wgt;
                    }
                }
                return weight > 0 ? sum / weight : -1;
            }
            catch
            {
                return -1;
            }
            finally
            {
                try { bmp.UnlockBits(data); } catch { }
            }
        }
    }

    // Qui utilise la camera en ce moment ?
    //
    // Windows tient ce registre a jour pour l'indicateur de confidentialite :
    // LastUsedTimeStop == 0 signifie que l'application a ouvert la camera et ne
    // l'a pas encore relachee. On evite ainsi d'ouvrir le peripherique pour le
    // savoir, ce qui reviendrait a le bloquer.
    public static class CameraUsage
    {
        const string Base = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

        // Renvoie le nom de l'application qui tient la camera, ou null si elle est libre.
        // L'executable de cette application est ignore : on ne veut pas se detecter soi-meme.
        public static string InUseBy() { return InUseBy(null); }

        // <paramref name="alsoIgnore"/> sert a ne pas se confondre avec le ffmpeg que
        // l'on lance soi-meme pour mesurer : Windows l'enregistre sous son propre nom.
        // Contrepartie assumee : un ffmpeg lance par ailleurs ne sera pas detecte.
        public static string InUseBy(string alsoIgnore)
        {
            string self = SelfExeKeyFragment();
            string found = null;
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(Base, false))
                {
                    if (root == null) return null;
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        using (RegistryKey k = root.OpenSubKey(sub, false))
                        {
                            if (k == null) continue;
                            // Les applications de bureau sont regroupees sous "NonPackaged"
                            if (string.Equals(sub, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (string sub2 in k.GetSubKeyNames())
                                {
                                    using (RegistryKey k2 = k.OpenSubKey(sub2, false))
                                    {
                                        string hit = Check(k2, sub2, self, alsoIgnore);
                                        if (hit != null) found = hit;
                                    }
                                }
                                continue;
                            }
                            string h = Check(k, sub, self, alsoIgnore);
                            if (h != null) found = h;
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
            return found;
        }

        static string Check(RegistryKey k, string name, string self, string alsoIgnore)
        {
            if (k == null) return null;
            object start = k.GetValue("LastUsedTimeStart");
            object stop = k.GetValue("LastUsedTimeStop");
            if (start == null || stop == null) return null;
            if (Convert.ToInt64(stop) != 0) return null;              // relachee
            if (Convert.ToInt64(start) == 0) return null;
            string pretty = Pretty(name);
            if (Matches(pretty, self)) return null;
            if (Matches(pretty, alsoIgnore)) return null;
            return pretty;
        }

        static bool Matches(string pretty, string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            return pretty.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Les chemins sont stockes avec '#' a la place des separateurs.
        static string Pretty(string keyName)
        {
            string s = keyName.Replace('#', '\\');
            int i = s.LastIndexOf('\\');
            return i >= 0 && i < s.Length - 1 ? s.Substring(i + 1) : s;
        }

        static string SelfExeKeyFragment()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return System.IO.Path.GetFileName(exe);
            }
            catch { return null; }
        }
    }
}
