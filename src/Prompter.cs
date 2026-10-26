using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tepi
{
    // Teleprompter: pasted script scrolls on the island
    partial class Island
    {
        const float PrompterW = 560, PrompterH = 150, PrompterPad = 28;
        const int PrompterHotkey = 2;

        string prompterText;
        List<string> prompterLines;
        float prompterWrapW, prompterWrapPx, prompterPos, pxPerWord = 4;
        bool prompterPaused;

        bool PrompterOn { get { return prompterText != null; } }
        int PrompterWpm { get { return Math.Max(60, Math.Min(260, settings.GetInt("prompter.wpm", 140))); } }
        float PrompterPx { get { return Math.Max(15, Math.Min(26, settings.GetInt("prompter.size", 19))); } }
        float PrompterLineH { get { return (float)Math.Round(PrompterPx * 1.55f); } }
        static string PrompterFile { get { return Path.Combine(Settings.Dir, "prompter.txt"); } }

        void OpenPrompter()
        {
            expanded = false;
            Kick();
            string last = "";
            try { if (File.Exists(PrompterFile)) last = File.ReadAllText(PrompterFile); }
            catch (Exception ex) { Log.Write("prompter read: " + ex.Message); }
            using (var f = new PrompterSetup(last, PrompterWpm, (int)PrompterPx))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
                settings.Set("prompter.wpm", f.Wpm);
                settings.Set("prompter.size", f.Px);
                settings.Save();
                try { File.WriteAllText(PrompterFile, f.Script); }
                catch (Exception ex) { Log.Write("prompter save: " + ex.Message); }
                StartPrompter(f.Script);
            }
        }

        void StartPrompter(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            prompterText = text.Trim();
            prompterLines = null;
            prompterPos = -PrompterLineH * 1.5f;
            prompterPaused = false;
            expanded = false;
            ApplyGlass();
            Kick();
        }

        void StopPrompter()
        {
            prompterText = null;
            prompterLines = null;
            ApplyGlass();
            Kick();
        }

        void TogglePrompter()
        {
            if (!PrompterOn) return;
            // Replay from the top once finished
            if (prompterPaused && prompterPos >= PrompterEnd()) prompterPos = -PrompterLineH * 1.5f;
            prompterPaused = !prompterPaused;
            Kick();
        }

        void NudgeSpeed(int step)
        {
            settings.Set("prompter.wpm", Math.Max(60, Math.Min(260, PrompterWpm + step * 20)));
            settings.Save();
            Kick();
        }

        void ScrollPrompter(int delta)
        {
            prompterPos = Clamp(prompterPos - delta / 120f * PrompterLineH, -PrompterLineH * 1.5f, PrompterEnd());
            Kick();
        }

        float PrompterEnd() { return prompterLines == null ? 0 : Math.Max(0, (prompterLines.Count - 1) * PrompterLineH); }

        bool PrompterRunning() { return PrompterOn && !prompterPaused && current == View.Prompter; }

        void StepPrompter(float dt)
        {
            if (!PrompterRunning() || prompterLines == null) return;
            prompterPos += PrompterWpm / 60f * pxPerWord * dt;
            float end = PrompterEnd();
            if (prompterPos >= end) { prompterPos = end; prompterPaused = true; }
        }

        // Word wrap, once per text, width and size
        void WrapPrompter(float width, Font f)
        {
            var lines = new List<string>();
            int words = 0, filled = 0;
            foreach (var para in prompterText.Replace("\r\n", "\n").Split('\n'))
            {
                var parts = para.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) { if (lines.Count > 0 && lines[lines.Count - 1].Length > 0) lines.Add(""); continue; }
                string cur = "";
                foreach (var word in parts)
                {
                    string next = cur.Length == 0 ? word : cur + " " + word;
                    if (cur.Length > 0 && p.Measure(next, f) > width) { lines.Add(cur); filled++; cur = word; }
                    else cur = next;
                }
                lines.Add(cur);
                filled++;
                words += parts.Length;
            }
            prompterLines = lines;
            prompterWrapW = width;
            prompterWrapPx = PrompterPx;
            pxPerWord = PrompterLineH * Math.Max(1, filled) / Math.Max(1, words);
        }

        void DrawPrompter(float w, float h, DateTime now)
        {
            var f = Ink.Semi(PrompterPx);
            float lh = PrompterLineH, textW = w - PrompterPad * 2;
            if (prompterLines == null || prompterWrapW != textW || prompterWrapPx != PrompterPx) WrapPrompter(textW, f);

            // Lines dim away from the reading line
            float readY = (float)Math.Round(h * 0.4f), baseA = p.Alpha, bottom = pointerInside ? h - 52 : h - 4;
            var st = g.Save();
            g.IntersectClip(new RectangleF(0, 6, w, h - 12));
            int first = Math.Max(0, (int)Math.Floor((prompterPos - readY) / lh));
            for (int i = first; i < prompterLines.Count; i++)
            {
                float cy = readY + i * lh - prompterPos;
                if (cy - lh > h) break;
                if (prompterLines[i].Length == 0) continue;
                float near = Ease.Clamp01(Math.Abs(cy - readY) / lh);
                float edge = Ease.Clamp01(Math.Min(cy - 4, bottom - cy) / (lh * 1.3f));
                p.Alpha = baseA * edge * (1 - 0.55f * near);
                if (p.Alpha > 0.02f) p.Text(prompterLines[i], f, p.T.Text, w / 2, cy, textW, StringAlignment.Center);
            }
            p.Alpha = baseA;
            g.Restore(st);

            // Progress along the bottom edge
            float end = PrompterEnd(), done = end <= 0 ? 1 : Ease.Clamp01(prompterPos / end);
            var track = new RectangleF(PrompterPad, h - 8, textW, 2);
            p.FillRound(track, 1, p.T.Line);
            if (done > 0) p.FillRound(new RectangleF(track.X, track.Y, Math.Max(2, track.Width * done), 2), 1, p.T.Accent);

            if (pointerInside) DrawPrompterControls(w, h);
            else if (prompterPaused) p.TextRight(prompterPos >= end ? "selesai" : "jeda", Ink.Small(11f), p.T.Sub, w - 22, 18);
        }

        void DrawPrompterControls(float w, float h)
        {
            float cy = h - 30, bw = 30;
            var bar = new RectangleF(w / 2 - 132, cy - 18, 264, 36);
            p.FillRound(bar, 18, p.T.Fill);
            var slow = new RectangleF(bar.X + 6, cy - 15, bw, bw);
            var play = new RectangleF(slow.Right + 4, cy - 15, bw, bw);
            var fast = new RectangleF(play.Right + 4, cy - 15, bw, bw);
            RoundButton(slow, "pSlow", delegate { NudgeSpeed(-1); });
            p.FillRound(new RectangleF(slow.X + 10, cy - 0.8f, 10, 1.6f), 0.8f, p.T.Text);
            RoundButton(play, "pPlay", delegate { TogglePrompter(); });
            if (prompterPaused)
                using (var tri = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    tri.AddPolygon(new[] { new PointF(play.X + 12, cy - 6), new PointF(play.X + 21, cy), new PointF(play.X + 12, cy + 6) });
                    g.FillPath(p.B(p.T.Text), tri);
                }
            else
            {
                p.FillRound(new RectangleF(play.X + 10.5f, cy - 5.5f, 3, 11), 1, p.T.Text);
                p.FillRound(new RectangleF(play.X + 16.5f, cy - 5.5f, 3, 11), 1, p.T.Text);
            }
            RoundButton(fast, "pFast", delegate { NudgeSpeed(1); });
            p.FillRound(new RectangleF(fast.X + 10, cy - 0.8f, 10, 1.6f), 0.8f, p.T.Text);
            p.FillRound(new RectangleF(fast.X + 14.2f, cy - 5, 1.6f, 10), 0.8f, p.T.Text);
            p.Text(PrompterWpm + " kata/menit", Ink.Small(11f), p.T.Sub, fast.Right + 10, cy, bar.Right - fast.Right - 52, StringAlignment.Near);
            var close = new RectangleF(bar.Right - 36, cy - 15, bw, bw);
            RoundButton(close, "pClose", delegate { StopPrompter(); });
            p.Icon('', 10f, p.T.Text, close.X + bw / 2, cy);
        }

        void RoundButton(RectangleF r, string id, Action act)
        {
            if (Hover(id)) p.FillRound(r, r.Height / 2, p.T.FillHover);
            AddHit(r, id, act);
        }
    }

    // Paste the script, pick speed and size
    class PrompterSetup : Form
    {
        readonly TextBox script = new TextBox();
        readonly TrackBar speed = new TrackBar(), size = new TrackBar();
        readonly Label speedLabel = new Label(), sizeLabel = new Label();

        public string Script { get { return script.Text; } }
        public int Wpm { get { return speed.Value * 20; } }
        public int Px { get { return size.Value; } }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public PrompterSetup(string text, int wpm, int px)
        {
            Text = "Teleprompter";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(520, 440);
            ShowInTaskbar = true;
            TopMost = true;
            var bg = Ink.Light ? Color.FromArgb(245, 245, 245) : Color.FromArgb(10, 10, 10);
            var fg = Ink.Light ? Color.FromArgb(10, 10, 10) : Color.FromArgb(244, 244, 244);
            var sub = Ink.Light ? Color.FromArgb(88, 88, 88) : Color.FromArgb(160, 160, 160);
            BackColor = bg;
            ForeColor = fg;
            Font = Ink.Text(14f);

            var heading = new Label { Text = "Teleprompter", Font = Ink.Display(20f) };
            heading.SetBounds(24, 18, 472, 30);
            script.SetBounds(24, 56, 472, 220);
            script.Multiline = true;
            script.AcceptsReturn = true;
            script.ScrollBars = ScrollBars.Vertical;
            script.BorderStyle = BorderStyle.FixedSingle;
            script.BackColor = Ink.Light ? Color.White : Color.FromArgb(24, 24, 24);
            script.ForeColor = fg;
            script.Font = Ink.Text(14f);
            script.MaxLength = 200000;
            script.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");

            speedLabel.SetBounds(24, 290, 150, 24);
            speed.SetBounds(180, 286, 316, 32);
            speed.Minimum = 3; speed.Maximum = 13; speed.TickFrequency = 1; speed.LargeChange = 1;
            speed.Value = Math.Max(3, Math.Min(13, wpm / 20));
            sizeLabel.SetBounds(24, 330, 150, 24);
            size.SetBounds(180, 326, 316, 32);
            size.Minimum = 15; size.Maximum = 26; size.TickFrequency = 1; size.LargeChange = 1;
            size.Value = Math.Max(15, Math.Min(26, px));
            foreach (var t in new[] { speed, size }) { t.BackColor = bg; t.AutoSize = false; }
            speed.ValueChanged += delegate { Labels(); };
            size.ValueChanged += delegate { Labels(); };
            Labels();

            var hint = new Label { Text = "Jeda: klik island atau Ctrl+Alt+Spasi", ForeColor = sub };
            hint.SetBounds(24, 392, 260, 24);
            var start = new Button { Text = "Mulai" };
            start.SetBounds(356, 384, 140, 34);
            start.FlatStyle = FlatStyle.Flat;
            start.FlatAppearance.BorderSize = 0;
            start.BackColor = fg;
            start.ForeColor = bg;
            start.Font = Ink.Semi(14f);
            start.Cursor = Cursors.Hand;
            start.Click += delegate
            {
                if (string.IsNullOrWhiteSpace(script.Text)) { script.Focus(); return; }
                DialogResult = DialogResult.OK;
                Close();
            };
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; start.PerformClick(); }
            };
            Controls.AddRange(new Control[] { heading, script, speedLabel, speed, sizeLabel, size, hint, start });
        }

        void Labels()
        {
            speedLabel.Text = Wpm + " kata/menit";
            sizeLabel.Text = "Huruf " + Px + " px";
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = Ink.Light ? 0 : 1;
            try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch (Exception) { }
        }
    }
}
