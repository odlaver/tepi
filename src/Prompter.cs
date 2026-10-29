using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Tepi
{
    // Teleprompter: pasted script scrolls on the island
    partial class Island
    {
        const float PrompterPad = 28, PrompterMinW = 400, PrompterMaxW = 900, PrompterMinH = 110, PrompterMaxH = 600;
        float PrompterW { get { return prompterSizeW > 0 ? prompterSizeW : Clamp(settings.GetInt("prompter.width", 560), PrompterMinW, PrompterMaxW); } }
        float PrompterH { get { return prompterSizeH > 0 ? prompterSizeH : Clamp(settings.GetInt("prompter.height", 150), PrompterMinH, PrompterMaxH); } }
        float PrompterBg { get { return Clamp(settings.GetInt("prompter.bg", 55), 15, 100) / 100f; } }
        const int PrompterHotkey = 2, PrompterFaster = 4, PrompterSlower = 5, PrompterSetupKey = 6, PrompterRestart = 7, PrompterStop = 8, PrompterBack = 9, PrompterAhead = 10;

        string prompterText;
        float prompterWrapW, prompterWrapPx, prompterWrapLh, prompterPos, pxPerWord = 4;
        bool prompterWrapped, prompterHeld;

        // Paused script takes clicks; running passes them
        bool prompterPaused
        {
            get { return prompterHeld; }
            set
            {
                if (prompterHeld == value) return;
                prompterHeld = value;
                if (!value && current == View.Prompter) PrompterThrough();
                ApplyClickThrough();
            }
        }

        bool PrompterOn { get { return prompterText != null; } }
        int PrompterWpm { get { return Math.Max(60, Math.Min(400, settings.GetInt("prompter.wpm", 140))); } }
        float PrompterPx { get { return Math.Max(15, Math.Min(26, settings.GetInt("prompter.size", 19))); } }
        float PrompterLineH { get { return (float)Math.Round(PrompterPx * 1.55f); } }
        bool PrompterMirror { get { return settings.GetBool("prompter.mirror", false); } }
        static string PrompterFile { get { return Path.Combine(Settings.Dir, "prompter.txt"); } }

        readonly List<float> prompterMarks = new List<float>();
        int prompterNextMark;
        DateTime prompterGoUtc;
        long prompterDrawn;

        // Words measured once per script and size
        readonly List<string> pWords = new List<string>();
        readonly List<float> pWidth = new List<float>();
        readonly List<bool> pGlue = new List<bool>();
        readonly List<int> pParaEnd = new List<int>(), pMarkAt = new List<int>();
        readonly List<bool> pParaBlank = new List<bool>();
        readonly Dictionary<string, float> pMeasured = new Dictionary<string, float>();
        string pTokFor;
        float pTokPx, pSpace;
        int pWordCount;
        static StringFormat measureFormat;

        // Lines as word ranges; text built lazily
        readonly List<int> lineStart = new List<int>(), lineEnd = new List<int>();
        readonly Dictionary<long, string> lineText = new Dictionary<long, string>();
        readonly StringBuilder lineBuild = new StringBuilder();
        int leftShown = -1, anchorTok = -1;
        float anchorFrac, anchorPos;
        string leftStr = "";

        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        bool prompterSetupOpen;

        void OpenPrompter()
        {
            if (prompterSetupOpen) return;
            bool live = PrompterOn, wasPaused = prompterPaused;
            // Hold still while adjusting
            if (live) prompterPaused = true;
            else expanded = false;
            Kick();
            string last = live ? prompterText : "";
            if (!live)
                try { if (File.Exists(PrompterFile)) last = File.ReadAllText(PrompterFile); }
                catch (Exception ex) { Log.Write("prompter read: " + ex.Message); }
            int wpm0 = PrompterWpm, px0 = (int)PrompterPx, bg0 = (int)Math.Round(PrompterBg * 100);
            bool mirror0 = PrompterMirror;
            prompterSetupOpen = true;
            using (var f = new PrompterSetup(last, wpm0, px0, mirror0, bg0, live))
            {
                if (tray != null) f.Icon = tray.Icon;
                // Live script previews every change
                if (live) f.Changed += delegate { if (!PrompterOn) return; PrompterLook(f.Wpm, f.Px, f.Mirror, f.Bg); Kick(); };
                bool ok = f.ShowDialog() == DialogResult.OK;
                prompterSetupOpen = false;
                if (!ok)
                {
                    PrompterLook(wpm0, px0, mirror0, bg0);
                    if (live && PrompterOn) prompterPaused = wasPaused;
                    Kick();
                    return;
                }
                PrompterLook(f.Wpm, f.Px, f.Mirror, f.Bg);
                settings.Save();
                try { File.WriteAllText(PrompterFile, f.Script); }
                catch (Exception ex) { Log.Write("prompter save: " + ex.Message); }
                if (!live || !PrompterOn || f.Script.Trim() != prompterText) { StartPrompter(f.Script); return; }
                // Same script: rewrap keeps the place
                prompterPaused = wasPaused;
                Kick();
            }
        }

        void PrompterLook(int wpm, int px, bool mirror, int bg)
        {
            settings.Set("prompter.wpm", wpm);
            settings.Set("prompter.size", px);
            settings.Set("prompter.mirror", mirror);
            settings.Set("prompter.bg", bg);
        }

        void StartPrompter(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            prompterText = text.Trim();
            prompterWrapped = false;
            prompterPaused = false;
            expanded = false;
            Rewind();
            // Shortcut is held only while a script runs
            if (!RegisterHotKey(Handle, PrompterHotkey, 0x0001 | 0x0002 | 0x4000, (uint)Keys.Space)) Log.Write("hotkey Ctrl+Alt+Space taken");
            if (!RegisterHotKey(Handle, PrompterFaster, 0x0001 | 0x0002 | 0x4000, (uint)Keys.Oemplus)) Log.Write("hotkey Ctrl+Alt+= taken");
            if (!RegisterHotKey(Handle, PrompterSlower, 0x0001 | 0x0002 | 0x4000, (uint)Keys.OemMinus)) Log.Write("hotkey Ctrl+Alt+- taken");
            if (!RegisterHotKey(Handle, PrompterSetupKey, 0x0001 | 0x0002 | 0x4000, (uint)Keys.P)) Log.Write("hotkey Ctrl+Alt+P taken");
            if (!RegisterHotKey(Handle, PrompterRestart, 0x0001 | 0x0002 | 0x4000, (uint)Keys.R)) Log.Write("hotkey Ctrl+Alt+R taken");
            if (!RegisterHotKey(Handle, PrompterStop, 0x0001 | 0x0002 | 0x4000, (uint)Keys.X)) Log.Write("hotkey Ctrl+Alt+X taken");
            if (!RegisterHotKey(Handle, PrompterBack, 0x0001 | 0x0002 | 0x4000, (uint)Keys.Left)) Log.Write("hotkey Ctrl+Alt+Left taken");
            if (!RegisterHotKey(Handle, PrompterAhead, 0x0001 | 0x0002 | 0x4000, (uint)Keys.Right)) Log.Write("hotkey Ctrl+Alt+Right taken");
            prompterHalf = PrompterW / 2;
            if (WantHalf > canvasHalf) CreateCanvas();
            ApplyGlass();
            Kick();
        }

        void StopPrompter()
        {
            if (prompterSizing != 0) { Capture = false; EndPrompterSize(); }
            prompterText = null;
            prompterWrapped = false;
            prompterHeld = false;
            PrompterCursor(0);
            UnregisterHotKey(Handle, PrompterHotkey);
            UnregisterHotKey(Handle, PrompterFaster);
            UnregisterHotKey(Handle, PrompterSlower);
            UnregisterHotKey(Handle, PrompterSetupKey);
            UnregisterHotKey(Handle, PrompterRestart);
            UnregisterHotKey(Handle, PrompterStop);
            UnregisterHotKey(Handle, PrompterBack);
            UnregisterHotKey(Handle, PrompterAhead);
            // Frame shrinks canvas once shell fits
            ApplyGlass();
            Kick();
        }

        // Back to the top, with a 3-2-1 countdown
        void Rewind()
        {
            prompterPos = -PrompterLineH * 1.5f;
            prompterNextMark = 0;
            prompterGoUtc = DateTime.UtcNow.AddSeconds(3);
        }

        // From the top, countdown, then rolling
        void RestartPrompter()
        {
            if (!PrompterOn) return;
            Rewind();
            prompterPaused = false;
            Kick();
        }

        // Jump by reading time at current speed
        void SeekPrompter(float seconds)
        {
            if (!PrompterOn) return;
            prompterPos = Clamp(prompterPos + PrompterSpeedPx() * seconds, -PrompterLineH * 1.5f, PrompterEnd());
            SyncMarks();
            Kick();
        }

        void TogglePrompter()
        {
            if (!PrompterOn) return;
            if (prompterPaused && prompterPos >= PrompterEnd()) Rewind();
            prompterPaused = !prompterPaused;
            Kick();
        }

        void NudgeSpeed(int step)
        {
            settings.Set("prompter.wpm", Math.Max(60, Math.Min(400, PrompterWpm + step * 20)));
            settings.Save();
            Kick();
        }

        void ScrollPrompter(int delta)
        {
            prompterPos = Clamp(prompterPos - delta / 120f * PrompterLineH, -PrompterLineH * 1.5f, PrompterEnd());
            SyncMarks();
            Kick();
        }

        void SyncMarks()
        {
            prompterNextMark = 0;
            while (prompterNextMark < prompterMarks.Count && prompterMarks[prompterNextMark] <= prompterPos) prompterNextMark++;
        }

        float PrompterEnd() { return !prompterWrapped ? 0 : Math.Max(0, (lineStart.Count - 1) * PrompterLineH); }

        bool PrompterRunning() { return PrompterOn && !prompterPaused && current == View.Prompter; }

        int Countdown() { return (int)Math.Ceiling((prompterGoUtc - DateTime.UtcNow).TotalSeconds); }

        float PrompterSpeedPx() { return PrompterWpm / 60f * pxPerWord; }

        void StepPrompter(float dt)
        {
            StepPrompterSize();
            if (!PrompterRunning() || !prompterWrapped || Countdown() > 0) return;
            float next = prompterPos + PrompterSpeedPx() * dt;
            // [jeda] in the script stops here
            if (prompterNextMark < prompterMarks.Count && next >= prompterMarks[prompterNextMark])
            {
                prompterPos = prompterMarks[prompterNextMark++];
                prompterPaused = true;
                return;
            }
            prompterPos = next;
            float end = PrompterEnd();
            if (prompterPos >= end) { prompterPos = end; prompterPaused = true; }
        }

        // What the frame shows; same key means no redraw
        long PrompterKey()
        {
            int cd = Countdown();
            long k = (long)Math.Round(prompterPos * scale);
            k = k * 31 + (cd > 0 && !prompterPaused ? cd : 0);
            k = k * 31 + (prompterPaused ? 1 : 2);
            k = k * 31 + LeftSeconds();
            k = k * 31 + (pointerInside ? 1 : 2);
            k = k * 31 + (hoverId == null ? 0 : hoverId.GetHashCode());
            k = k * 31 + prompterEdge * 16 + prompterSizing;
            return k;
        }

        int LeftSeconds()
        {
            float speed = PrompterSpeedPx();
            if (speed <= 0) return -1;
            return (int)Math.Ceiling(Math.Max(0, PrompterEnd() - prompterPos) / speed);
        }

        string LeftText()
        {
            int s = LeftSeconds();
            if (s < 0) return "";
            if (s != leftShown) { leftShown = s; leftStr = (s / 60) + ":" + (s % 60).ToString("00"); }
            return leftStr;
        }

        float WordWidth(string s, Font f)
        {
            float w;
            if (pMeasured.TryGetValue(s, out w)) return w;
            if (measureFormat == null)
            {
                measureFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
                measureFormat.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            }
            w = g.MeasureString(s, f, PointF.Empty, measureFormat).Width;
            pMeasured[s] = w;
            return w;
        }

        // Script into measured words, [jeda] marks
        void TokenizePrompter(Font f)
        {
            pWords.Clear(); pWidth.Clear(); pGlue.Clear(); pParaEnd.Clear(); pParaBlank.Clear(); pMarkAt.Clear();
            pMeasured.Clear();
            lineText.Clear();
            pWordCount = 0;
            pSpace = WordWidth(" ", f);
            // Narrowest line any width allows
            float limit = PrompterMinW - PrompterPad * 2 - 2;
            foreach (var para in prompterText.Replace("\r\n", "\n").Split('\n'))
            {
                var parts = para.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in parts)
                {
                    if (raw.Equals("[jeda]", StringComparison.OrdinalIgnoreCase) || raw.Equals("[pause]", StringComparison.OrdinalIgnoreCase))
                    {
                        pMarkAt.Add(pWords.Count);
                        continue;
                    }
                    pWordCount++;
                    float w = WordWidth(raw, f);
                    if (w <= limit) { pWords.Add(raw); pWidth.Add(w); pGlue.Add(false); continue; }
                    // Wider than a line: split by characters
                    int start = 0;
                    while (start < raw.Length)
                    {
                        int len = 1;
                        while (start + len < raw.Length && WordWidth(raw.Substring(start, len + 1), f) <= limit) len++;
                        string piece = raw.Substring(start, len);
                        pWords.Add(piece); pWidth.Add(WordWidth(piece, f)); pGlue.Add(start > 0);
                        start += len;
                    }
                }
                pParaEnd.Add(pWords.Count);
                pParaBlank.Add(parts.Length == 0);
            }
            pTokFor = prompterText;
            pTokPx = PrompterPx;
        }

        // Rewrap by sums; reading place stays put
        void WrapPrompter(float width, Font f)
        {
            float lh = PrompterLineH;
            int anchor = -1;
            float frac = 0;
            bool same = prompterWrapped && ReferenceEquals(pTokFor, prompterText) && lineStart.Count > 0 && prompterWrapLh > 0;
            // Unmoved since last wrap: keep the word
            if (same && prompterPos >= 0 && anchorTok >= 0 && prompterPos == anchorPos) { anchor = anchorTok; frac = anchorFrac; }
            else if (same && prompterPos >= 0)
            {
                float at = prompterPos / prompterWrapLh;
                int li = Math.Min(lineStart.Count - 1, (int)at);
                anchor = lineStart[li];
                frac = Math.Min(0.999f, at - li);
            }
            float oldLh = prompterWrapLh;
            if (!ReferenceEquals(pTokFor, prompterText) || pTokPx != PrompterPx) TokenizePrompter(f);

            lineStart.Clear();
            lineEnd.Clear();
            // Margin keeps sums under the drawn width
            float room = width - 2;
            int tok = 0, filled = 0;
            for (int p = 0; p < pParaEnd.Count; p++)
            {
                int end = pParaEnd[p];
                if (tok == end)
                {
                    int n = lineStart.Count;
                    if (pParaBlank[p] && n > 0 && lineEnd[n - 1] > lineStart[n - 1]) { lineStart.Add(tok); lineEnd.Add(tok); }
                    continue;
                }
                int s = tok;
                float cur = 0;
                for (; tok < end; tok++)
                {
                    float add = pWidth[tok] + (tok > s && !pGlue[tok] ? pSpace : 0);
                    if (tok > s && cur + add > room) { lineStart.Add(s); lineEnd.Add(tok); filled++; s = tok; cur = pWidth[tok]; }
                    else cur += add;
                }
                lineStart.Add(s);
                lineEnd.Add(end);
                filled++;
            }
            if (lineStart.Count == 0) { lineStart.Add(0); lineEnd.Add(0); }
            prompterWrapped = true;
            prompterWrapW = width;
            prompterWrapPx = PrompterPx;
            prompterWrapLh = lh;
            pxPerWord = lh * Math.Max(1, filled) / Math.Max(1, pWordCount);
            prompterMarks.Clear();
            foreach (int m in pMarkAt)
            {
                float y = (m == 0 ? 0 : LineOf(m - 1)) * lh;
                if (prompterMarks.Count == 0 || prompterMarks[prompterMarks.Count - 1] < y) prompterMarks.Add(y);
            }
            if (anchor >= 0) prompterPos = (LineOf(anchor) + frac) * lh;
            else if (same && prompterPos < 0) prompterPos *= lh / oldLh;
            prompterPos = Clamp(prompterPos, -lh * 1.5f, PrompterEnd());
            anchorTok = anchor;
            anchorFrac = frac;
            anchorPos = prompterPos;
            SyncMarks();
        }

        // Last line starting at or before word
        int LineOf(int word)
        {
            int lo = 0, hi = lineStart.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (lineStart[mid] <= word) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        string LineText(int i)
        {
            int s = lineStart[i], e = lineEnd[i];
            if (e <= s) return "";
            long key = ((long)s << 32) | (uint)e;
            string str;
            if (lineText.TryGetValue(key, out str)) return str;
            lineBuild.Length = 0;
            for (int t = s; t < e; t++)
            {
                if (t > s && !pGlue[t]) lineBuild.Append(' ');
                lineBuild.Append(pWords[t]);
            }
            str = lineBuild.ToString();
            if (lineText.Count > 4096) lineText.Clear();
            lineText[key] = str;
            return str;
        }

        // See-through backdrop, no frost
        void FillPrompterBg(System.Drawing.Drawing2D.GraphicsPath path)
        {
            g.FillPath(p.B(Ink.A(Ink.Base.Bg, PrompterBg)), path);
            g.DrawPath(p.P(Ink.Base.Line, 1f), path);
        }

        void DrawPrompter(float w, float h, DateTime now)
        {
            // Fading out after Stop: nothing left to draw
            if (prompterText == null) return;
            var f = Ink.Semi(PrompterPx);
            // Wrap at the final width so a morph never reflows the script
            float lh = PrompterLineH, textW = PrompterW - PrompterPad * 2;
            if (!prompterWrapped || prompterWrapW != textW || prompterWrapPx != PrompterPx || !ReferenceEquals(pTokFor, prompterText)) WrapPrompter(textW, f);

            var st = g.Save();
            g.IntersectClip(new RectangleF(0, 6, w, h - 12));
            if (PrompterMirror) { g.TranslateTransform(w, 0); g.ScaleTransform(-1, 1); }
            int cd = Countdown();
            if (cd > 0 && !prompterPaused) p.TextCenter(cd.ToString(), Ink.Display(44f), p.T.Text, w / 2, h / 2 - 4);
            else DrawPrompterLines(w, h, f, lh, textW);
            g.Restore(st);

            // Progress along the bottom edge
            float end = PrompterEnd(), done = end <= 0 ? 1 : Ease.Clamp01(prompterPos / end);
            var track = new RectangleF(PrompterPad, h - 8, w - PrompterPad * 2, 2);
            p.FillRound(track, 1, p.T.Line);
            if (done > 0) p.FillRound(new RectangleF(track.X, track.Y, Math.Max(2, track.Width * done), 2), 1, p.T.Accent);
            DrawKnob(track.X + track.Width * done, track.Y + 1, track.X, track.Right);

            if (prompterSizing != 0) p.TextRight(SizeText(), Ink.Small(11f), p.T.Sub, w - 22, 18);
            else if (pointerInside) DrawPrompterControls(w, h);
            else
            {
                string state = !prompterPaused ? LeftText() : prompterPos >= end ? Lang.T("selesai") : Lang.T("jeda") + " · " + LeftText();
                p.TextRight(state, Ink.Small(11f), p.T.Sub, w - 22, 18);
            }
            if (prompterPaused && (pointerInside || prompterSizing != 0)) DrawPrompterGrips(w, h);
        }

        void DrawPrompterLines(float w, float h, Font f, float lh, float textW)
        {
            // Whole device pixels: crisp text, fewer redraws
            float pos = (float)Math.Round(prompterPos * scale) / scale;
            float readY = (float)Math.Round(h * 0.4f), baseA = p.Alpha, bottom = pointerInside && prompterSizing == 0 ? h - 52 : h - 4;
            int first = Math.Max(0, (int)Math.Floor((pos - readY) / lh));
            for (int i = first; i < lineStart.Count; i++)
            {
                float cy = readY + i * lh - pos;
                if (cy - lh > h) break;
                float near = Ease.Clamp01(Math.Abs(cy - readY) / lh);
                float edge = Ease.Clamp01(Math.Min(cy - 4, bottom - cy) / (lh * 1.3f));
                p.Alpha = baseA * edge * (1 - 0.55f * near);
                if (p.Alpha <= 0.02f) continue;
                if (lineEnd[i] > lineStart[i]) p.Text(LineText(i), f, p.T.Text, w / 2, cy, textW, StringAlignment.Center);
                if (prompterMarks.Contains(i * lh)) p.Circle(PrompterPad - 12, cy, 3f, p.T.Accent);
            }
            p.Alpha = baseA;
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
            p.Text(Lang.F("{0} kata/menit", PrompterWpm), Ink.Small(11f), p.T.Sub, fast.Right + 10, cy, bar.Right - fast.Right - 52, StringAlignment.Near);
            var close = new RectangleF(bar.Right - 36, cy - 15, bw, bw);
            RoundButton(close, "pClose", delegate { StopPrompter(); });
            p.Icon('', 10f, p.T.Text, close.X + bw / 2, cy);
        }

        void RoundButton(RectangleF r, string id, Action act)
        {
            if (Hover(id)) p.FillRound(r, r.Height / 2, p.T.FillHover);
            AddHit(r, id, act);
        }

        // Paused island: edges drag to resize

        const int EdgeLeft = 1, EdgeRight = 2, EdgeBottom = 4;
        const float GripReach = 10, GripCorner = 24;
        int prompterEdge, prompterSizing;
        bool prompterSizeMoved;
        float sizeW0, sizeH0, prompterSizeW, prompterSizeH, sizeShownW, sizeShownH;
        Point sizeFrom, sizeTo;
        string sizeStr = "";

        int PrompterEdgeAt(PointF pt)
        {
            if (current != View.Prompter || !prompterPaused || !PrompterOn) return 0;
            var s = shell;
            if (!s.Contains(pt)) return 0;
            bool nearL = pt.X <= s.X + GripReach, nearR = pt.X >= s.Right - GripReach, nearB = pt.Y >= s.Bottom - GripReach;
            bool cornerB = pt.Y >= s.Bottom - GripCorner;
            int e = 0;
            if (nearL || (nearB && pt.X <= s.X + GripCorner)) e |= EdgeLeft;
            else if (nearR || (nearB && pt.X >= s.Right - GripCorner)) e |= EdgeRight;
            if (nearB || (e != 0 && cornerB)) e |= EdgeBottom;
            return e;
        }

        void PrompterCursor(int edge)
        {
            var want = edge == EdgeBottom ? Cursors.SizeNS : edge == (EdgeLeft | EdgeBottom) ? Cursors.SizeNESW : edge == (EdgeRight | EdgeBottom) ? Cursors.SizeNWSE : edge != 0 ? Cursors.SizeWE : Cursors.Default;
            if (Cursor != want) Cursor = want;
        }

        // From mouse move: cursor and grip highlight
        void PrompterHover(PointF pt, bool inside)
        {
            int edge = inside ? PrompterEdgeAt(pt) : 0;
            PrompterCursor(edge);
            if (edge == prompterEdge) return;
            prompterEdge = edge;
            Kick();
        }

        bool PrompterGrab(PointF pt, Point screen)
        {
            int edge = PrompterEdgeAt(pt);
            if (edge == 0) return false;
            Capture = true;
            BeginPrompterSize(edge, screen);
            return true;
        }

        void BeginPrompterSize(int edge, Point screen)
        {
            prompterEdge = prompterSizing = edge;
            sizeFrom = sizeTo = screen;
            sizeW0 = prompterSizeW = PrompterW;
            sizeH0 = prompterSizeH = PrompterH;
            PrompterCursor(edge);
            // Widest canvas once, not per move
            prompterHalf = PrompterMaxW / 2;
            if (WantHalf > canvasHalf) CreateCanvas();
            Kick();
        }

        void SizePrompterTo(Point screen)
        {
            if (screen == sizeTo) return;
            sizeTo = screen;
            Kick();
        }

        // Running again: hover state from pause goes
        void PrompterThrough()
        {
            pointerInside = false;
            hoverId = null;
            prompterEdge = 0;
            PrompterCursor(0);
        }

        // Per frame: newest pointer sets the shape
        void StepPrompterSize()
        {
            prompterSizeMoved = false;
            if (prompterSizing == 0) return;
            float dx = (sizeTo.X - sizeFrom.X) / scale, dy = (sizeTo.Y - sizeFrom.Y) / scale;
            float w = sizeW0, h = sizeH0;
            // Centred island grows on both sides
            if ((prompterSizing & EdgeRight) != 0) w += dx * 2;
            if ((prompterSizing & EdgeLeft) != 0) w -= dx * 2;
            if ((prompterSizing & EdgeBottom) != 0) h += dy;
            w = (float)Math.Round(Clamp(w, PrompterMinW, PrompterMaxW) / 2) * 2;
            h = (float)Math.Round(Clamp(h, PrompterMinH, PrompterMaxH));
            // Changed shape: one vsync-paced frame
            prompterSizeMoved = w != prompterSizeW || h != prompterSizeH;
            prompterSizeW = w;
            prompterSizeH = h;
            sw.Snap(prompterSizeW);
            sh.Snap(prompterSizeH);
        }

        void EndPrompterSize()
        {
            if (prompterSizing == 0) return;
            StepPrompterSize();
            prompterSizing = 0;
            settings.Set("prompter.width", (int)prompterSizeW);
            settings.Set("prompter.height", (int)prompterSizeH);
            settings.Save();
            prompterSizeW = prompterSizeH = 0;
            prompterHalf = PrompterW / 2;
            // Canvas back to what fits
            if (canvasHalf > WantHalf + 0.5f) CreateCanvas();
            Kick();
        }

        string SizeText()
        {
            if (prompterSizeW != sizeShownW || prompterSizeH != sizeShownH)
            {
                sizeShownW = prompterSizeW;
                sizeShownH = prompterSizeH;
                sizeStr = (int)sizeShownW + " × " + (int)sizeShownH;
            }
            return sizeStr;
        }

        // Faint grips; the active edge brightens
        void DrawPrompterGrips(float w, float h)
        {
            int on = prompterSizing != 0 ? prompterSizing : prompterEdge;
            float side = Math.Min(36, h * 0.3f), cy = h / 2;
            p.FillRound(new RectangleF(4, cy - side / 2, 3, side), 1.5f, Ink.A(p.T.Text, (on & EdgeLeft) != 0 ? 0.85f : 0.3f));
            p.FillRound(new RectangleF(w - 7, cy - side / 2, 3, side), 1.5f, Ink.A(p.T.Text, (on & EdgeRight) != 0 ? 0.85f : 0.3f));
            if ((on & EdgeBottom) != 0) p.FillRound(new RectangleF(w / 2 - 22, h - 4.5f, 44, 3), 1.5f, Ink.A(p.T.Text, 0.85f));
        }
    }

    // Paste the script, pick speed and size
    class PrompterSetup : KitWindow
    {
        readonly KitPopup library = new KitPopup();
        readonly KitButton import = new KitButton(), keep = new KitButton(), remove = new KitButton(), cancel = new KitButton(), start = new KitButton();
        readonly ScriptWell well = new ScriptWell();
        readonly KitGroup group = new KitGroup();
        readonly KitRow speedRow = new KitRow(), sizeRow = new KitRow(), shadeRow = new KitRow(), mirrorRow = new KitRow();
        readonly KitSlider speed = new KitSlider(), size = new KitSlider(), shade = new KitSlider();
        readonly KitSwitch mirror = new KitSwitch();
        readonly Label title = new Label(), markHint = new Label(), count = new Label(), keysCap = new Label(), sizeHint = new Label();
        readonly KeyList keys = new KeyList();
        readonly List<string> names = new List<string>();
        readonly string lastText;
        readonly bool live;
        bool picking, ready;

        public event Action Changed;

        static string Folder { get { return Path.Combine(Settings.Dir, "naskah"); } }

        TextBox script { get { return well.Box; } }
        public string Script { get { return script.Text; } }
        public int Wpm { get { return speed.Value; } }
        public int Px { get { return size.Value; } }
        public bool Mirror { get { return mirror.Checked; } }
        public int Bg { get { return shade.Value; } }

        public PrompterSetup(string text, int wpm, int px, bool mirrored, int bgPct, bool live)
        {
            this.live = live;
            lastText = text;
            Text = Lang.T("Teleprompter");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            title.Text = Lang.T("Teleprompter");
            title.AutoSize = true;
            title.UseMnemonic = false;

            library.AccessibleName = Lang.T("Daftar naskah");
            library.Changed += i => Pick();
            Action<KitButton, string, Action> button = (b, label, act) => { b.Text = label; b.Click += delegate { act(); }; };
            button(import, Lang.T("Impor"), Import);
            button(keep, Lang.T("Simpan naskah"), Save);
            button(remove, Lang.T("Hapus"), Delete);
            button(cancel, Lang.T("Batal"), Close);
            button(start, Lang.T(live ? "Simpan" : "Mulai"), Submit);
            start.Primary = true;

            script.AccessibleName = Lang.T("Naskah");
            script.MaxLength = 200000;
            script.Text = Crlf(text);
            script.TextChanged += delegate { Count(); well.FitBars(); };
            foreach (var l in new[] { markHint, count, keysCap, sizeHint }) { l.UseMnemonic = false; l.AutoSize = false; }
            markHint.Text = Lang.T("Tulis [jeda] untuk berhenti otomatis.");
            count.TextAlign = ContentAlignment.MiddleRight;
            markHint.TextAlign = sizeHint.TextAlign = ContentAlignment.MiddleLeft;
            keysCap.Text = Lang.T("Pintasan, dengan Ctrl+Alt");
            sizeHint.Text = Lang.T("Saat jeda, tarik tepi island untuk mengubah ukurannya.");

            Slider(speedRow, speed, Lang.T("Kecepatan"), 60, 400, 20, wpm, v => Lang.F("{0} kata/menit", v));
            Slider(sizeRow, size, Lang.T("Ukuran huruf"), 15, 26, 1, px, v => v + " px");
            Slider(shadeRow, shade, Lang.T("Latar"), 15, 100, 5, bgPct, v => v + "%");
            mirror.Checked = mirrored;
            mirror.AccessibleName = mirrorRow.Title = Lang.T("Cermin");
            mirror.AccessibleDescription = mirrorRow.Detail = Lang.T("Untuk teleprompter kaca");
            mirrorRow.Field = mirror;
            mirror.CheckedChanged += delegate { Notify(); };
            foreach (var r in new[] { speedRow, sizeRow, shadeRow, mirrorRow }) { r.Controls.Add(r.Field); group.Controls.Add(r); }

            keys.Rows = new[]
            {
                new[] { Lang.T("Jeda dan lanjut"), Lang.T("Spasi") },
                new[] { Lang.T("Ulang dari awal"), "R" },
                new[] { Lang.T("Mundur atau maju 5 detik"), "←", "→" },
                new[] { Lang.T("Lebih cepat atau lambat"), "=", "−" },
                new[] { Lang.T("Buka jendela ini"), "P" },
                new[] { Lang.T("Hentikan"), "X" },
            };
            keys.AccessibleName = keysCap.Text;
            keys.AccessibleDescription = keys.Spoken();

            Controls.AddRange(new Control[] { title, library, import, keep, remove, well, markHint, count, group, keysCap, keys, sizeHint, cancel, start });
            int tab = 0;
            foreach (Control c in Controls) c.TabIndex = tab++;
            Fill(null);
            Count();
            ActiveControl = script;
            ready = true;
        }

        void Slider(KitRow row, KitSlider s, string name, int min, int max, int step, int value, Func<int, string> fmt)
        {
            s.Minimum = min;
            s.Maximum = max;
            s.Step = step;
            s.Value = Math.Max(min, Math.Min(max, (int)Math.Round((double)(value - min) / step) * step + min));
            s.Format = fmt;
            s.AccessibleName = row.Title = name;
            row.Detail = fmt(s.Value);
            row.Field = s;
            s.Changed += v =>
            {
                row.Detail = fmt(v);
                row.Invalidate();
                if (s == speed) Count();
                Notify();
            };
        }

        void Notify()
        {
            if (!ready) return;
            var h = Changed;
            if (h != null) h();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Small screens: shrink to fit
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            float fit = Math.Min((area.Width - 40) / (Ui * DesignW), (area.Height - 60) / (Ui * DesignH));
            if (fit < 1) Ui *= fit;
            Relayout();
            Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
        }

        const int DesignW = 820, DesignH = 632;

        public override void Relayout()
        {
            float s = Ui;
            Func<float, int> u = v => (int)Math.Round(v * s);
            BackColor = KitInk.Window;
            ForeColor = KitInk.Text;
            Native.DarkFrame(Handle, !Ink.Light);
            ClientSize = new Size(u(DesignW), u(DesignH));
            title.Font = UiFont(24f);
            title.ForeColor = KitInk.Text;
            title.BackColor = KitInk.Window;
            title.Location = new Point(u(28), u(20));
            foreach (var l in new[] { markHint, count, keysCap, sizeHint }) { l.Font = UiFont(12f); l.ForeColor = KitInk.Sub; l.BackColor = KitInk.Window; }

            // Left: library and script
            int left = u(28), leftW = u(428), top = u(74);
            int x = left + leftW;
            foreach (var b in new[] { remove, keep, import })
            {
                b.Font = UiFont(12.5f);
                int bw = TextRenderer.MeasureText(b.Text, b.Font).Width + u(32);
                x -= bw;
                b.SetBounds(x, top, bw, u(30));
                x -= u(8);
            }
            library.Font = UiFont(12.5f);
            library.SetBounds(left, top, x - left, u(30));
            int keysTop = u(358), keysH = u(198), bottom = keysTop + keysH;
            well.SetBounds(left, u(116), leftW, bottom - u(28) - u(116));
            well.Place(s);
            markHint.SetBounds(left + u(4), well.Bottom + u(6), u(260), u(20));
            count.SetBounds(left + leftW - u(164), well.Bottom + u(6), u(160), u(20));

            // Right: look rows and shortcuts
            int right = u(480), rightW = u(312), y = 0;
            foreach (Control c in group.Controls)
            {
                var r = (KitRow)c;
                r.SetBounds(0, y, rightW, u(60));
                if (r.Field is KitSlider) r.Field.Size = new Size(u(136), u(30));
                else r.Field.Size = new Size(u(46), u(26));
                r.Field.Location = new Point(r.Width - r.Field.Width - u(16), (r.Height - r.Field.Height) / 2);
                y += r.Height;
            }
            group.BackColor = KitInk.Group;
            group.SetBounds(right, top, rightW, y);
            keysCap.SetBounds(right + u(12), group.Bottom + u(18), rightW - u(12), u(20));
            keys.SetBounds(right, keysTop, rightW, keysH);

            // Footer: resize hint, then actions
            int fy = bottom + u(22), bh = u(32);
            start.Font = UiFont(13f);
            cancel.Font = UiFont(13f);
            int sw = Math.Max(u(96), TextRenderer.MeasureText(start.Text, start.Font).Width + u(36));
            int cw = Math.Max(u(84), TextRenderer.MeasureText(cancel.Text, cancel.Font).Width + u(36));
            start.SetBounds(right + rightW - sw, fy, sw, bh);
            cancel.SetBounds(start.Left - u(8) - cw, fy, cw, bh);
            sizeHint.SetBounds(left + u(4), fy, cancel.Left - left - u(20), bh);
            Invalidate(true);
        }

        void Submit()
        {
            if (string.IsNullOrWhiteSpace(script.Text)) { script.Focus(); return; }
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            // Ctrl+Enter starts from anywhere
            if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; Submit(); }
        }

        // Word count and reading time
        void Count()
        {
            string s = script.Text;
            int words = 0, i = 0, n = s.Length;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(s[i])) i++;
                int b = i;
                while (i < n && !char.IsWhiteSpace(s[i])) i++;
                int len = i - b;
                if (len == 0) break;
                bool mark = (len == 6 && string.Compare(s, b, "[jeda]", 0, 6, StringComparison.OrdinalIgnoreCase) == 0) ||
                            (len == 7 && string.Compare(s, b, "[pause]", 0, 7, StringComparison.OrdinalIgnoreCase) == 0);
                if (!mark) words++;
            }
            if (words == 0) { count.Text = ""; return; }
            int secs = (int)Math.Round(words * 60.0 / Math.Max(1, Wpm));
            count.Text = Lang.F("{0} kata · {1}", words, (secs / 60) + ":" + (secs % 60).ToString("00"));
        }

        // Saved scripts: pick, import, save, delete
        void Fill(string select)
        {
            picking = true;
            names.Clear();
            try
            {
                if (Directory.Exists(Folder))
                {
                    foreach (var f in Directory.GetFiles(Folder, "*.txt")) names.Add(Path.GetFileNameWithoutExtension(f));
                    names.Sort(StringComparer.CurrentCultureIgnoreCase);
                }
            }
            catch (Exception ex) { Log.Write("scripts list: " + ex.Message); }
            var items = new List<string> { Lang.T("Naskah terakhir") };
            items.AddRange(names);
            library.Items = items.ToArray();
            int at = select == null ? 0 : names.IndexOf(select) + 1;
            library.Selected = Math.Max(0, at);
            remove.Enabled = library.Selected > 0;
            picking = false;
        }

        void Pick()
        {
            remove.Enabled = library.Selected > 0;
            if (picking) return;
            if (library.Selected <= 0) { script.Text = Crlf(lastText); return; }
            try { script.Text = Crlf(File.ReadAllText(Path.Combine(Folder, names[library.Selected - 1] + ".txt"))); }
            catch (Exception ex) { Log.Write("script read: " + ex.Message); }
        }

        static string Crlf(string s) { return (s ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        // Name from the first line, safe for a file name
        static string NameFor(string text)
        {
            string first = text.Trim();
            int nl = first.IndexOfAny(new[] { '\r', '\n' });
            if (nl > 0) first = first.Substring(0, nl);
            foreach (char bad in Path.GetInvalidFileNameChars()) first = first.Replace(bad, ' ');
            first = first.Trim().TrimEnd('.');
            if (first.Length > 40) first = first.Substring(0, 40).Trim();
            return first.Length > 0 ? first : Lang.T("Naskah");
        }

        void Save()
        {
            if (string.IsNullOrWhiteSpace(script.Text)) { script.Focus(); return; }
            string name = library.Selected > 0 ? names[library.Selected - 1] : NameFor(script.Text);
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(Path.Combine(Folder, name + ".txt"), script.Text);
            }
            catch (Exception ex) { Log.Write("script save: " + ex.Message); return; }
            Fill(name);
        }

        void Delete()
        {
            if (library.Selected <= 0) return;
            try { File.Delete(Path.Combine(Folder, names[library.Selected - 1] + ".txt")); }
            catch (Exception ex) { Log.Write("script delete: " + ex.Message); }
            Fill(null);
            script.Text = Crlf(lastText);
            script.Focus();
        }

        void Import()
        {
            using (var open = new OpenFileDialog { Filter = Lang.T("Teks atau Markdown") + " (*.txt;*.md)|*.txt;*.md", Title = Lang.T("Impor naskah") })
            {
                if (open.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var info = new FileInfo(open.FileName);
                    if (info.Length > 400000) { script.Text = ""; return; }
                    string text = File.ReadAllText(open.FileName);
                    if (open.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) text = PlainMarkdown(text);
                    picking = true;
                    library.Selected = 0;
                    remove.Enabled = false;
                    picking = false;
                    script.Text = Crlf(text.Trim());
                }
                catch (Exception ex) { Log.Write("script import: " + ex.Message); }
            }
        }

        // Read-aloud text: drop Markdown syntax, keep the words
        static string PlainMarkdown(string md)
        {
            var sb = new StringBuilder();
            bool fence = false;
            foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.TrimStart().StartsWith("```")) { fence = !fence; continue; }
                if (fence) continue;
                line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s{0,3}#{1,6}\s+", "");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*([-*+]|\d+[.)])\s+", "");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*>\s?", "");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"!\[[^\]]*\]\([^)]*\)", "");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"(\*\*|__|\*|_|`|~~)(.+?)\1", "$2");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"<[^>]+>", "");
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*([-*_]\s*){3,}$")) line = "";
                sb.Append(line).Append('\n');
            }
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n");
        }
    }

    // Rounded well around a plain multiline TextBox
    class ScriptWell : Panel
    {
        public readonly TextBox Box = new TextBox();
        float ui = 1;

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        public ScriptWell()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Box.BorderStyle = BorderStyle.None;
            Box.Multiline = true;
            Box.AcceptsReturn = true;
            Box.WordWrap = true;
            Box.ScrollBars = ScrollBars.None;
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            // Scrollbar follows the field tone
            Box.HandleCreated += delegate { try { SetWindowTheme(Box.Handle, KitInk.Group.GetBrightness() > 0.5f ? "Explorer" : "DarkMode_Explorer", null); } catch (Exception) { } };
            MouseDown += delegate { Box.Focus(); };
            Controls.Add(Box);
        }

        public void Place(float ui)
        {
            this.ui = ui;
            Box.Font = new Font(Ink.MenuFamily, 14f * ui, GraphicsUnit.Pixel);
            Box.BackColor = KitInk.Group;
            Box.ForeColor = KitInk.Text;
            Box.SetBounds((int)(16 * ui), (int)(12 * ui), Width - (int)(22 * ui), Height - (int)(24 * ui));
            FitBars();
            Invalidate();
        }

        // Scrollbar only once the script overflows
        public void FitBars()
        {
            bool over;
            if (Box.IsHandleCreated) over = (Box.GetLineFromCharIndex(Box.TextLength) + 1) * Box.Font.Height > Box.ClientSize.Height;
            else
            {
                // No handle yet: measure the wrap
                var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
                over = TextRenderer.MeasureText(Box.Text, Box.Font, new Size(Box.Width, int.MaxValue), flags).Height > Box.Height;
            }
            var want = over ? ScrollBars.Vertical : ScrollBars.None;
            if (Box.ScrollBars != want) Box.ScrollBars = want;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(KitInk.Under(this));
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Ink.Round(r, 12 * ui))
            {
                using (var b = new SolidBrush(KitInk.Group)) g.FillPath(b, path);
                using (var pen = new Pen(Box.Focused ? KitInk.Faint : KitInk.Line, Box.Focused ? 1.6f : 1f)) g.DrawPath(pen, path);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Box.Font != null) Box.Font.Dispose();
            base.Dispose(disposing);
        }
    }

    // Shortcut rows: action left, key caps right
    class KeyList : Control
    {
        public string[][] Rows = new string[0][];

        public KeyList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
        }

        float Ui { get { var w = FindForm() as KitWindow; return w != null ? w.Ui : 1f; } }

        public string Spoken()
        {
            var sb = new StringBuilder();
            foreach (var r in Rows)
            {
                sb.Append(r[0]).Append(": Ctrl+Alt+").Append(r[1]);
                for (int i = 2; i < r.Length; i++) sb.Append(" / ").Append(r[i]);
                sb.Append(". ");
            }
            return sb.ToString().Trim();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            KitInk.Smooth(g);
            g.Clear(KitInk.Under(this));
            float s = Ui, rowH = (Height - 12 * s) / Math.Max(1, Rows.Length);
            using (var path = Ink.Round(new RectangleF(0, 0, Width - 1, Height - 1), 12 * s))
            using (var b = new SolidBrush(KitInk.Group)) g.FillPath(b, path);
            using (var f = new Font(Ink.MenuFamily, 13f * s, GraphicsUnit.Pixel))
            using (var kf = new Font(Ink.MenuFamily, 11.5f * s, GraphicsUnit.Pixel))
            using (var cap = new SolidBrush(KitInk.Line))
                for (int i = 0; i < Rows.Length; i++)
                {
                    float y = 6 * s + i * rowH, x = Width - 16 * s;
                    // Caps laid right to left
                    for (int k = Rows[i].Length - 1; k >= 1; k--)
                    {
                        float w = Math.Max(22 * s, TextRenderer.MeasureText(Rows[i][k], kf).Width + 8 * s);
                        var r = new RectangleF(x - w, y + (rowH - 22 * s) / 2, w, 22 * s);
                        using (var cp = Ink.Round(r, 6 * s)) g.FillPath(cap, cp);
                        KitInk.Draw(g, Rows[i][k], kf, KitInk.Text, r, ContentAlignment.MiddleCenter);
                        x = r.X - 5 * s;
                    }
                    KitInk.Draw(g, Rows[i][0], f, KitInk.Text, new RectangleF(16 * s, y, x - 24 * s, rowH), ContentAlignment.MiddleLeft);
                }
        }
    }
}
