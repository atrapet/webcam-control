using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WebcamControl
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        [STAThread]
        static int Main(string[] args)
        {
            List<string> a = new List<string>(args);

            if (Has(a, "--help") || Has(a, "-h") || Has(a, "/?")) return Console_(Usage);
            if (Has(a, "--list")) return Console_(ListAll);

            int applyAt = IndexOf(a, "--apply");
            if (applyAt >= 0)
            {
                string name = applyAt + 1 < a.Count ? a[applyAt + 1] : null;
                return Console_(delegate { return ApplyProfile(name); });
            }

            // ----- mode interface -----
            bool hidden = Has(a, "--tray");

            bool fresh;
            using (Mutex single = new Mutex(true, "Local\\WebcamControl.SingleInstance", out fresh))
            {
                if (!fresh)
                {
                    MessageBox.Show("Webcam Control tourne deja (zone de notification).", "Webcam Control",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log.Write("Exception interface : " + e.Exception);
                    MessageBox.Show("Erreur inattendue :" + Environment.NewLine + e.Exception.Message
                        + Environment.NewLine + Environment.NewLine + "Details dans " + Log.Path_,
                        "Webcam Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };

                Application.Run(new MainForm(hidden));
                GC.KeepAlive(single);
            }
            return 0;
        }

        // Rattache la sortie a la console appelante pour les modes ligne de commande.
        static int Console_(Func<int> body)
        {
            AttachConsole(-1);
            Console.WriteLine();
            int rc = body();
            Console.WriteLine();
            return rc;
        }

        static int Usage()
        {
            Console.WriteLine("Webcam Control - pilotage des reglages UVC d'une webcam");
            Console.WriteLine();
            Console.WriteLine("  WebcamControl.exe                  ouvre le panneau");
            Console.WriteLine("  WebcamControl.exe --tray           demarre reduit dans la zone de notification");
            Console.WriteLine("  WebcamControl.exe --list           liste les cameras et les profils");
            Console.WriteLine("  WebcamControl.exe --apply <profil> applique un profil puis rend la main");
            Console.WriteLine();
            Console.WriteLine("Configuration : " + Store.ConfigPath);
            return 0;
        }

        static int ListAll()
        {
            Console.WriteLine("Cameras detectees :");
            List<string> devices = CameraSession.ListDevices();
            if (devices.Count == 0) Console.WriteLine("  (aucune)");
            foreach (string d in devices) Console.WriteLine("  - " + d);

            ConfigDto cfg = Store.Load();
            Console.WriteLine();
            Console.WriteLine("Camera ciblee : " + cfg.Device);
            Console.WriteLine("Profils :");
            if (cfg.Profiles.Count == 0) Console.WriteLine("  (aucun, lance le panneau une premiere fois)");
            foreach (ProfileDto p in cfg.Profiles)
            {
                bool active = string.Equals(p.Name, cfg.ActiveProfile, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine("  " + (active ? "* " : "  ") + p.Name + "  (" + p.Settings.Count + " reglages)");
            }
            return 0;
        }

        static int ApplyProfile(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                Console.WriteLine("Il manque le nom du profil : --apply \"Visio jour\"");
                return 2;
            }

            ConfigDto cfg = Store.Load();
            ProfileDto profile = cfg.Find(name);
            if (profile == null)
            {
                Console.WriteLine("Profil introuvable : " + name);
                return 3;
            }

            using (CameraSession s = CameraSession.Open(cfg.Device))
            {
                if (s == null)
                {
                    Console.WriteLine("Camera introuvable : " + cfg.Device);
                    return 4;
                }

                List<ControlDef> defs = s.Discover();
                Dictionary<string, ControlState> want = profile.ToStates();
                int ok = 0, ko = 0;
                foreach (ControlDef d in defs)
                {
                    ControlState st;
                    if (!want.TryGetValue(d.Key, out st)) continue;
                    if (s.Write(d, st)) ok++; else ko++;
                }
                Console.WriteLine("Profil \"" + profile.Name + "\" applique sur " + s.DeviceName
                                  + " : " + ok + " reglages" + (ko > 0 ? ", " + ko + " en echec" : ""));

                cfg.ActiveProfile = profile.Name;
                Store.Save(cfg);
                return ko > 0 ? 1 : 0;
            }
        }

        static bool Has(List<string> a, string flag)
        {
            return IndexOf(a, flag) >= 0;
        }

        static int IndexOf(List<string> a, string flag)
        {
            for (int i = 0; i < a.Count; i++)
                if (string.Equals(a[i], flag, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }
}
