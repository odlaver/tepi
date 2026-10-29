using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
        static void ReadAsset(object release, out string url, out string digest, out DateTime uploaded)
        {
            url = digest = null;
            uploaded = DateTime.MinValue;
            var assets = Json.Arr(release, "assets");
            if (assets == null) return;
            foreach (var a in assets)
            {
                if (Json.Str(a, "name") != "Tepi.exe") continue;
                string u = Json.Str(a, "browser_download_url"), d = Json.Str(a, "digest");
                if (!u.StartsWith(AssetPrefix, StringComparison.Ordinal) || !d.StartsWith("sha256:", StringComparison.Ordinal) || d.Length != 71) return;
                url = u;
                digest = d.Substring(7).ToLowerInvariant();
                DateTime.TryParse(Json.Str(a, "updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out uploaded);
            }
        }

        static string ownDigest;

        // SHA-256 of the running exe, once
        static string OwnDigest()
        {
            if (ownDigest != null) return ownDigest;
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(Application.ExecutablePath))
                ownDigest = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            return ownDigest;
        }

        void InstallUpdate()
        {
            if (updating) return;
            if (newAsset == null || newDigest == null) { if (newUrl != null) OpenUrl(newUrl); return; }
            updating = true;
            if (UpdateChanged != null) UpdateChanged();
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
                    if (UpdateChanged != null) UpdateChanged();
                    if (error == null)
                    {
                        Log.Write("update: installed " + version);
                        // Open dialogs would keep this copy alive
                        foreach (var f in new List<Form>(Application.OpenForms.Cast<Form>())) if (f != this) f.Close();
                        Close();
                        return;
                    }
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
            catch
            {
                // Restore the running copy, always
                try { MoveRetry(old, exe); }
                catch (Exception ex)
                {
                    Log.Write("update restore: " + ex.Message);
                    File.Copy(old, exe, true);
                }
                try { File.Delete(fresh); } catch (Exception) { }
                throw;
            }
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
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            foreach (var name in new[] { "Tepi.old.exe", "Tepi.new.exe" })
                try
                {
                    string leftover = Path.Combine(dir, name);
                    if (File.Exists(leftover)) File.Delete(leftover);
                }
                catch (Exception ex) { Log.Write("update cleanup: " + ex.Message); }
        }
    }
}
