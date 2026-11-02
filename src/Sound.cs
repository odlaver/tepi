using System;
using System.IO;

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
    }
}
