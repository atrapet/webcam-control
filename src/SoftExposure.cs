using System;
using System.Drawing;
using System.Windows.Forms;

namespace WebcamControl
{
    // Auto-exposition logicielle.
    //
    // Contraintes materielles mesurees sur la N60, qui dictent entierement ce
    // fonctionnement :
    //   - le flux video est exclusif, meme via le Frame Server de Windows : on
    //     ne peut pas mesurer la lumiere pendant qu'une autre application filme ;
    //   - en mode automatique, la valeur d'exposition relue est une valeur morte,
    //     elle ne renseigne donc pas sur la scene ;
    //   - l'exposition n'a que six crans exploitables, espaces d'environ 20 % de
    //     luminance, alors que le gain offre cent crans couvrant toute la plage.
    //
    // D'ou la strategie : l'exposition et le gain restent en manuel en
    // permanence, donc le firmware ne provoque plus aucun saut. La correction se
    // fait au gain, et l'exposition ne bouge qu'en butee. La mesure n'a lieu que
    // lorsque la camera est libre, ou depuis l'apercu quand il est ouvert.
    //
    // Vitesse de convergence : rapide quand personne ne regarde, tres lente quand
    // l'apercu est ouvert, nulle pendant qu'une autre application filme.
    public class SoftAe
    {
        // Variation de luminance obtenue pour un cran de gain, au milieu de la
        // plage. Releve sur la N60 : le gain 20 -> 40 fait passer YAVG de 37 a 92.
        const double LumaPerGainUnit = 2.5;
        const double Kp = 0.25;             // correction partielle : on converge sans osciller

        public SoftAeDto Settings = SoftAeDto.Default();

        // Dependances fournies par le formulaire. Tout est appele sur le thread interface.
        // WithCamera ouvre la camera, execute l'action, puis la relache aussitot :
        // la garder ouverte empecherait le navigateur d'y acceder.
        public Action<Action<CameraSession>> WithCamera;
        public Func<bool> CameraKnown;
        public Func<ControlDef> GetExposureDef;
        public Func<ControlDef> GetGainDef;
        public Func<bool> PreviewRunning;
        public Func<double> PreviewLuma;        // < 0 si aucune mesure fraiche
        public Func<string> GetFfmpegPath;
        public Func<string> GetDeviceName;
        public Action<int, int> Applied;        // (exposition, gain) -> rafraichit l'interface
        public Action<string> Status;
        public Action<string> Trace;            // journal des corrections
        public Control Ui;                      // pour repasser sur le thread interface

        int currentExposure, currentGain;
        bool primed;
        bool lastSeenInUse;
        DateTime lastPass = DateTime.MinValue;
        DateTime freeSince = DateTime.MinValue;
        int settled;

        // Delai de courtoisie apres qu'une autre application a relache la camera.
        const int GraceAfterReleaseSeconds = 20;

        // Duree maximale d'occupation de la camera pour une mesure. Chaque seconde
        // compte : c'est autant de temps pendant lequel une autre application se
        // verrait refuser l'acces.
        const int PassSeconds = 4;

        PreviewStream pass;
        int passSamples;
        DateTime passStarted;
        DateTime lastSampleAt;

        public bool PassRunning { get { return pass != null; } }

        public void Reset() { primed = false; settled = 0; }

        // Force l'exposition et le gain en manuel et memorise le point de depart.
        public void Prime()
        {
            ControlDef e = GetExposureDef != null ? GetExposureDef() : null;
            ControlDef g = GetGainDef != null ? GetGainDef() : null;
            if (WithCamera == null || g == null) return;

            bool done = false;
            WithCamera(delegate(CameraSession s)
            {
                ControlState st;
                currentGain = (Settings.GainMin + Settings.GainMax) / 2;
                if (s.TryRead(g, out st)) currentGain = Clamp(st.Value, Settings.GainMin, Settings.GainMax);
                s.Write(g, new ControlState(currentGain, false));

                if (e != null)
                {
                    currentExposure = (Settings.ExposureMin + Settings.ExposureMax) / 2;
                    if (s.TryRead(e, out st) && !st.Auto)
                        currentExposure = Clamp(st.Value, Settings.ExposureMin, Settings.ExposureMax);
                    s.Write(e, new ControlState(currentExposure, false));   // jamais d'auto materiel
                }
                done = true;
            });
            if (!done) return;

            primed = true;
            settled = 0;
            if (Applied != null) Applied(currentExposure, currentGain);
        }

        // Appelee une fois par seconde par le formulaire.
        public void Tick()
        {
            if (!Settings.Enabled) return;
            if (WithCamera == null) return;
            if (CameraKnown != null && !CameraKnown()) return;
            if (!primed) Prime();

            bool ourPreview = PreviewRunning != null && PreviewRunning();
            // Pendant une passe, c'est notre propre ffmpeg qui tient la camera et que
            // Windows enregistre sous son nom : ne pas l'exclure reviendrait a se
            // prendre soi-meme pour une application concurrente et a s'interrompre.
            string otherApp = (pass != null || ourPreview) ? null : CameraUsage.InUseBy(FfmpegLeafName());

            // Une autre application filme : on ne mesure pas et on ne touche a rien.
            if (otherApp != null && !ourPreview)
            {
                if (pass != null) StopPass();
                lastSeenInUse = true;
                Report("en pause : " + otherApp + " utilise la camera");
                return;
            }

            // La camera vient de se liberer. On ne mesure surtout pas tout de suite :
            // quitter une visioconference et en ouvrir une autre dans la foulee est
            // courant, et une mesure a cet instant refuserait la camera a l'application
            // suivante. On laisse donc passer un delai de courtoisie.
            if (lastSeenInUse)
            {
                lastSeenInUse = false;
                freeSince = DateTime.Now;
            }
            bool graceElapsed = (DateTime.Now - freeSince).TotalSeconds >= GraceAfterReleaseSeconds;

            if (ourPreview)
            {
                if (pass != null) StopPass();       // l'apercu fournit deja des images
                double y = PreviewLuma != null ? PreviewLuma() : -1;
                if (y >= 0) Step(y, Settings.LiveStepMax, true);
                return;
            }

            if (pass != null) { WatchPass(); return; }

            if (!Settings.MeterIdle) { Report("en veille : mesure seulement avec l'apercu ouvert"); return; }
            if (!graceElapsed) return;
            if ((DateTime.Now - lastPass).TotalSeconds >= Settings.IdleIntervalSeconds) StartPass();
        }

        public void MeasureNow()
        {
            if (PreviewRunning != null && PreviewRunning())
            {
                double y = PreviewLuma != null ? PreviewLuma() : -1;
                if (y >= 0) Step(y, Settings.IdleStepMax, false);
                return;
            }
            if (pass == null) StartPass();
        }

        // ---------------- passe de mesure sur camera libre ----------------

        void StartPass()
        {
            string dev = GetDeviceName != null ? GetDeviceName() : null;
            string ff = GetFfmpegPath != null ? GetFfmpegPath() : "ffmpeg";
            if (string.IsNullOrEmpty(dev)) return;

            // Dernier controle juste avant d'ouvrir, pour ne pas voler la camera.
            if (CameraUsage.InUseBy(FfmpegLeafName()) != null) return;

            passSamples = 0;
            passStarted = DateTime.Now;
            lastSampleAt = DateTime.MinValue;
            settled = 0;

            pass = new PreviewStream();
            pass.FrameReady += OnPassFrame;
            pass.Stopped += OnPassStopped;
            // 15 i/s : cadence minimale acceptee par la N60 en 320x240. La duree
            // limite fait sortir ffmpeg tout seul, ce qui rend la camera proprement
            // meme si l'arret explicite echoue.
            pass.Start(ff, dev, 320, 240, 15, PassSeconds);
            Report("mesure en cours...");
            if (Trace != null) Trace("AE passe de mesure demarree");
        }

        void OnPassFrame(Bitmap bmp)
        {
            double y;
            try { y = Metering.Luma(bmp); }
            finally { bmp.Dispose(); }
            if (y < 0) return;

            // On laisse au capteur le temps d'appliquer la correction precedente.
            if ((DateTime.Now - lastSampleAt).TotalMilliseconds < 350) return;
            lastSampleAt = DateTime.Now;

            if (Ui == null || Ui.IsDisposed || !Ui.IsHandleCreated) return;
            try { Ui.BeginInvoke(new Action<double>(OnPassSample), y); }
            catch { }
        }

        void OnPassSample(double luma)
        {
            if (pass == null) return;
            passSamples++;
            Step(luma, Settings.IdleStepMax, false);
            if (settled >= 2 || passSamples >= 6) StopPass();
        }

        void WatchPass()
        {
            // Pendant une passe, la camera est reservee : toute autre application qui
            // la demande se voit refuser. On abrege donc au moindre signe de
            // concurrence, et on borne la duree dans tous les cas.
            if (CameraUsage.InUseBy(FfmpegLeafName()) != null)
            {
                if (Trace != null) Trace("AE mesure abregee : une autre application demande la camera");
                StopPass();
                return;
            }
            if ((DateTime.Now - passStarted).TotalSeconds > 3) StopPass();
        }

        void OnPassStopped(string message)
        {
            if (Ui == null || Ui.IsDisposed || !Ui.IsHandleCreated) return;
            try
            {
                Ui.BeginInvoke(new Action(delegate
                {
                    if (pass == null) return;
                    // Echec d'ouverture : une application a pris la camera entre-temps.
                    if (passSamples == 0 && !string.IsNullOrEmpty(message))
                    {
                        Report("mesure impossible, camera occupee");
                        if (Trace != null) Trace("AE mesure impossible : " + message.Replace(Environment.NewLine, " "));
                    }
                    StopPass();
                }));
            }
            catch { }
        }

        void StopPass()
        {
            PreviewStream p = pass;
            pass = null;
            if (Trace != null) Trace("AE passe terminee apres " + passSamples + " mesures");

            // Une passe sans aucune mesure signifie que la camera etait momentanement
            // prise : on retente bien avant l'intervalle normal.
            lastPass = passSamples > 0
                ? DateTime.Now
                : DateTime.Now.AddSeconds(-Math.Max(0, Settings.IdleIntervalSeconds - 20));
            if (p != null)
            {
                p.FrameReady -= OnPassFrame;
                p.Stopped -= OnPassStopped;
                // Liberation synchrone, volontairement. Passer par le pool de threads
                // laissait le processus de mesure survivre, et tant qu'il vit la camera
                // reste reservee : plus aucune autre application ne peut l'ouvrir.
                try { p.Dispose(); }
                catch (Exception ex) { if (Trace != null) Trace("AE liberation du flux : " + ex.Message); }
            }
        }

        // ---------------- loi de commande ----------------

        // Renvoie true si une correction a ete appliquee.
        bool Step(double luma, int maxStep, bool live)
        {
            ControlDef g = GetGainDef != null ? GetGainDef() : null;
            if (WithCamera == null || g == null) return false;
            if (!primed) Prime();

            double error = Settings.TargetLuma - luma;
            if (Math.Abs(error) <= Settings.Deadband)
            {
                settled++;
                Report(string.Format("stable - luminance {0:0} pour une cible de {1}", luma, Settings.TargetLuma));
                return false;
            }
            settled = 0;

            int step = (int)Math.Round(error * Kp / LumaPerGainUnit);
            if (step == 0) step = error > 0 ? 1 : -1;
            step = Clamp(step, -maxStep, maxStep);

            int wanted = Clamp(currentGain + step, Settings.GainMin, Settings.GainMax);
            if (wanted != currentGain)
            {
                currentGain = wanted;
                int g2 = currentGain;
                WithCamera(delegate(CameraSession s) { s.Write(g, new ControlState(g2, false)); });
            }
            else
            {
                // Le gain est en butee : on deplace l'exposition d'un cran et on
                // recentre le gain, ce qui garde la luminosite a peu pres continue.
                ControlDef e = GetExposureDef != null ? GetExposureDef() : null;
                if (e == null) { Report("butee de gain atteinte"); return false; }

                int lo = Math.Max(Settings.ExposureMin, e.Min);
                int hi = Math.Min(Settings.ExposureMax, e.Max);
                int newExposure = Clamp(currentExposure + (error > 0 ? 1 : -1), lo, hi);
                if (newExposure == currentExposure)
                {
                    Report(luma < Settings.TargetLuma
                        ? "scene trop sombre pour les bornes fixees"
                        : "scene trop claire pour les bornes fixees");
                    return false;
                }
                currentExposure = newExposure;
                currentGain = (Settings.GainMin + Settings.GainMax) / 2;
                int e2 = currentExposure, g3 = currentGain;
                WithCamera(delegate(CameraSession s)
                {
                    s.Write(e, new ControlState(e2, false));
                    s.Write(g, new ControlState(g3, false));
                });
            }

            if (Applied != null) Applied(currentExposure, currentGain);
            string msg = string.Format("{0} - luminance {1:0}, cible {2}, gain {3}, exposition {4}",
                live ? "ajustement doux" : "mesure", luma, Settings.TargetLuma, currentGain,
                Catalog.ExposureText(currentExposure));
            Report(msg);
            if (Trace != null) Trace("AE " + msg);
            return true;
        }

        void Report(string text)
        {
            if (Status != null) Status(text);
        }

        string FfmpegLeafName()
        {
            try
            {
                string p = GetFfmpegPath != null ? GetFfmpegPath() : null;
                if (string.IsNullOrEmpty(p)) return "ffmpeg.exe";
                string leaf = System.IO.Path.GetFileName(p);
                if (string.IsNullOrEmpty(leaf)) return "ffmpeg.exe";
                return leaf.IndexOf('.') >= 0 ? leaf : leaf + ".exe";
            }
            catch { return "ffmpeg.exe"; }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
