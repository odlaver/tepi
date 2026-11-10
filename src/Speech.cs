using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Tepi
{
    class SpeechSegment
    {
        public double Start, End;
        public string Text;
    }

    class SpeechResult
    {
        public string Text = "", Error;
        // Groq unreachable: offline may stand in
        public bool NoNet;
        public List<SpeechSegment> Segments = new List<SpeechSegment>();
        public bool Ok { get { return Error == null; } }
    }

    // Speech to text: any provider or whisper.cpp
    static class Speech
    {
        public static SpeechResult Remote(AiProvider p, string key, string baseUrl, string model, byte[] wav, string lang, string prompt, bool segments)
        {
            var parts = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("model", model),
                new KeyValuePair<string, string>("temperature", "0"),
                new KeyValuePair<string, string>("response_format", segments ? "verbose_json" : "json"),
            };
            if (!string.IsNullOrEmpty(lang)) parts.Add(new KeyValuePair<string, string>("language", lang));
            if (!string.IsNullOrEmpty(prompt)) parts.Add(new KeyValuePair<string, string>("prompt", prompt));
            var r = Multipart(baseUrl + "audio/transcriptions", Ai.Headers(p, key), parts, wav, Math.Max(30000, wav.Length / 40));
            var res = new SpeechResult();
            if (!r.Ok) { res.Error = Ai.Error(p, r); res.NoNet = r.Status == 0; return res; }
            var d = Json.Parse(r.Body) as Dictionary<string, object>;
            object text;
            if (d == null || !d.TryGetValue("text", out text)) { res.Error = Lang.F("Jawaban {0} tidak terbaca", Ai.Label(p)); return res; }
            res.Text = (text as string ?? "").Trim();
            object segs;
            if (segments && d.TryGetValue("segments", out segs) && segs is IEnumerable)
                foreach (var o in (IEnumerable)segs)
                {
                    var s = o as Dictionary<string, object>;
                    if (s == null) continue;
                    res.Segments.Add(new SpeechSegment { Start = Num(s, "start"), End = Num(s, "end"), Text = (s.ContainsKey("text") ? s["text"] as string ?? "" : "").Trim() });
                }
            // No segments back: one span per chunk
            if (segments && res.Segments.Count == 0 && res.Text.Length > 0) res.Segments.Add(new SpeechSegment { Start = 0, End = 0, Text = res.Text });
            return res;
        }

        static double Num(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;
        }

        static WebResult Multipart(string url, Dictionary<string, string> headers, List<KeyValuePair<string, string>> fields, byte[] wav, int timeoutMs)
        {
            string boundary = "tepi" + Guid.NewGuid().ToString("N");
            var body = new MemoryStream();
            Action<string> put = s => { var b = Encoding.UTF8.GetBytes(s); body.Write(b, 0, b.Length); };
            foreach (var f in fields) put("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + f.Key + "\"\r\n\r\n" + f.Value + "\r\n");
            put("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"dikte.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
            body.Write(wav, 0, wav.Length);
            put("\r\n--" + boundary + "--\r\n");
            var result = new WebResult();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Timeout = req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "Tepi/" + Island.AppVersion + " (+https://github.com/odlaver/tepi)";
            foreach (var h in headers) req.Headers[h.Key] = h.Value;
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            try
            {
                var bytes = body.ToArray();
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    result.Status = (int)resp.StatusCode;
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) result.Body = sr.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                if (http == null) { result.Body = ex.Message; return result; }
                result.Status = (int)http.StatusCode;
                try { using (var sr = new StreamReader(http.GetResponseStream(), Encoding.UTF8)) result.Body = sr.ReadToEnd(); } catch (Exception) { }
            }
            catch (Exception ex) { result.Body = ex.Message; }
            return result;
        }

        // Offline: whisper.cpp, fetched on request

        public class LocalModel
        {
            public string Id, Name, File, Sha;
            public long Size;
        }

        // Sizes and hashes from huggingface.co/ggerganov/whisper.cpp
        public static readonly LocalModel[] Models =
        {
            new LocalModel { Id = "cepat", Name = "Cepat", File = "ggml-base-q5_1.bin", Size = 59707625, Sha = "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898" },
            new LocalModel { Id = "seimbang", Name = "Seimbang", File = "ggml-small-q5_1.bin", Size = 190085487, Sha = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb" },
            new LocalModel { Id = "akurat", Name = "Akurat", File = "ggml-large-v3-turbo-q5_0.bin", Size = 574041195, Sha = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2" },
        };

        // whisper.cpp release b5454, CPU build
        const string BinUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
        const string BinSha = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb";
        const long BinSize = 8928640;
        const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

        public static string LocalDir { get { return Path.Combine(Settings.Dir, "whisper"); } }
        static string Cli { get { return Path.Combine(LocalDir, "whisper-cli.exe"); } }

        public static LocalModel Model(string id)
        {
            foreach (var m in Models) if (m.Id == id) return m;
            return Models[1];
        }

        public static bool LocalReady(LocalModel m) { return File.Exists(Cli) && File.Exists(Path.Combine(LocalDir, m.File)); }

        // Bytes still to fetch for this model
        public static long Missing(LocalModel m) { return (File.Exists(Cli) ? 0 : BinSize) + (File.Exists(Path.Combine(LocalDir, m.File)) ? 0 : m.Size); }

        public static void RemoveLocal()
        {
            try { if (Directory.Exists(LocalDir)) Directory.Delete(LocalDir, true); }
            catch (Exception ex) { Log.Write("hapus whisper: " + ex.Message); }
        }

        public static long LocalBytes()
        {
            long n = 0;
            try { if (Directory.Exists(LocalDir)) foreach (var f in new DirectoryInfo(LocalDir).GetFiles()) n += f.Length; } catch (Exception) { }
            return n;
        }

        // Worker; progress gets done and total bytes
        public static string Fetch(LocalModel m, Action<long, long> progress, Func<bool> cancelled)
        {
            Directory.CreateDirectory(LocalDir);
            long total = Missing(m), done = 0;
            if (!File.Exists(Cli))
            {
                string zip = Path.Combine(LocalDir, "whisper-bin.zip");
                string err = Download(BinUrl, zip, BinSha, n => progress(done + n, total), cancelled);
                if (err != null) return err;
                done += BinSize;
                try
                {
                    using (var a = ZipFile.OpenRead(zip))
                        foreach (var e in a.Entries)
                        {
                            string name = e.Name.ToLowerInvariant();
                            // Only what whisper-cli loads
                            if (name == "whisper-cli.exe" || name == "whisper.dll" || (name.StartsWith("ggml") && name.EndsWith(".dll")))
                                e.ExtractToFile(Path.Combine(LocalDir, e.Name), true);
                        }
                }
                catch (Exception ex) { Log.Write("whisper zip: " + ex.Message); return Lang.T("Berkas whisper.cpp rusak"); }
                finally { try { File.Delete(zip); } catch (Exception) { } }
            }
            string model = Path.Combine(LocalDir, m.File);
            if (!File.Exists(model))
            {
                string err = Download(ModelUrl + m.File, model, m.Sha, n => progress(done + n, total), cancelled);
                if (err != null) return err;
            }
            return null;
        }

        static string Download(string url, string path, string sha, Action<long> progress, Func<bool> cancelled)
        {
            string part = path + ".part";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 30000;
                req.ReadWriteTimeout = 60000;
                req.UserAgent = "Tepi/" + Island.AppVersion + " (+https://github.com/odlaver/tepi)";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var input = resp.GetResponseStream())
                using (var output = File.Create(part))
                using (var hash = SHA256.Create())
                {
                    var buf = new byte[1 << 16];
                    long got = 0;
                    int n;
                    DateTime next = DateTime.MinValue;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancelled()) { output.Close(); File.Delete(part); return Lang.T("Unduhan dibatalkan"); }
                        output.Write(buf, 0, n);
                        hash.TransformBlock(buf, 0, n, null, 0);
                        got += n;
                        if (DateTime.UtcNow >= next) { progress(got); next = DateTime.UtcNow.AddMilliseconds(250); }
                    }
                    hash.TransformFinalBlock(buf, 0, 0);
                    var sb = new StringBuilder(64);
                    foreach (var b in hash.Hash) sb.Append(b.ToString("x2"));
                    output.Close();
                    // Pinned hash: a swapped file never runs
                    if (sb.ToString() != sha) { File.Delete(part); Log.Write("sha beda: " + url); return Lang.T("Berkas unduhan tidak cocok, tidak dipakai"); }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(part, path);
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("unduh " + url + ": " + ex.Message);
                try { if (File.Exists(part)) File.Delete(part); } catch (Exception) { }
                return Lang.T("Unduhan gagal, cek internet");
            }
        }

        static readonly Regex Stamp = new Regex(@"^\[(\d+):(\d+):(\d+)[.,](\d+) --> (\d+):(\d+):(\d+)[.,](\d+)\]\s*(.*)$");

        public static SpeechResult Local(LocalModel m, byte[] wav, string lang, string prompt)
        {
            var res = new SpeechResult();
            if (!LocalReady(m)) { res.Error = Lang.T("Model offline belum diunduh"); return res; }
            string tmp = Path.Combine(Path.GetTempPath(), "tepi-dikte-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                File.WriteAllBytes(tmp, wav);
                var args = new StringBuilder();
                args.Append("-m \"").Append(Path.Combine(LocalDir, m.File)).Append("\" -f \"").Append(tmp).Append("\"");
                args.Append(" -l ").Append(string.IsNullOrEmpty(lang) ? "auto" : lang);
                args.Append(" -t ").Append(Math.Max(1, Math.Min(8, Environment.ProcessorCount)));
                // Greedy decoding: fast enough on small CPUs
                args.Append(" -bs 1 -bo 1 -np");
                if (!string.IsNullOrEmpty(prompt)) args.Append(" --prompt \"").Append(prompt.Replace("\"", "'")).Append("\"");
                var psi = new ProcessStartInfo(Cli, args.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = LocalDir,
                };
                using (var p = Process.Start(psi))
                {
                    p.BeginErrorReadLine();
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(600000)) { try { p.Kill(); } catch (Exception) { } res.Error = Lang.T("whisper.cpp terlalu lama"); return res; }
                    if (p.ExitCode != 0) { Log.Write("whisper-cli keluar " + p.ExitCode); res.Error = Lang.T("whisper.cpp gagal membaca rekaman"); return res; }
                    var text = new StringBuilder();
                    foreach (var raw in output.Split('\n'))
                    {
                        var match = Stamp.Match(raw.Trim());
                        if (!match.Success) continue;
                        string t = match.Groups[9].Value.Trim();
                        if (t.Length == 0) continue;
                        res.Segments.Add(new SpeechSegment { Start = Secs(match, 1), End = Secs(match, 5), Text = t });
                        if (text.Length > 0) text.Append(' ');
                        text.Append(t);
                    }
                    res.Text = text.ToString();
                }
            }
            catch (Exception ex) { Log.Write("whisper lokal: " + ex.Message); res.Error = Lang.T("whisper.cpp tidak bisa dijalankan"); }
            finally { try { File.Delete(tmp); } catch (Exception) { } }
            return res;
        }

        static double Secs(Match m, int g)
        {
            return int.Parse(m.Groups[g].Value) * 3600 + int.Parse(m.Groups[g + 1].Value) * 60 + int.Parse(m.Groups[g + 2].Value) + int.Parse(m.Groups[g + 3].Value) / 1000.0;
        }
    }
}
