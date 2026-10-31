using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Default microphone mute, system-wide
    partial class VolumeWatcher
    {
        public event Action<bool, bool, bool> MicChanged; // muted, present, first reading
        IAudioEndpointVolume mic, micComm;
        MicCallback micCb;

        class MicCallback : IAudioEndpointVolumeCallback
        {
            public VolumeWatcher Owner;
            public int OnNotify(IntPtr data)
            {
                var h = Owner.MicChanged;
                if (h != null) h(Marshal.ReadInt32(data, 16) != 0, true, false);
                return 0;
            }
        }

        // Console and calls can differ
        void AttachMic()
        {
            DetachMic(ref mic);
            DetachMic(ref micComm);
            if (micCb == null) micCb = new MicCallback { Owner = this };
            string consoleId;
            mic = OpenCapture(0, out consoleId);
            string commId;
            var comm = OpenCapture(2, out commId);
            if (comm != null && commId != consoleId) micComm = comm;
            else if (comm != null) Marshal.ReleaseComObject(comm);
            bool muted = false;
            if (mic != null) { mic.RegisterControlChangeNotify(micCb); mic.GetMute(out muted); }
            var h = MicChanged;
            if (h != null) h(muted, mic != null, true);
        }

        IAudioEndpointVolume OpenCapture(int role, out string id)
        {
            id = null;
            IMMDevice dev;
            if (enumerator.GetDefaultAudioEndpoint(1, role, out dev) != 0 || dev == null) return null;
            dev.GetId(out id);
            var iid = typeof(IAudioEndpointVolume).GUID;
            object o;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return null;
            return (IAudioEndpointVolume)o;
        }

        void DetachMic(ref IAudioEndpointVolume ep)
        {
            if (ep == null) return;
            try { if (ep == mic) ep.UnregisterControlChangeNotify(micCb); } catch (Exception) { }
            Marshal.ReleaseComObject(ep);
            ep = null;
        }

        public void SetMicMute(bool mute)
        {
            Do(delegate
            {
                if (mic != null) mic.SetMute(mute, ref ctx);
                if (micComm != null) micComm.SetMute(mute, ref ctx);
            });
        }
    }

    partial class Island
    {
        const int MicHotkey = 12;
        const char IcoMicOff = '';
        bool micMuted, micPresent;
        Action actMic;

        void StartMic()
        {
            if (!RegisterHotKey(Handle, MicHotkey, 0x0001 | 0x0002 | 0x4000, (uint)Keys.M)) Log.Write("hotkey Ctrl+Alt+M taken");
            actMic = ToggleMic;
        }

        void ToggleMic()
        {
            if (!micPresent) { ShowInfo(Ink.IcoMic, Lang.T("Tidak ada mikrofon"), "", true); return; }
            volume.SetMicMute(!micMuted);
        }

        void OnMic(bool muted, bool present, bool first)
        {
            bool changed = muted != micMuted;
            micMuted = muted;
            micPresent = present;
            if (!first && changed)
            {
                var t = ShowToast(View.Mic, 1.6, 1, false, true);
                if (t != null) t.Flag = muted;
            }
            Kick();
        }

        // Muted while an app listens: say so
        bool MicSilenced { get { return micMuted && micApps.Length > 0; } }

        string MicTitle() { return toast != null && toast.Flag ? Lang.T("Mikrofon mati") : Lang.T("Mikrofon nyala"); }

        float MicWidth() { return p.G == null || toast == null ? 220 : Clamp(p.Measure(MicTitle(), Ink.Semi(13.5f)) + 76, 180, 300); }

        void DrawMic(float w, float h)
        {
            if (toast == null) return;
            float cy = h / 2;
            var semi = Ink.Semi(13.5f);
            float x = CenterStart(w, 27, MicTitle(), semi, null, semi, 0);
            p.Icon(toast.Flag ? IcoMicOff : Ink.IcoMic, 15f, toast.Flag ? p.T.Negative : p.T.Text, x + 8, cy);
            p.Text(MicTitle(), semi, p.T.Text, x + 27, cy, w - x - 43, StringAlignment.Near);
        }
    }
}
