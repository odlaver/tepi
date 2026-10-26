using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace Tepi
{
    // One-click update: download, verify SHA-256, swap, restart
    partial class Island
    {
        const string AssetPrefix = "https://github.com/odlaver/tepi/releases/download/";
        const long AssetMax = 20 * 1024 * 1024;
        string newAsset, newDigest;
        bool updating;

        // From the release JSON; null unless it is ours and hashed
        static void ReadAsset(object release, out string url, out string digest)
        {
            url = digest = null;
            var assets = Json.Arr(release, "assets");
            if (assets == null) return;
            foreach (var a in assets)
            {
                if (Json.Str(a, "name") != "Tepi.exe") continue;
                string u = Json.Str(a, "browser_download_url"), d = Json.Str(a, "digest");
                if (!u.StartsWith(AssetPrefix, StringComparison.Ordinal) || !d.StartsWith("sha256:", StringComparison.Ordinal) || d.Length != 71) return;
                url = u;
                digest = d.Substring(7).ToLowerInvariant();
            }
        }

        void InstallUpdate()
        {
            if (updating) return;
            if (newAsset == null || newDigest == null) { if (newUrl != null) OpenUrl(newUrl); return; }
            updating = true;
            ShowInfo(Ink.IcoRefresh, Lang.F("Mengunduh Tepi {0}", newVersion), "", true);
            string url = newAsset, digest = newDigest, version = newVersion;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { error = Swap(url, digest); }
                catch (Exception ex) { error = ex.Message; }
                Post(() =>
                {
                    updating = false;
                    if (error == null) { Log.Write("update: installed " + version); Close(); return; }
                    if (error == Restart) { ShowInfo(Ink.IcoRefresh, Lang.F("Tepi {0} terpasang", version), Lang.T("buka ulang Tepi"), true); return; }
                    Log.Write("update failed: " + error);
                    ShowInfo(Ink.IcoRefresh, Lang.T("Pembaruan gagal"), Lang.T("halaman rilis dibuka"), true);
                    if (newUrl != null) OpenUrl(newUrl);
                });
            });
        }

        static string Swap(string url, string digest)
        {
            byte[] data;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Tepi/" + AppVersion;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 30000;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var stream = res.GetResponseStream())
            using (var mem = new MemoryStream())
            {
                if (res.ResponseUri.Scheme != Uri.UriSchemeHttps) return "bukan https";
                var buf = new byte[65536];
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    mem.Write(buf, 0, n);
                    if (mem.Length > AssetMax) return "berkas terlalu besar";
                }
                data = mem.ToArray();
            }
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            if (hash != digest) return "SHA-256 tidak cocok";

            string exe = Application.ExecutablePath, dir = Path.GetDirectoryName(exe);
            string fresh = Path.Combine(dir, "Tepi.new.exe"), old = Path.Combine(dir, "Tepi.old.exe");
            File.WriteAllBytes(fresh, data);
            // Once releases are signed, the next one must be signed by the same publisher
            string mine = Signature.Signer(exe);
            if (mine != null && Signature.Signer(fresh) != mine)
            {
                try { File.Delete(fresh); } catch (IOException) { }
                return "tanda tangan tidak cocok";
            }
            if (File.Exists(old)) File.Delete(old);
            // A running exe can be renamed, not overwritten
            MoveRetry(exe, old);
            try { MoveRetry(fresh, exe); }
            catch { MoveRetry(old, exe); throw; }
            try { Process.Start(new ProcessStartInfo(exe, "--after " + Process.GetCurrentProcess().Id) { UseShellExecute = false, WorkingDirectory = dir }); }
            catch (Exception ex) { Log.Write("update restart: " + ex.Message); return Restart; }
            return null;
        }

        const string Restart = "restart";

        // Antivirus may hold a fresh exe for a moment
        static void MoveRetry(string from, string to)
        {
            for (int i = 0; ; i++)
            {
                try { File.Move(from, to); return; }
                catch (IOException) { if (i >= 9) throw; }
                catch (UnauthorizedAccessException) { if (i >= 9) throw; }
                Thread.Sleep(300);
            }
        }

        // Leftover from the previous version
        static void CleanUpdate()
        {
            try
            {
                string old = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Tepi.old.exe");
                if (File.Exists(old)) File.Delete(old);
            }
            catch (Exception ex) { Log.Write("update cleanup: " + ex.Message); }
        }
    }
}
