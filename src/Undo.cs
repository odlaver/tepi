using System;
using System.Collections.Generic;
using System.Drawing;
using WinTimer = System.Windows.Forms.Timer;

namespace Tepi
{
    // Removals wait 5 s behind an Undo bar, then commit
    partial class Island
    {
        sealed class Pending
        {
            public string Text;
            public Action Commit, Restore;
            public DateTime Since;
        }

        const int UndoMs = 5000;
        Pending pending;
        WinTimer pendingTimer;
        // Messages hidden while their removal can still be undone
        readonly HashSet<uint> notifHidden = new HashSet<uint>();

        void Undoable(string text, Action commit, Action restore)
        {
            CommitPending();
            pending = new Pending { Text = text, Commit = commit, Restore = restore, Since = DateTime.UtcNow };
            Announce(text + ". " + Lang.T("Urungkan: Ctrl+Z dalam mode keyboard"));
            if (pendingTimer == null)
            {
                pendingTimer = new WinTimer { Interval = UndoMs };
                pendingTimer.Tick += delegate { CommitPending(); };
            }
            pendingTimer.Stop();
            pendingTimer.Start();
            Kick();
        }

        void CommitPending()
        {
            var was = pending;
            pending = null;
            if (pendingTimer != null) pendingTimer.Stop();
            if (was == null) return;
            try { was.Commit(); } catch (Exception ex) { Log.Write("undo commit: " + ex.Message); }
            Kick();
        }

        void UndoPending()
        {
            var was = pending;
            pending = null;
            if (pendingTimer != null) pendingTimer.Stop();
            if (was == null) return;
            try { was.Restore(); } catch (Exception ex) { Log.Write("undo restore: " + ex.Message); }
            Kick();
        }

        // Messages leave the list now, Windows later
        void RemoveNotifsLater(List<NotifItem> items, string text)
        {
            var at = new List<int>();
            foreach (var n in items) at.Add(notifs.IndexOf(n));
            int unreadWas = unread;
            foreach (var n in items) { notifs.Remove(n); notifHidden.Add(n.Id); }
            unread = Math.Min(unread, notifs.Count);
            Undoable(text, delegate
            {
                foreach (var n in items) notifHidden.Remove(n.Id);
                RemoveNotifs(items);
            }, delegate
            {
                for (int i = 0; i < items.Count; i++)
                {
                    notifHidden.Remove(items[i].Id);
                    if (notifs.Contains(items[i])) continue;
                    notifs.Insert(Math.Max(0, Math.Min(notifs.Count, at[i])), items[i]);
                }
                unread = Math.Min(unreadWas, notifs.Count);
            });
        }

        // Task leaves the list now, its source later
        void MarkDoneLater(KuliaTask task)
        {
            if (task.ReadOnly || tasks == null) return;
            int at = tasks.IndexOf(task);
            tasks.Remove(task);
            Undoable(Lang.T("Ditandai selesai"), delegate
            {
                if (tasks != null && !tasks.Contains(task)) tasks.Add(task);
                MarkDone(task);
            }, delegate
            {
                if (tasks != null && !tasks.Contains(task)) tasks.Insert(Math.Max(0, Math.Min(tasks.Count, at)), task);
            });
        }

        void RemoveShelfLater(List<ShelfItem> items, string text)
        {
            var at = new List<int>();
            foreach (var it in items) at.Add(shelf.IndexOf(it));
            foreach (var it in items) { shelf.Remove(it); shelfPicked.Remove(it.Id); }
            Undoable(text, delegate
            {
                foreach (var it in items) if (it.Thumb != null) it.Thumb.Dispose();
                SaveShelf();
            }, delegate
            {
                for (int i = 0; i < items.Count; i++)
                    if (!shelf.Contains(items[i])) shelf.Insert(Math.Max(0, Math.Min(shelf.Count, at[i])), items[i]);
            });
        }

        void ClearClipsLater()
        {
            var was = new List<ClipItem>(clips);
            clips.Clear();
            Undoable(Lang.T("Riwayat salin dikosongkan"), delegate { }, delegate
            {
                foreach (var c in was) if (!clips.Contains(c)) clips.Add(c);
            });
        }

        // Bar over the panel's foot while an Undo is open
        void DrawUndoBar(float w, float h)
        {
            if (pending == null) return;
            var f = Ink.Semi(12.5f);
            string undo = Lang.T("Urungkan");
            float uw = p.Measure(undo, f) + 26, tw = Math.Min(w - 120, p.Measure(pending.Text, f));
            float bw = 20 + tw + 12 + uw + 6, bh = 36;
            float k = Ease.OutCubic((float)(DateTime.UtcNow - pending.Since).TotalSeconds / 0.2f);
            var bar = new RectangleF(w / 2 - bw / 2, h - bh - 14 + 8 * (1 - k) * Moves, bw, bh);
            float keep = p.Alpha;
            p.Alpha = keep * k;
            p.FillRound(bar, bh / 2, Ink.Mix(p.T.Bg, p.T.Text, 0.14f));
            p.StrokeRound(bar, bh / 2, p.T.Line, 1f);
            p.Text(pending.Text, f, p.T.Text, bar.X + 18, bar.Y + bh / 2, tw, StringAlignment.Near);
            var btn = new RectangleF(bar.Right - uw - 4, bar.Y + 4, uw, bh - 8);
            AddHit(btn, "undo", UndoPending);
            float s = Squish("undo", btn, 0.94f);
            p.FillRound(btn, btn.Height / 2, Pressed("undo") ? Ink.Mix(p.T.Accent, p.T.OnAccent, 0.2f) : Ink.Mix(p.T.Accent, p.T.OnAccent, 0.1f * HoverK("undo")));
            p.TextCenter(undo, f, p.T.OnAccent, btn.X + btn.Width / 2, btn.Y + btn.Height / 2);
            Unsquish(btn, s);
            p.Alpha = keep;
        }

        bool UndoArriving() { return pending != null && (DateTime.UtcNow - pending.Since).TotalSeconds < 0.22; }
    }
}
