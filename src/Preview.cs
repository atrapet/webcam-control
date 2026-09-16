using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;

namespace WebcamControl
{
    // Apercu live.
    //
    // On ne monte pas de graphe DirectShow de rendu : on demande a ffmpeg de recopier
    // telles quelles les trames MJPEG de la camera sur sa sortie standard (-c:v copy,
    // donc aucun reencodage) et on decoupe le flux sur les marqueurs JPEG SOI/EOI.
    // C'est leger, ca n'empeche pas une autre application d'ouvrir la camera en meme
    // temps, et ca evite une pile d'interop fragile.
    public class PreviewStream : IDisposable
    {
        public event Action<Bitmap> FrameReady;      // declenche sur le thread de lecture
        public event Action<string> Stopped;         // message non nul si arret anormal

        Process proc;
        Thread reader;
        volatile bool stopping;
        readonly StringBuilder stderr = new StringBuilder();

        public bool Running { get { return proc != null && !proc.HasExited; } }

        public void Start(string ffmpeg, string device, int width, int height, int fps)
        {
            Stop();
            stopping = false;
            stderr.Length = 0;

            string args =
                "-hide_banner -loglevel error -nostdin " +
                "-f dshow -rtbufsize 32M " +
                "-vcodec mjpeg " +
                "-video_size " + width + "x" + height + " " +
                "-framerate " + fps + " " +
                "-i video=\"" + device + "\" " +
                "-c:v copy -f mjpeg -";

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = ffmpeg;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                proc = null;
                Raise("ffmpeg introuvable (" + ffmpeg + ") : " + ex.Message);
                return;
            }

            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null) { lock (stderr) { if (stderr.Length < 4000) stderr.AppendLine(e.Data); } }
            };
            proc.BeginErrorReadLine();

            reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Name = "preview-reader";
            reader.Start();
        }

        void ReadLoop()
        {
            const int SoiEoiOverhead = 4;
            byte[] buf = new byte[1 << 20];
            int len = 0;
            byte[] chunk = new byte[64 * 1024];
            Stream src = proc.StandardOutput.BaseStream;

            try
            {
                while (!stopping)
                {
                    int n = src.Read(chunk, 0, chunk.Length);
                    if (n <= 0) break;

                    if (len + n > buf.Length)
                    {
                        if (len + n > 16 << 20) { len = 0; continue; }     // flux incoherent, on repart a zero
                        Array.Resize(ref buf, Math.Max(buf.Length * 2, len + n));
                    }
                    Buffer.BlockCopy(chunk, 0, buf, len, n);
                    len += n;

                    while (true)
                    {
                        int start = FindMarker(buf, 0, len, 0xD8);
                        if (start < 0)
                        {
                            // rien d'exploitable, on ne garde que le dernier octet (un 0xFF eventuel)
                            if (len > 1) { buf[0] = buf[len - 1]; len = 1; }
                            break;
                        }
                        int end = FindMarker(buf, start + 2, len, 0xD9);
                        if (end < 0)
                        {
                            if (start > 0) { Buffer.BlockCopy(buf, start, buf, 0, len - start); len -= start; }
                            break;
                        }

                        int frameLen = end + 2 - start;
                        if (frameLen > SoiEoiOverhead) Emit(buf, start, frameLen);

                        int consumed = end + 2;
                        Buffer.BlockCopy(buf, consumed, buf, 0, len - consumed);
                        len -= consumed;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!stopping) { Raise("lecture du flux interrompue : " + ex.Message); return; }
            }

            if (!stopping)
            {
                string err;
                lock (stderr) { err = stderr.ToString().Trim(); }
                Raise(err.Length > 0 ? err : "ffmpeg s'est arrete");
            }
        }

        void Emit(byte[] buf, int offset, int count)
        {
            Action<Bitmap> h = FrameReady;
            if (h == null) return;
            try
            {
                Bitmap copy;
                using (MemoryStream ms = new MemoryStream(buf, offset, count, false))
                using (Image img = Image.FromStream(ms, false, false))
                {
                    // on recopie pour ne plus dependre du MemoryStream une fois ferme
                    copy = new Bitmap(img);
                }
                h(copy);
            }
            catch
            {
                // trame tronquee ou corrompue : on l'ignore, la suivante arrive dans 60 ms
            }
        }

        static int FindMarker(byte[] b, int from, int len, byte marker)
        {
            for (int i = Math.Max(from, 0); i < len - 1; i++)
                if (b[i] == 0xFF && b[i + 1] == marker) return i;
            return -1;
        }

        void Raise(string message)
        {
            Action<string> h = Stopped;
            if (h != null) h(message);
        }

        public void Stop()
        {
            stopping = true;
            Process p = proc;
            proc = null;
            if (p != null)
            {
                try { if (!p.HasExited) p.Kill(); }
                catch { }
                try { p.Dispose(); }
                catch { }
            }
            Thread t = reader;
            reader = null;
            if (t != null && t.IsAlive) { try { t.Join(500); } catch { } }
        }

        public void Dispose() { Stop(); }
    }
}
