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
        volatile int framesEmitted;
        readonly StringBuilder stderr = new StringBuilder();

        // Parametres du dernier demarrage, pour pouvoir retenter sans format impose.
        string lastFfmpeg, lastDevice;
        int lastWidth, lastHeight, lastFps;
        bool triedFallback;

        public bool Running { get { return proc != null && !proc.HasExited; } }

        public void Start(string ffmpeg, string device, int width, int height, int fps)
        {
            lastFfmpeg = ffmpeg; lastDevice = device;
            lastWidth = width; lastHeight = height; lastFps = fps;
            triedFallback = false;
            StartInternal(false);
        }

        void StartInternal(bool fallback)
        {
            Stop();
            stopping = false;
            framesEmitted = 0;
            stderr.Length = 0;

            // Un format impose que la camera refuse fait echouer l'ouverture ; le repli
            // laisse ffmpeg choisir ce que le peripherique propose par defaut.
            string format = fallback
                ? ""
                : "-vcodec mjpeg -video_size " + lastWidth + "x" + lastHeight + " -framerate " + lastFps + " ";

            string ffmpeg = lastFfmpeg, device = lastDevice;
            // stdin reste ouvert : envoyer "q" laisse ffmpeg fermer proprement la
            // camera, ce qu'un Kill ne fait pas. Sans cela Windows garde le
            // peripherique marque comme utilise et l'auto-exposition se croit bloquee.
            string args =
                "-hide_banner -loglevel error " +
                "-f dshow -rtbufsize 32M " + format +
                "-i video=\"" + device + "\" " +
                "-c:v mjpeg -q:v 6 -f mjpeg -";
            if (!fallback) args = args.Replace("-c:v mjpeg -q:v 6", "-c:v copy");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = ffmpeg;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;

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

            if (stopping) return;

            string err;
            lock (stderr) { err = stderr.ToString().Trim(); }

            // Aucune image recue : le format demande n'a probablement pas ete accepte.
            // On retente une fois en laissant la camera imposer le sien.
            if (framesEmitted == 0 && !triedFallback)
            {
                triedFallback = true;
                try { StartInternal(true); return; }
                catch { }
            }

            Raise(err.Length > 0 ? err : "ffmpeg s'est arrete");
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
                framesEmitted++;
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
                bool gone = false;
                try
                {
                    if (p.HasExited) gone = true;
                    else
                    {
                        // Arret propre d'abord : ffmpeg quitte sur "q" et relache la camera.
                        try { p.StandardInput.Write("q"); p.StandardInput.Flush(); }
                        catch { }
                        gone = p.WaitForExit(600);
                    }
                }
                catch { }

                if (!gone)
                {
                    try { p.Kill(); gone = p.WaitForExit(1500); }
                    catch { }
                }
                if (!gone) Log.Write("ffmpeg n'a pas pu etre arrete, la camera peut rester marquee occupee");

                try { p.Dispose(); }
                catch { }
            }
            Thread t = reader;
            reader = null;
            // Le repli appelle Stop depuis le thread de lecture lui-meme : ne pas
            // l'attendre dans ce cas, sinon il s'attend indefiniment.
            if (t != null && t != Thread.CurrentThread && t.IsAlive) { try { t.Join(500); } catch { } }
        }

        public void Dispose() { Stop(); }
    }
}
