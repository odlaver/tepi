using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Tepi
{
    // Long commands: Tepi.exe --run "npm run build"
    partial class Island
    {
        class RunItem
        {
            public string Id = "", Command = "";
            public int Pid;
            public DateTime StartedUtc;
        }

        readonly Dictionary<string, RunItem> runs = new Dictionary<string, RunItem>();

        // Only start and end arrive; nothing executes here
        void OnRun(HookEvent ev)
        {
            string id = CleanText(Json.Str(ev.Data, "id"), 40), cmd = CleanText(Json.Str(ev.Data, "title"), 120);
            if (id.Length == 0 || cmd.Length == 0) return;
            if (Json.Str(ev.Data, "state") == "start")
            {
                if (runs.Count >= 8) return;
                int pid;
                int.TryParse(Json.Str(ev.Data, "pid"), out pid);
                runs[id] = new RunItem { Id = id, Command = cmd, Pid = pid, StartedUtc = DateTime.UtcNow };
                Kick();
                return;
            }
            RunItem r;
            if (!runs.TryGetValue(id, out r)) return;
            runs.Remove(id);
            int code;
            int.TryParse(Json.Str(ev.Data, "code"), out code);
            string took = Elapsed(DateTime.UtcNow - r.StartedUtc);
            SoundCue(code == 0 ? CueDone : CueFail);
            if (code != 0) EyesReact(EyeMood.Sad, 0);
            var t = ShowToast(View.Info, 4, 2, false, true);
            if (t != null)
            {
                t.Glyph = code == 0 ? '' : '';
                t.Title = code == 0 ? Lang.F("Selesai dalam {0}", took) : Lang.F("Gagal, kode {0}", code);
                t.Detail = r.Command;
            }
            Kick();
        }

        // Killed shells never send an end
        void PruneRuns()
        {
            if (runs.Count == 0) return;
            List<string> gone = null;
            foreach (var r in runs.Values)
            {
                bool alive = false;
                try { using (var p = Process.GetProcessById(r.Pid)) alive = !p.HasExited; } catch (Exception) { }
                if (!alive) (gone ?? (gone = new List<string>())).Add(r.Id);
            }
            if (gone == null) return;
            foreach (var id in gone) runs.Remove(id);
            Kick();
        }

        RunItem ActiveRun()
        {
            RunItem latest = null;
            foreach (var r in runs.Values) if (latest == null || r.StartedUtc > latest.StartedUtc) latest = r;
            return latest;
        }

        static string Elapsed(TimeSpan d)
        {
            if (d.TotalHours >= 1) return ((int)d.TotalHours) + ":" + d.Minutes.ToString("00") + ":" + d.Seconds.ToString("00");
            return ((int)d.TotalMinutes) + ":" + d.Seconds.ToString("00");
        }

        float RunWidth()
        {
            var r = ActiveRun();
            float t = r == null || p.G == null ? 120 : p.Measure(r.Command, Ink.Semi(12.5f));
            return Clamp(t + 112, 220, 400);
        }

        Action<DateTime> runClock;
        RectangleF runClockAt;

        void DrawRun(float w, float h)
        {
            var r = ActiveRun();
            if (r == null) return;
            float cy = h / 2;
            p.Icon(Ink.IcoTerminal, 13f, p.T.Text, 21, cy);
            string more = runs.Count > 1 ? "+" + (runs.Count - 1) + "  " : "";
            p.Text(more + r.Command, Ink.Semi(12.5f), p.T.Text, 38, cy, w - 38 - 74, StringAlignment.Near);
            // Elapsed ticks once a second
            if (runClock == null) runClock = t =>
            {
                var a = ActiveRun();
                if (a != null) p.TextRight(Elapsed(DateTime.UtcNow - a.StartedUtc), Ink.Semi(12f), p.T.Sub, runClockAt.Right, runClockAt.Y + runClockAt.Height / 2);
            };
            runClockAt = new RectangleF(w - 78, cy - 10, 60, 20);
            LiveRect(runClockAt, TickFps, runClock);
        }
    }

    // Runs the command here, tells the island
    static class RunCommand
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        public static int Run(string[] args)
        {
            if (args.Length < 2) return 2;
            var sb = new StringBuilder();
            for (int i = 1; i < args.Length; i++) { if (sb.Length > 0) sb.Append(' '); sb.Append(args[i]); }
            string cmd = sb.ToString();
            // Output stays in the caller's terminal
            AttachConsole(-1);
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            int code;
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", "/d /s /c \"" + cmd + "\"") { UseShellExecute = false }))
            {
                Tell(id, cmd, "start", p.Id, 0);
                p.WaitForExit();
                code = p.ExitCode;
            }
            Tell(id, cmd, "end", 0, code);
            return code;
        }

        static void Tell(string id, string cmd, string state, int pid, int code)
        {
            if (!Relay.Running()) return;
            var o = new Dictionary<string, object> { { "hook_event_name", "Run" }, { "id", id }, { "title", cmd }, { "state", state }, { "pid", pid.ToString() }, { "code", code.ToString() } };
            try { Relay.Post("/notify", Encoding.UTF8.GetBytes(Json.Encode(o)), 2000); } catch (Exception) { }
        }
    }
}
