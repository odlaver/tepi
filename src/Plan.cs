using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Tepi
{
    // One step of an agent's plan
    class PlanStep
    {
        public string Text = "", Active = "";
        // 0 pending, 1 working, 2 done
        public int Status;
    }

    // One diff line in a permission card
    class DiffLine
    {
        // Kind: context, added, removed, header (0-3)
        public int Kind;
        public string Text = "";
    }

    static class PlanReader
    {
        const int MaxSteps = 50, MaxText = 200;

        // TodoWrite (Claude), update_plan (Codex), write_todos (Gemini)
        public static List<PlanStep> Read(string tool, Dictionary<string, object> input)
        {
            if (input == null) return null;
            object[] items;
            string textKey, activeKey = null;
            if (tool == "TodoWrite") { items = Json.Arr(input, "todos"); textKey = "content"; activeKey = "activeForm"; }
            else if (tool == "update_plan") { items = Json.Arr(input, "plan"); textKey = "step"; }
            else if (tool == "write_todos") { items = Json.Arr(input, "todos"); textKey = "description"; }
            else return null;
            var list = new List<PlanStep>();
            foreach (var o in items)
            {
                if (list.Count >= MaxSteps) break;
                string text = Fmt.Visible(Fmt.OneLine(Json.Str(o, textKey), MaxText));
                if (text.Length == 0) continue;
                string st = Json.Str(o, "status").ToLowerInvariant();
                int status = st == "completed" || st == "done" || st == "cancelled" ? 2 : st.StartsWith("in") ? 1 : 0;
                string active = activeKey != null ? Fmt.Visible(Fmt.OneLine(Json.Str(o, activeKey), MaxText)) : "";
                list.Add(new PlanStep { Text = text, Active = active, Status = status });
            }
            return list;
        }
    }

    static class DiffBuilder
    {
        const int MaxLines = 300;

        // From structured input, never display text
        public static List<DiffLine> For(string tool, Dictionary<string, object> input)
        {
            if (input == null) return null;
            var lines = new List<DiffLine>();
            string path = Json.Str(input, "file_path");
            if (tool == "Edit")
            {
                lines.Add(Head(path + (Json.Str(input, "replace_all") == "True" ? Lang.T(" · semua kemunculan") : "")));
                Lines(lines, Json.Str(input, "old_string"), Json.Str(input, "new_string"));
            }
            else if (tool == "MultiEdit")
            {
                lines.Add(Head(path));
                var edits = Json.Arr(input, "edits");
                for (int i = 0; i < edits.Length; i++)
                {
                    lines.Add(Head("@@ " + (i + 1) + "/" + edits.Length + (Json.Str(edits[i], "replace_all") == "True" ? Lang.T(" · semua kemunculan") : "")));
                    Lines(lines, Json.Str(edits[i], "old_string"), Json.Str(edits[i], "new_string"));
                }
            }
            else if (tool == "Write")
            {
                lines.Add(Head(path));
                foreach (var l in Split(Json.Str(input, "content"))) lines.Add(new DiffLine { Kind = 1, Text = l });
            }
            else if (tool == "apply_patch")
            {
                // Codex patch: its marks are the diff
                string patch = Json.Str(input, "input");
                if (patch.Length == 0) patch = Json.Str(input, "patch");
                foreach (var l in Split(patch))
                {
                    int kind = l.StartsWith("***") || l.StartsWith("@@") ? 3 : l.StartsWith("+") ? 1 : l.StartsWith("-") ? 2 : 0;
                    lines.Add(new DiffLine { Kind = kind, Text = kind == 1 || kind == 2 ? l.Substring(1) : l });
                }
            }
            else return null;
            foreach (var d in lines) d.Text = Fmt.Visible(d.Text.Replace("\t", "    "));
            return lines;
        }

        static DiffLine Head(string text) { return new DiffLine { Kind = 3, Text = text }; }

        static string[] Split(string s) { return (s ?? "").Replace("\r", "").Split('\n'); }

        // Capped line LCS; else all-out, all-in
        static void Lines(List<DiffLine> into, string before, string after)
        {
            var a = Split(before);
            var b = Split(after);
            if (a.Length > MaxLines || b.Length > MaxLines)
            {
                foreach (var l in a) into.Add(new DiffLine { Kind = 2, Text = l });
                foreach (var l in b) into.Add(new DiffLine { Kind = 1, Text = l });
                return;
            }
            var lcs = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
                for (int j = b.Length - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < a.Length || y < b.Length)
            {
                if (x < a.Length && y < b.Length && a[x] == b[y]) { into.Add(new DiffLine { Kind = 0, Text = a[x] }); x++; y++; }
                else if (y < b.Length && (x >= a.Length || lcs[x, y + 1] >= lcs[x + 1, y])) { into.Add(new DiffLine { Kind = 1, Text = b[y] }); y++; }
                else { into.Add(new DiffLine { Kind = 2, Text = a[x] }); x++; }
            }
        }

        public static int Count(List<DiffLine> lines, int kind)
        {
            int n = 0;
            foreach (var d in lines) if (d.Kind == kind) n++;
            return n;
        }
    }

    partial class Island
    {
        // Unknown plan tools: log names, keys only
        readonly HashSet<string> planToolsSeen = new HashSet<string>();

        void TakePlan(AgentSession s, string tool, Dictionary<string, object> input)
        {
            var plan = PlanReader.Read(tool, input);
            if (plan != null)
            {
                s.Plan = plan;
                return;
            }
            if ((tool.StartsWith("Task") || tool.IndexOf("todo", StringComparison.OrdinalIgnoreCase) >= 0) && input != null && planToolsSeen.Add(tool))
                Log.Write("plan tool seen: " + tool + " keys " + string.Join(",", new List<string>(input.Keys).ToArray()));
        }

        static int PlanDone(AgentSession s)
        {
            int n = 0;
            if (s.Plan != null) foreach (var st in s.Plan) if (st.Status == 2) n++;
            return n;
        }

        static string PlanCount(AgentSession s)
        {
            return s.Plan == null || s.Plan.Count == 0 ? "" : PlanDone(s) + "/" + s.Plan.Count;
        }

        // Current step in its doing wording
        static string PlanNow(AgentSession s)
        {
            if (s.Plan == null) return "";
            foreach (var st in s.Plan) if (st.Status == 1) return st.Active.Length > 0 ? st.Active : st.Text;
            return "";
        }

        // Segment per step; live one breathes
        void DrawPlanBar(AgentSession s, RectangleF r)
        {
            if (s == null || s.Plan == null || s.Plan.Count == 0) return;
            int n = s.Plan.Count;
            float breath = Anim.Reduced ? 0.6f : 0.45f + 0.35f * (float)(0.5 + 0.5 * Math.Sin(clock.Elapsed.TotalSeconds * Math.PI * 1.4));
            if (n > 14)
            {
                p.FillRound(r, r.Height / 2, p.T.Line);
                p.FillRound(new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * PlanDone(s) / n), r.Height), r.Height / 2, p.T.Progress);
                return;
            }
            float gap = 3, w = (r.Width - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
            {
                int st = s.Plan[i].Status;
                var seg = new RectangleF(r.X + i * (w + gap), r.Y, w, r.Height);
                p.FillRound(seg, r.Height / 2, st == 2 ? p.T.Progress : st == 1 ? Ink.A(p.T.Progress, breath) : p.T.Line);
            }
        }

        // Checklist rows under an agent session
        const float PlanRowH = 22;

        float PlanListH(AgentSession s) { return s.Plan == null ? 0 : s.Plan.Count * PlanRowH + 8; }

        void DrawPlanList(AgentSession s, float x, float y, float w)
        {
            if (s.Plan == null) return;
            for (int i = 0; i < s.Plan.Count; i++)
            {
                var st = s.Plan[i];
                float cy = y + i * PlanRowH + PlanRowH / 2;
                if (st.Status == 2)
                {
                    p.Circle(x + 7, cy, 7, p.T.Sub);
                    p.Check(x + 7, cy, 8, p.T.Bg, 1);
                }
                else if (st.Status == 1) p.Orbit(x + 7, cy, 6f, orbitPhase, p.T.Text);
                else g.DrawEllipse(p.P(p.T.Faint, 1.4f), x + 1, cy - 6, 12, 12);
                p.Text(st.Status == 1 && st.Active.Length > 0 ? st.Active : st.Text, st.Status == 1 ? Ink.Semi(12f) : Ink.Text(12f),
                       st.Status == 2 ? p.T.Faint : st.Status == 1 ? p.T.Text : p.T.Sub, x + 22, cy, w - 22, StringAlignment.Near);
            }
        }

        // Diff rows in the permission card

        // Wrapped visual rows for the box
        static List<KeyValuePair<DiffLine, string>> WrapDiff(List<DiffLine> lines, int perLine)
        {
            var rows = new List<KeyValuePair<DiffLine, string>>();
            perLine = Math.Max(8, perLine);
            foreach (var d in lines)
            {
                string t = d.Text;
                if (t.Length == 0) { rows.Add(new KeyValuePair<DiffLine, string>(d, "")); continue; }
                for (int i = 0; i < t.Length; i += perLine) rows.Add(new KeyValuePair<DiffLine, string>(d, t.Substring(i, Math.Min(perLine, t.Length - i))));
            }
            return rows;
        }

        void DrawDiff(List<KeyValuePair<DiffLine, string>> rows, RectangleF box, int first, int count, float lineH)
        {
            var mono = Ink.Mono(12f);
            for (int i = 0; i < count && first + i < rows.Count; i++)
            {
                var row = rows[first + i];
                float y = box.Y + i * lineH;
                var band = new RectangleF(box.X - 8, y, box.Width + 16, lineH);
                int kind = row.Key.Kind;
                if (kind == 1) g.FillRectangle(p.B(Ink.A(p.T.Positive, 0.16f)), band);
                else if (kind == 2) g.FillRectangle(p.B(Ink.A(p.T.Negative, 0.16f)), band);
                string mark = kind == 1 ? "+" : kind == 2 ? "−" : "";
                Color ink = kind == 1 ? p.T.Positive : kind == 2 ? p.T.Negative : kind == 3 ? p.T.Sub : p.T.Text;
                if (mark.Length > 0) p.Text(mark, mono, ink, box.X - 6, y + lineH / 2, 12, StringAlignment.Near);
                p.Text(row.Value, kind == 3 ? Ink.Semi(11.5f) : mono, kind == 0 ? p.T.Text : kind == 3 ? p.T.Sub : Ink.Mix(p.T.Text, ink, 0.35f), box.X + 8, y + lineH / 2, box.Width - 8, StringAlignment.Near);
            }
        }
    }
}
