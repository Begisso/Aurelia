using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Aurelia
{
    enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
        public PropertyKey(Guid g, int p) { fmtid = g; pid = p; }
    }

    // PROPVARIANT: 16 bytes on x86, 24 bytes on x64. Padded so it is never too small.
    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(8)] public int intVal;
        [FieldOffset(16)] public IntPtr pad;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(int frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(int frames, int flags);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    // Undocumented but long-stable interface used by the Windows sound control panel to set the default device.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr p);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int b, IntPtr p);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int b, IntPtr a, IntPtr c);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    static class Win32
    {
        [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant pv);
        [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW")]
        public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);
        [DllImport("avrt.dll")] public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    class AudioDevice
    {
        public string Id;
        public string Name;
        public int FormFactor; // 1 speakers, 3 headphones, 5 headset, 9 HDMI/DisplayPort, 8 S/PDIF ...
        public bool LooksVirtual;
        public override string ToString() { return Name; }
    }

    static class Devices
    {
        public const int DEVICE_STATE_ACTIVE = 1;
        static readonly PropertyKey PKEY_FriendlyName = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        static readonly PropertyKey PKEY_FormFactor = new PropertyKey(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

        static readonly string[] VirtualHints = {
            "dfx", "audio enhancer", "cable input", "vb-audio", "voicemeeter", "steam streaming speakers",
            "virtual", "vac", "line 1", "blackhole", "soundflower", "obs"
        };

        public static IMMDeviceEnumerator CreateEnumerator()
        {
            return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) { try { Marshal.ReleaseComObject(o); } catch { } }
        }

        static string ReadString(IPropertyStore store, PropertyKey key)
        {
            PropVariant pv;
            if (store.GetValue(ref key, out pv) != 0) return null;
            string s = null;
            if (pv.vt == 31 && pv.ptr != IntPtr.Zero) s = Marshal.PtrToStringUni(pv.ptr);
            Win32.PropVariantClear(ref pv);
            return s;
        }

        static int ReadUInt(IPropertyStore store, PropertyKey key)
        {
            PropVariant pv;
            if (store.GetValue(ref key, out pv) != 0) return -1;
            int v = -1;
            if (pv.vt == 19 || pv.vt == 3) v = pv.intVal;
            Win32.PropVariantClear(ref pv);
            return v;
        }

        static AudioDevice Describe(IMMDevice dev)
        {
            var d = new AudioDevice();
            string id;
            dev.GetId(out id);
            d.Id = id;
            IPropertyStore store;
            if (dev.OpenPropertyStore(0, out store) == 0 && store != null)
            {
                d.Name = ReadString(store, PKEY_FriendlyName);
                d.FormFactor = ReadUInt(store, PKEY_FormFactor);
                Release(store);
            }
            if (string.IsNullOrEmpty(d.Name)) d.Name = "Unknown device";
            string low = d.Name.ToLowerInvariant();
            foreach (var h in VirtualHints) if (low.Contains(h)) { d.LooksVirtual = true; break; }
            return d;
        }

        public static List<AudioDevice> ListRender()
        {
            var list = new List<AudioDevice>();
            var en = CreateEnumerator();
            try
            {
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(EDataFlow.Render, DEVICE_STATE_ACTIVE, out col) != 0) return list;
                int n;
                col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice dev;
                    if (col.Item(i, out dev) == 0 && dev != null)
                    {
                        try { list.Add(Describe(dev)); } catch { }
                        Release(dev);
                    }
                }
                Release(col);
            }
            finally { Release(en); }
            return list;
        }

        public static string GetDefaultRenderId(ERole role)
        {
            var en = CreateEnumerator();
            try
            {
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(EDataFlow.Render, role, out dev) != 0 || dev == null) return null;
                string id;
                dev.GetId(out id);
                Release(dev);
                return id;
            }
            catch { return null; }
            finally { Release(en); }
        }

        public static bool SetDefault(string id, ERole role)
        {
            if (string.IsNullOrEmpty(id)) return false;
            object o = null;
            try
            {
                o = new PolicyConfigClient();
                var pc = (IPolicyConfig)o;
                return pc.SetDefaultEndpoint(id, role) == 0;
            }
            catch { return false; }
            finally { Release(o); }
        }

        /// Rank how likely a device is the one a person actually listens on.
        public static int ListenScore(AudioDevice d)
        {
            if (d.LooksVirtual) return 0;
            switch (d.FormFactor)
            {
                case 3: case 5: return 50;   // headphones / headset
                case 1: return 40;           // speakers
                case 4: return 30;           // line level
                case 8: case 9: return 20;   // SPDIF / HDMI
                default: return 10;
            }
        }
    }
}
