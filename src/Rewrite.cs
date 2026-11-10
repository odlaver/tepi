using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // Selected text: fix or translate in place
    partial class Island
    {
        const int FixHotkey = 22, TranslateHotkey = 23;
        const int RewriteMax = 8000;
        bool rewriting;

        // Clipboard writes by Tepi itself, not previews
        DateTime clipQuietUtc;

        void RewriteSelection(bool translate)
        {
            if (rewriting || suruhBusy || dictState != DictState.Off) return;
            if (!Ai.Ready(Ai.Text)) { DictSay(Lang.T("Layanan teks belum diatur"), Lang.T("Pengaturan › AI"), true); return; }
            IntPtr target = GetAncestor(GetForegroundWindow(), 3);
            if (target == IntPtr.Zero || target == Handle) return;
            // Empty clipboard comes back empty
            var original = Snapshot() ?? new DataObject();
            uint seq = GetClipboardSequenceNumber();
            rewriting = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var keys = Chord(0x43);
                SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(INPUT)));
                // Wait for the copy to land
                for (int i = 0; i < 24 && GetClipboardSequenceNumber() == seq; i++) Thread.Sleep(25);
                bool copied = GetClipboardSequenceNumber() != seq;
                Post(delegate { RewriteCopied(translate, target, original, copied); });
            });
        }

        // Ctrl+key with held hotkey mods lifted first
        static INPUT[] Chord(ushort vk)
        {
            for (int i = 0; i < 100 && AnyHeld(0x10, 0x11, 0x12, 0x5B, 0x5C); i++) Thread.Sleep(15);
            var keys = new List<INPUT>();
            // Dummy key stops Alt opening menus
            if (AnyHeld(0x12)) { keys.Add(Key(0xE8, '\0', 0)); keys.Add(Key(0xE8, '\0', 2)); }
            foreach (ushort m in new ushort[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
                if (AnyHeld(m)) keys.Add(Key(m, '\0', 2));
            keys.AddRange(new[] { Key(0x11, '\0', 0), Key(vk, '\0', 0), Key(vk, '\0', 2), Key(0x11, '\0', 2) });
            return keys.ToArray();
        }

        // Editors copy the whole line when nothing is selected
        static bool WholeLineCopy()
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return false;
                if (data.GetDataPresent("VisualStudioEditorOperationsLineCutCopyClipboardTag")) return true;
                if (data.GetDataPresent("vscode-editor-data"))
                {
                    var raw = data.GetData("vscode-editor-data");
                    string json = raw as string;
                    var ms = raw as System.IO.MemoryStream;
                    if (json == null && ms != null) json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                    if (json != null && json.Replace(" ", "").Contains("\"isFromEmptySelection\":true")) return true;
                }
            }
            catch (ExternalException) { }
            return false;
        }

        void RewriteCopied(bool translate, IntPtr target, DataObject original, bool copied)
        {
            string text = null;
            try { if (copied && !WholeLineCopy()) text = Clipboard.GetText(); } catch (ExternalException) { }
            if (string.IsNullOrWhiteSpace(text))
            {
                rewriting = false;
                Restore(original);
                DictSay(Lang.T("Tidak ada teks terpilih"), Lang.T("Blok teksnya dulu"), true);
                return;
            }
            if (text.Length > RewriteMax) { rewriting = false; Restore(original); DictSay(Lang.T("Teks terlalu panjang"), Lang.F("Maksimal {0} karakter", RewriteMax), true); return; }
            dictState = DictState.Working;
            Kick();
            string system = translate
                ? "Translate the user's text. If it is Indonesian, translate to English; otherwise translate to Indonesian. Keep line breaks, formatting, names, and code unchanged. Reply with the translation only, no notes."
                : "Fix spelling, grammar, and punctuation in the user's text. Keep its language, meaning, tone, line breaks, names, and code. Reply with the corrected text only, no notes.";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                string result = Ai.Chat(system, text, out error);
                Post(delegate
                {
                    rewriting = false;
                    dictState = DictState.Off;
                    Kick();
                    if (string.IsNullOrWhiteSpace(result)) { Restore(original); DictSay(error ?? Lang.T("Tidak ada jawaban"), "", true); return; }
                    PasteText(result.Trim(), target, translate ? Lang.T("Diterjemahkan") : Lang.T("Diperbaiki"), original);
                });
            });
        }

        void Restore(DataObject original)
        {
            clipQuietUtc = DateTime.UtcNow.AddSeconds(1.5);
            try
            {
                if (original != null && original.GetFormats(false).Length > 0) Clipboard.SetDataObject(original, false, 5, 40);
                else Clipboard.Clear();
            }
            catch (Exception ex) { Log.Write("pulihkan clipboard: " + ex.Message); }
        }
    }
}
