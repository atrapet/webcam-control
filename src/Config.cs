using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Win32;

namespace WebcamControl
{
    [DataContract]
    public class SettingDto
    {
        [DataMember(Order = 0)] public string Key;
        [DataMember(Order = 1)] public int Value;
        [DataMember(Order = 2)] public bool Auto;
    }

    [DataContract]
    public class SoftAeDto
    {
        [DataMember(Order = 0)] public bool Enabled;
        [DataMember(Order = 1)] public int TargetLuma;
        [DataMember(Order = 2)] public int Deadband;
        [DataMember(Order = 3)] public int GainMin;
        [DataMember(Order = 4)] public int GainMax;
        [DataMember(Order = 5)] public int ExposureMin;
        [DataMember(Order = 6)] public int ExposureMax;
        [DataMember(Order = 7)] public int IdleIntervalSeconds;
        [DataMember(Order = 8)] public int LiveStepMax;
        [DataMember(Order = 9)] public int IdleStepMax;
        // Nullable a dessein : un profil enregistre avant l'apparition de ce
        // reglage doit hériter du defaut, pas de false.
        [DataMember(Order = 10)] public bool? MeterWhenIdle;

        public bool MeterIdle { get { return MeterWhenIdle ?? true; } }

        public static SoftAeDto Default()
        {
            SoftAeDto d = new SoftAeDto();
            d.Enabled = false;
            d.TargetLuma = 110;          // sur 255, mesure ponderee vers le centre
            d.Deadband = 6;              // en dessous, l'oeil ne voit rien : on ne bouge pas
            d.GainMin = 5;
            d.GainMax = 75;              // au dela le bruit devient visible
            d.ExposureMin = -9;
            d.ExposureMax = -5;          // -4 et au dela saturent et font chuter la cadence
            d.IdleIntervalSeconds = 300;
            // Mesurer camera libre garde l'exposition juste tout au long de la
            // journee, au prix d'environ trois secondes d'occupation par cycle.
            d.MeterWhenIdle = true;
            d.LiveStepMax = 1;           // apercu ouvert : un cran, soit environ 2 %
            d.IdleStepMax = 8;           // personne ne regarde : on peut converger vite
            return d;
        }

        public SoftAeDto Clone()
        {
            return (SoftAeDto)MemberwiseClone();
        }

        public void Sanitize()
        {
            if (TargetLuma < 20 || TargetLuma > 230) TargetLuma = 110;
            if (Deadband < 1 || Deadband > 60) Deadband = 6;
            if (GainMin < 0) GainMin = 0;
            if (GainMax <= GainMin) { GainMin = 5; GainMax = 75; }
            if (ExposureMax < ExposureMin) { ExposureMin = -9; ExposureMax = -5; }
            if (IdleIntervalSeconds < 15) IdleIntervalSeconds = 300;
            if (LiveStepMax < 1) LiveStepMax = 1;
            if (IdleStepMax < 1) IdleStepMax = 8;
            if (MeterWhenIdle == null) MeterWhenIdle = true;
        }
    }

    [DataContract]
    public class ProfileDto
    {
        [DataMember(Order = 0)] public string Name;
        [DataMember(Order = 1)] public List<SettingDto> Settings;
        [DataMember(Order = 2)] public SoftAeDto SoftAe;

        public ProfileDto() { Settings = new List<SettingDto>(); }

        public SoftAeDto EffectiveSoftAe()
        {
            if (SoftAe == null) return SoftAeDto.Default();
            SoftAe.Sanitize();
            return SoftAe;
        }

        public Dictionary<string, ControlState> ToStates()
        {
            Dictionary<string, ControlState> d = new Dictionary<string, ControlState>();
            if (Settings == null) return d;
            foreach (SettingDto s in Settings)
                if (s != null && s.Key != null) d[s.Key] = new ControlState(s.Value, s.Auto);
            return d;
        }

        public static ProfileDto From(string name, Dictionary<string, ControlState> states)
        {
            return From(name, states, null);
        }

        public static ProfileDto From(string name, Dictionary<string, ControlState> states, SoftAeDto softAe)
        {
            ProfileDto p = new ProfileDto();
            p.Name = name;
            p.SoftAe = softAe != null ? softAe.Clone() : null;
            foreach (KeyValuePair<string, ControlState> kv in states)
            {
                SettingDto s = new SettingDto();
                s.Key = kv.Key; s.Value = kv.Value.Value; s.Auto = kv.Value.Auto;
                p.Settings.Add(s);
            }
            return p;
        }
    }

    [DataContract]
    public class ConfigDto
    {
        [DataMember(Order = 0)] public string Device;
        [DataMember(Order = 1)] public string ActiveProfile;
        [DataMember(Order = 2)] public bool WatchdogEnabled;
        [DataMember(Order = 3)] public int WatchdogSeconds;
        [DataMember(Order = 4)] public string FfmpegPath;
        [DataMember(Order = 5)] public int PreviewWidth;
        [DataMember(Order = 6)] public int PreviewHeight;
        [DataMember(Order = 7)] public int PreviewFps;
        [DataMember(Order = 8)] public bool ShowThirds;
        [DataMember(Order = 9)] public List<ProfileDto> Profiles;

        public ConfigDto()
        {
            Device = "NexiGo";
            WatchdogEnabled = true;
            WatchdogSeconds = 5;
            FfmpegPath = "ffmpeg";
            PreviewWidth = 640;
            PreviewHeight = 480;
            PreviewFps = 15;
            ShowThirds = false;
            Profiles = new List<ProfileDto>();
        }

        public ProfileDto Find(string name)
        {
            if (name == null) return null;
            foreach (ProfileDto p in Profiles)
                if (p != null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        public void Upsert(ProfileDto profile)
        {
            for (int i = 0; i < Profiles.Count; i++)
            {
                if (string.Equals(Profiles[i].Name, profile.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Profiles[i] = profile;
                    return;
                }
            }
            Profiles.Add(profile);
        }

        public void Remove(string name)
        {
            for (int i = Profiles.Count - 1; i >= 0; i--)
                if (string.Equals(Profiles[i].Name, name, StringComparison.OrdinalIgnoreCase)) Profiles.RemoveAt(i);
        }
    }

    public static class Store
    {
        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebcamControl");
            }
        }

        public static string ConfigPath { get { return Path.Combine(Dir, "config.json"); } }

        public static ConfigDto Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return new ConfigDto();
                byte[] raw = File.ReadAllBytes(ConfigPath);
                if (raw.Length == 0) return new ConfigDto();
                using (MemoryStream ms = new MemoryStream(raw))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(ConfigDto));
                    ConfigDto c = ser.ReadObject(ms) as ConfigDto;
                    if (c == null) return new ConfigDto();
                    if (c.Profiles == null) c.Profiles = new List<ProfileDto>();
                    if (c.WatchdogSeconds < 1) c.WatchdogSeconds = 5;
                    if (string.IsNullOrEmpty(c.FfmpegPath)) c.FfmpegPath = "ffmpeg";
                    if (c.PreviewWidth < 160) { c.PreviewWidth = 640; c.PreviewHeight = 480; }
                    if (c.PreviewFps < 1) c.PreviewFps = 15;
                    return c;
                }
            }
            catch
            {
                return new ConfigDto();
            }
        }

        public static void Save(ConfigDto cfg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                using (MemoryStream ms = new MemoryStream())
                {
                    // JSON indente : le fichier reste modifiable a la main
                    using (System.Xml.XmlDictionaryWriter w =
                        JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                    {
                        DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(ConfigDto));
                        ser.WriteObject(w, cfg);
                        w.Flush();
                    }
                    File.WriteAllBytes(ConfigPath, ms.ToArray());
                }
            }
            catch (Exception ex)
            {
                Log.Write("Sauvegarde config impossible: " + ex.Message);
            }
        }
    }

    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "WebcamControl";

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                        return k != null && k.GetValue(ValueName) != null;
                }
                catch { return false; }
            }
        }

        public static void Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on)
                    {
                        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        k.SetValue(ValueName, "\"" + exe + "\" --tray");
                    }
                    else if (k.GetValue(ValueName) != null)
                    {
                        k.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Demarrage automatique: " + ex.Message);
            }
        }
    }

    public static class Log
    {
        static readonly object gate = new object();

        public static string Path_ { get { return System.IO.Path.Combine(Store.Dir, "webcam-control.log"); } }

        public static void Write(string line)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Store.Dir);
                    File.AppendAllText(Path_,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
