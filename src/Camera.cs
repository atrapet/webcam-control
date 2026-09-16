using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WebcamControl
{
    // ---------- Interop DirectShow : strictement ce qu'il faut pour piloter un peripherique UVC ----------

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator([In] ref Guid pType, out IEnumMoniker ppEnumMoniker, int dwFlags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name,
                               [MarshalAs(UnmanagedType.Struct)] out object val, IntPtr err);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name,
                                [MarshalAs(UnmanagedType.Struct)] ref object val);
    }

    [ComImport, Guid("C6E13360-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAMVideoProcAmp
    {
        [PreserveSig] int GetRange(int prop, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int prop, int value, int flags);
        [PreserveSig] int Get(int prop, out int value, out int flags);
    }

    [ComImport, Guid("C6E13370-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAMCameraControl
    {
        [PreserveSig] int GetRange(int prop, out int min, out int max, out int step, out int def, out int caps);
        [PreserveSig] int Set(int prop, int value, int flags);
        [PreserveSig] int Get(int prop, out int value, out int flags);
    }

    // ---------- Modele ----------

    public class ControlDef
    {
        public string Key;              // identifiant stable, sert de cle dans les profils
        public string Label;            // libelle affiche
        public string Group;            // regroupement dans l'interface
        public bool IsCameraControl;    // false => IAMVideoProcAmp
        public int Prop;
        public int Min, Max, Step, Default;
        public bool CanAuto, CanManual;
        public Func<int, string> Format;

        public string Show(int v)
        {
            if (Format != null) return Format(v);
            return v.ToString();
        }
    }

    public class ControlState
    {
        public int Value;
        public bool Auto;
        public ControlState() { }
        public ControlState(int v, bool a) { Value = v; Auto = a; }
        public ControlState Clone() { return new ControlState(Value, Auto); }

        public override bool Equals(object o)
        {
            ControlState s = o as ControlState;
            if (s == null) return false;
            // en mode auto la valeur lue derive en permanence, seul le mode compte
            if (Auto && s.Auto) return true;
            return Auto == s.Auto && Value == s.Value;
        }
        public override int GetHashCode() { return Value * 2 + (Auto ? 1 : 0); }
    }

    // ---------- Session : garde le filtre lie, chaque acces coute alors ~1 ms ----------

    public class CameraSession : IDisposable
    {
        const int FlagAuto = 0x0001;
        const int FlagManual = 0x0002;

        static Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        static Guid VideoInputCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        static Guid IID_IBaseFilter = new Guid("56a86895-0ad4-11ce-b03a-0020af0ba770");
        static Guid IID_IPropertyBag = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");

        object filter;
        IAMVideoProcAmp procAmp;
        IAMCameraControl camCtl;

        string deviceName;
        public string DeviceName { get { return deviceName; } }
        public bool IsOpen { get { return filter != null; } }

        CameraSession() { }

        public static List<string> ListDevices()
        {
            List<string> names = new List<string>();
            IEnumMoniker em = Enumerate();
            if (em == null) return names;
            IMoniker[] one = new IMoniker[1];
            while (em.Next(1, one, IntPtr.Zero) == 0)
            {
                string n = FriendlyName(one[0]);
                if (n != null) names.Add(n);
                Marshal.ReleaseComObject(one[0]);
            }
            Marshal.ReleaseComObject(em);
            return names;
        }

        // Ouvre la camera dont le nom contient "match". Si match est vide, prend la premiere.
        public static CameraSession Open(string match)
        {
            IEnumMoniker em = Enumerate();
            if (em == null) return null;
            IMoniker[] one = new IMoniker[1];
            IMoniker first = null;
            string firstName = null;
            try
            {
                while (em.Next(1, one, IntPtr.Zero) == 0)
                {
                    string name = FriendlyName(one[0]);
                    bool hit = !string.IsNullOrEmpty(match) && name != null &&
                               name.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hit)
                    {
                        CameraSession s = Bind(one[0], name);
                        Marshal.ReleaseComObject(one[0]);
                        if (s != null) return s;
                        continue;
                    }
                    if (first == null && name != null) { first = one[0]; firstName = name; continue; }
                    Marshal.ReleaseComObject(one[0]);
                }
                if (string.IsNullOrEmpty(match) && first != null) return Bind(first, firstName);
                return null;
            }
            finally
            {
                if (first != null) Marshal.ReleaseComObject(first);
                Marshal.ReleaseComObject(em);
            }
        }

        static CameraSession Bind(IMoniker mon, string name)
        {
            object f;
            try { mon.BindToObject(null, null, ref IID_IBaseFilter, out f); }
            catch { return null; }
            if (f == null) return null;
            CameraSession s = new CameraSession();
            s.filter = f;
            s.procAmp = f as IAMVideoProcAmp;
            s.camCtl = f as IAMCameraControl;
            s.deviceName = name;
            return s;
        }

        static IEnumMoniker Enumerate()
        {
            Type t = Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum);
            if (t == null) return null;
            ICreateDevEnum de = null;
            try { de = Activator.CreateInstance(t) as ICreateDevEnum; }
            catch { return null; }
            if (de == null) return null;
            IEnumMoniker em;
            int hr = de.CreateClassEnumerator(ref VideoInputCategory, out em, 0);
            Marshal.ReleaseComObject(de);
            return hr == 0 ? em : null;
        }

        static string FriendlyName(IMoniker mon)
        {
            try
            {
                object bag;
                mon.BindToStorage(null, null, ref IID_IPropertyBag, out bag);
                IPropertyBag pb = bag as IPropertyBag;
                if (pb == null) return null;
                object val;
                int hr = pb.Read("FriendlyName", out val, IntPtr.Zero);
                Marshal.ReleaseComObject(bag);
                return hr == 0 && val != null ? val.ToString() : null;
            }
            catch { return null; }
        }

        // ---- lecture / ecriture ----

        bool Range(ControlDef d, out int min, out int max, out int step, out int def, out int caps)
        {
            min = max = step = def = caps = 0;
            try
            {
                if (d.IsCameraControl)
                    return camCtl != null && camCtl.GetRange(d.Prop, out min, out max, out step, out def, out caps) == 0;
                return procAmp != null && procAmp.GetRange(d.Prop, out min, out max, out step, out def, out caps) == 0;
            }
            catch { return false; }
        }

        public bool TryRead(ControlDef d, out ControlState state)
        {
            state = null;
            int v = 0, flags = 0, hr;
            try
            {
                if (d.IsCameraControl)
                {
                    if (camCtl == null) return false;
                    hr = camCtl.Get(d.Prop, out v, out flags);
                }
                else
                {
                    if (procAmp == null) return false;
                    hr = procAmp.Get(d.Prop, out v, out flags);
                }
            }
            catch { return false; }
            if (hr != 0) return false;
            state = new ControlState(v, (flags & FlagAuto) != 0);
            return true;
        }

        public bool Write(ControlDef d, ControlState s)
        {
            int flags = (s.Auto && d.CanAuto) ? FlagAuto : FlagManual;
            int value = s.Value;
            if (value < d.Min) value = d.Min;
            if (value > d.Max) value = d.Max;
            try
            {
                int hr = d.IsCameraControl
                    ? (camCtl == null ? -1 : camCtl.Set(d.Prop, value, flags))
                    : (procAmp == null ? -1 : procAmp.Set(d.Prop, value, flags));
                return hr == 0;
            }
            catch { return false; }
        }

        // Ne garde que les reglages reellement exposes par ce peripherique, avec leurs bornes reelles.
        public List<ControlDef> Discover()
        {
            List<ControlDef> found = new List<ControlDef>();
            foreach (ControlDef c in Catalog.All())
            {
                int min, max, step, def, caps;
                if (!Range(c, out min, out max, out step, out def, out caps)) continue;
                if (max <= min) continue;
                c.Min = min; c.Max = max; c.Step = step < 1 ? 1 : step; c.Default = def;
                c.CanAuto = (caps & FlagAuto) != 0;
                c.CanManual = (caps & FlagManual) != 0;
                found.Add(c);
            }
            return found;
        }

        public void Dispose()
        {
            procAmp = null; camCtl = null;
            if (filter != null) { try { Marshal.ReleaseComObject(filter); } catch { } filter = null; }
        }
    }

    // ---------- Catalogue des proprietes UVC standard ----------

    public static class Catalog
    {
        public const string GroupExposure = "Exposition et couleur";
        public const string GroupImage = "Image";
        public const string GroupFraming = "Cadrage";

        static ControlDef Pa(string key, string label, string group, int prop, Func<int, string> fmt)
        {
            ControlDef d = new ControlDef();
            d.Key = key; d.Label = label; d.Group = group; d.IsCameraControl = false; d.Prop = prop; d.Format = fmt;
            return d;
        }

        static ControlDef Cc(string key, string label, string group, int prop, Func<int, string> fmt)
        {
            ControlDef d = new ControlDef();
            d.Key = key; d.Label = label; d.Group = group; d.IsCameraControl = true; d.Prop = prop; d.Format = fmt;
            return d;
        }

        // Exposition UVC : la valeur n represente 2^n secondes.
        public static string ExposureText(int n)
        {
            double sec = Math.Pow(2, n);
            if (sec >= 1) return string.Format("{0:0.#} s", sec);
            int denom = (int)Math.Round(1.0 / sec);
            return string.Format("1/{0} s", denom);
        }

        public static List<ControlDef> All()
        {
            Func<int, string> plain = null;
            Func<int, string> kelvin = delegate(int v) { return v + " K"; };
            List<ControlDef> l = new List<ControlDef>();
            // l'ordre de cette liste est l'ordre d'affichage
            l.Add(Cc("Exposure", "Exposition", GroupExposure, 4, ExposureText));
            l.Add(Pa("Gain", "Gain", GroupExposure, 9, plain));
            l.Add(Pa("WhiteBalance", "Balance des blancs", GroupExposure, 7, kelvin));
            l.Add(Pa("BacklightCompensation", "Compensation contre-jour", GroupExposure, 8, plain));
            l.Add(Cc("Iris", "Iris", GroupExposure, 5, plain));

            l.Add(Pa("Brightness", "Luminosite", GroupImage, 0, plain));
            l.Add(Pa("Contrast", "Contraste", GroupImage, 1, plain));
            l.Add(Pa("Saturation", "Saturation", GroupImage, 3, plain));
            l.Add(Pa("Sharpness", "Nettete", GroupImage, 4, plain));
            l.Add(Pa("Hue", "Teinte", GroupImage, 2, plain));
            l.Add(Pa("Gamma", "Gamma", GroupImage, 5, plain));

            l.Add(Cc("Zoom", "Zoom", GroupFraming, 3, plain));
            l.Add(Cc("Pan", "Panoramique", GroupFraming, 0, plain));
            l.Add(Cc("Tilt", "Inclinaison", GroupFraming, 1, plain));
            l.Add(Cc("Roll", "Rotation", GroupFraming, 2, plain));
            l.Add(Cc("Focus", "Mise au point", GroupFraming, 6, plain));
            return l;
        }
    }
}
