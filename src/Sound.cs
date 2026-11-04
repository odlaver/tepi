using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Tepi
{
    // sound.agent: Windows' own sounds for agents and runs
    partial class Island
    {
        const string CueAsk = "Windows Message Nudge.wav", CueDone = "Windows Notify System Generic.wav", CueFail = "Windows Background.wav";

        void SoundCue(string file)
        {
            if (!settings.GetBool("sound.agent", false) || Quiet || fullscreen || PrompterOn || Native.DoNotDisturb()) return;
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", file);
            // Async, no default beep if missing
            if (File.Exists(path)) PlaySound(path, IntPtr.Zero, 0x0001 | 0x0002 | 0x20000);
        }

        // Reminder sounds: "" default, "none", or a file
        public const string CueRemind = "Windows Notify Calendar.wav", SoundNone = "none";
        const string MciAlias = "tepiremind";
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int mciSendString(string cmd, StringBuilder ret, int len, IntPtr cb);
        readonly StringBuilder mciMode = new StringBuilder(32);
        bool remindPlaying, remindDucked;

        static string RemindPath(string pick)
        {
            if (pick == SoundNone) return null;
            if (LocalFile(pick)) return pick;
            // Missing file falls back to default
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", CueRemind);
            return File.Exists(def) ? def : null;
        }

        // Local drive only; network paths would call out
        static bool LocalFile(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 4 || path[1] != ':' || path[2] != '\\' || !char.IsLetter(path[0])) return false;
            try { return new DriveInfo(path.Substring(0, 1)).DriveType != DriveType.Network && File.Exists(path); }
            catch (Exception) { return false; }
        }

        bool RemindMuted() { return Native.DoNotDisturb() || MeetingOn || PrompterOn; }

        // Plays a reminder sound; long ones duck music
        void PlayRemind(string pick, bool test = false)
        {
            StopRemind();
            if (!test && RemindMuted()) return;
            string path = RemindPath(pick);
            if (path == null) return;
            if (mciSendString("open \"" + path + "\" type mpegvideo alias " + MciAlias, null, 0, IntPtr.Zero) != 0) { Log.Write("sound: cannot open " + Path.GetFileName(path)); return; }
            mciSendString("play " + MciAlias, null, 0, IntPtr.Zero);
            remindPlaying = true;
            // Short chimes need no ducking
            var len = new StringBuilder(32);
            mciSendString("set " + MciAlias + " time format milliseconds", null, 0, IntPtr.Zero);
            mciSendString("status " + MciAlias + " length", len, len.Capacity, IntPtr.Zero);
            int ms;
            if (int.TryParse(len.ToString(), out ms) && ms > 2500) { volume.Duck(true); remindDucked = true; }
        }

        void StopRemind()
        {
            if (!remindPlaying) return;
            mciSendString("close " + MciAlias, null, 0, IntPtr.Zero);
            remindPlaying = false;
            if (remindDucked) { volume.Duck(false); remindDucked = false; }
        }

        // Clock tick: release once it ends
        void RemindSoundTick()
        {
            if (!remindPlaying) return;
            mciMode.Length = 0;
            mciSendString("status " + MciAlias + " mode", mciMode, mciMode.Capacity, IntPtr.Zero);
            if (mciMode.ToString() != "playing") StopRemind();
        }
    }
}
