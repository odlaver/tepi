using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Tepi
{
    // Phone link: opt-in LAN server, paired by a typed code
    class PhoneLink
    {
        static readonly string KeyFile = Path.Combine(Settings.Dir, "phone.key");
        const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        readonly object sync = new object();
        TcpListener listener;
        byte[] key, codeKey;
        string code;
        DateTime codeUntilUtc;
        int misses, version;
        string state = "{}";
        readonly Dictionary<string, DateTime> nonces = new Dictionary<string, DateTime>();
        readonly Semaphore slots = new Semaphore(8, 8);

        public int Port;
        public string Device = "", Error, Inbox;
        public DateTime SeenUtc;
        public event Action<int, string> Decided;
        public event Action Paired;
        public event Action<Dictionary<string, object>> Pushed;
        // Name, bytes so far, total, incoming
        public event Action<string, long, long, bool> Moved;
        public event Action<string, string> Received;
        public event Action<string> Sent;
        public event Action<string> Dropped;

        public const long FileMax = 500L * 1024 * 1024;
        const int Chunk = 384 * 1024;

        class Offer { public int Id; public string Path, Name; public long Size; }
        class Incoming { public FileStream Fs; public string Part, Name; public long Size, Got; public DateTime TouchedUtc; }
        readonly List<Offer> offers = new List<Offer>();
        readonly Dictionary<string, Incoming> incoming = new Dictionary<string, Incoming>();
        int offerIds;

        public PhoneLink()
        {
            try
            {
                var saved = Json.Parse(Vault.Load(KeyFile) ?? "");
                if (saved != null)
                {
                    key = Convert.FromBase64String(Json.Str(saved, "key"));
                    Device = Json.Str(saved, "device");
                }
            }
            catch (Exception ex) { Log.Write("phone key: " + ex.Message); key = null; }
        }

        public bool IsPaired { get { return key != null; } }

        public string Code
        {
            get { lock (sync) return code != null && DateTime.UtcNow < codeUntilUtc ? code : null; }
        }

        public void Start(int port)
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            catch (Exception ex)
            {
                Error = Lang.T("Port HP tidak bisa dibuka");
                Log.Write("phone listen: " + ex.Message);
                return;
            }
            new Thread(Accept) { IsBackground = true, Name = "phone" }.Start();
        }

        public void Stop()
        {
            try { if (listener != null) listener.Stop(); }
            catch (Exception ex) { Log.Write("phone stop: " + ex.Message); }
            lock (sync) Monitor.PulseAll(sync);
        }

        // 12 characters, about 59 bits; valid five minutes
        public string NewCode()
        {
            var sb = new StringBuilder();
            var b = new byte[1];
            using (var rng = RandomNumberGenerator.Create())
                while (sb.Length < 12)
                {
                    rng.GetBytes(b);
                    if (b[0] < 248) sb.Append(Alphabet[b[0] % Alphabet.Length]);
                }
            string c = sb.ToString();
            var k = Derive(c);
            lock (sync)
            {
                code = c.Substring(0, 4) + "-" + c.Substring(4, 4) + "-" + c.Substring(8, 4);
                codeKey = k;
                codeUntilUtc = DateTime.UtcNow.AddMinutes(5);
                misses = 0;
            }
            return code;
        }

        public void Forget()
        {
            lock (sync) { key = null; Device = ""; offers.Clear(); }
            Vault.Delete(KeyFile);
            DropIncoming(TimeSpan.Zero);
        }

        public void Sweep() { DropIncoming(TimeSpan.FromMinutes(2)); }

        public bool Online { get { return key != null && (DateTime.UtcNow - SeenUtc).TotalSeconds < 45; } }

        // Laptop to phone: phone pulls it
        public bool OfferFile(string path)
        {
            long size;
            try { size = new FileInfo(path).Length; } catch (Exception) { return false; }
            if (size > FileMax || key == null) return false;
            lock (sync)
            {
                offers.RemoveAll(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase));
                offers.Add(new Offer { Id = ++offerIds, Path = path, Name = Path.GetFileName(path), Size = size });
                version++;
                Monitor.PulseAll(sync);
            }
            return true;
        }

        string OffersJson()
        {
            var list = new List<object>();
            foreach (var o in offers) list.Add(new Dictionary<string, object> { { "id", o.Id }, { "name", o.Name }, { "size", o.Size } });
            return Json.Encode(list);
        }

        // Half-sent files after a quiet spell
        void DropIncoming(TimeSpan idle)
        {
            var dead = new List<Incoming>();
            lock (incoming)
            {
                var keys = new List<string>();
                foreach (var kv in incoming) if (DateTime.UtcNow - kv.Value.TouchedUtc >= idle) keys.Add(kv.Key);
                foreach (var k in keys) { dead.Add(incoming[k]); incoming.Remove(k); }
            }
            foreach (var d in dead)
            {
                try { d.Fs.Dispose(); File.Delete(d.Part); } catch (Exception ex) { Log.Write("phone part: " + ex.Message); }
                var ev = Dropped;
                if (ev != null && idle > TimeSpan.Zero) ev(d.Name);
            }
        }

        // Phone file name, made safe
        static string CleanName(string raw)
        {
            string n = raw ?? "";
            n = n.Substring(Math.Max(n.LastIndexOf('/'), n.LastIndexOf('\\')) + 1);
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim().TrimEnd('.');
            if (n.Length == 0) n = "berkas";
            return n.Length > 120 ? n.Substring(n.Length - 120) : n;
        }

        static string Unique(string dir, string name)
        {
            string path = Path.Combine(dir, name), stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; File.Exists(path) || Directory.Exists(path); i++) path = Path.Combine(dir, stem + " (" + i + ")" + ext);
            return path;
        }

        // One chunk in; done when all bytes arrived
        string Put(Dictionary<string, object> req)
        {
            string id = Json.Str(req, "id");
            long size = (long)Json.Num(req, "size", -1), offset = (long)Json.Num(req, "offset", -1);
            byte[] data;
            try { data = Convert.FromBase64String(Json.Str(req, "data")); } catch (FormatException) { return "{\"ok\":false}"; }
            if (id.Length == 0 || id.Length > 64 || size < 0 || size > FileMax || offset < 0 || data.Length > Chunk || Inbox == null) return "{\"ok\":false}";
            DropIncoming(TimeSpan.FromMinutes(2));
            Incoming inc;
            lock (incoming)
            {
                if (!incoming.TryGetValue(id, out inc))
                {
                    if (offset != 0 || incoming.Count >= 4) return "{\"ok\":false}";
                    Directory.CreateDirectory(Inbox);
                    string part = Path.Combine(Inbox, "." + Guid.NewGuid().ToString("N") + ".part");
                    inc = new Incoming { Fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write), Part = part, Name = CleanName(Json.Str(req, "name")), Size = size, TouchedUtc = DateTime.UtcNow };
                    File.SetAttributes(part, FileAttributes.Hidden);
                    incoming[id] = inc;
                }
                // Resend of a chunk we have
                if (offset < inc.Got) return "{\"ok\":true,\"got\":" + inc.Got + "}";
                if (offset != inc.Got || inc.Got + data.Length > inc.Size) return "{\"ok\":false,\"got\":" + inc.Got + "}";
                inc.TouchedUtc = DateTime.UtcNow;
                inc.Fs.Write(data, 0, data.Length);
                inc.Got += data.Length;
                if (inc.Got >= inc.Size) incoming.Remove(id);
            }
            var mv = Moved;
            if (mv != null) mv(inc.Name, inc.Got, inc.Size, true);
            if (inc.Got < inc.Size) return "{\"ok\":true,\"got\":" + inc.Got + "}";
            inc.Fs.Dispose();
            string final = Unique(Inbox, inc.Name);
            File.Move(inc.Part, final);
            File.SetAttributes(final, FileAttributes.Normal);
            MarkDownloaded(final);
            var r = Received;
            if (r != null) r(final, inc.Name);
            return "{\"ok\":true,\"got\":" + inc.Got + ",\"done\":true}";
        }

        // Phone pulls an offered file
        string Get(Dictionary<string, object> req)
        {
            int id = (int)Json.Num(req, "id", 0);
            long offset = (long)Json.Num(req, "offset", -1);
            Offer o;
            lock (sync) o = offers.Find(x => x.Id == id);
            if (o == null || offset < 0 || offset > o.Size) return "{\"ok\":false}";
            byte[] buf;
            using (var fs = new FileStream(o.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Position = offset;
                buf = new byte[(int)Math.Min(Chunk, o.Size - offset)];
                int n = 0;
                while (n < buf.Length) { int k = fs.Read(buf, n, buf.Length - n); if (k <= 0) break; n += k; }
                if (n < buf.Length) return "{\"ok\":false}";
            }
            bool done = offset + buf.Length >= o.Size;
            var mv = Moved;
            if (mv != null) mv(o.Name, offset + buf.Length, o.Size, false);
            if (done)
            {
                lock (sync) { offers.Remove(o); version++; Monitor.PulseAll(sync); }
                var s = Sent;
                if (s != null) s(o.Name);
            }
            return Json.Encode(new Dictionary<string, object> { { "ok", true }, { "name", o.Name }, { "size", o.Size }, { "data", Convert.ToBase64String(buf) }, { "done", done } });
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint mode, uint flags, IntPtr template);

        // Mark of the web: SmartScreen still checks
        static void MarkDownloaded(string path)
        {
            using (var h = CreateFile(path + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return;
                using (var fs = new FileStream(h, FileAccess.Write))
                {
                    var b = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n");
                    fs.Write(b, 0, b.Length);
                }
            }
        }

        // UI thread hands over the latest view
        public void Publish(string json)
        {
            lock (sync)
            {
                version++;
                state = json;
                Monitor.PulseAll(sync);
            }
        }

        // First LAN IPv4 with a gateway
        public static string Address()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var props = ni.GetIPProperties();
                    if (props.GatewayAddresses.Count == 0) continue;
                    foreach (var ua in props.UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) return ua.Address.ToString();
                }
            }
            catch (Exception ex) { Log.Write("phone address: " + ex.Message); }
            return "";
        }

        static byte[] Derive(string c)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(c.Replace("-", "")), Encoding.UTF8.GetBytes("tepi.phone.v1"), 60000, HashAlgorithmName.SHA256))
                return kdf.GetBytes(32);
        }

        static byte[] Sub(byte[] k, string label)
        {
            using (var h = new HMACSHA256(k)) return h.ComputeHash(Encoding.UTF8.GetBytes(label));
        }

        static string Mac(byte[] k, string text)
        {
            using (var h = new HMACSHA256(Sub(k, "mac"))) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        static bool Same(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        // AES-256-CBC, IV in front, base64
        static string Seal(byte[] k, string plain)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Sub(k, "enc");
                aes.GenerateIV();
                using (var e = aes.CreateEncryptor())
                {
                    var data = Encoding.UTF8.GetBytes(plain);
                    var ct = e.TransformFinalBlock(data, 0, data.Length);
                    var all = new byte[16 + ct.Length];
                    Array.Copy(aes.IV, all, 16);
                    Array.Copy(ct, 0, all, 16, ct.Length);
                    return Convert.ToBase64String(all);
                }
            }
        }

        static string Open(byte[] k, string sealedText)
        {
            var all = Convert.FromBase64String(sealedText);
            if (all.Length < 32) throw new CryptographicException("short");
            using (var aes = Aes.Create())
            {
                aes.Key = Sub(k, "enc");
                var iv = new byte[16];
                Array.Copy(all, iv, 16);
                aes.IV = iv;
                using (var d = aes.CreateDecryptor())
                    return Encoding.UTF8.GetString(d.TransformFinalBlock(all, 16, all.Length - 16));
            }
        }

        void Accept()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex) { Log.Write("phone accept: " + ex.Message); Thread.Sleep(500); continue; }
                if (!slots.WaitOne(0)) { c.Close(); continue; }
                new Thread(delegate () { try { Serve(c); } finally { slots.Release(); } }) { IsBackground = true, Name = "phone req" }.Start();
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    var stream = client.GetStream();
                    int started = Environment.TickCount;
                    string head;
                    byte[] early, body;
                    if (!AgentHub.ReadHead(stream, started, out head, out early)) return;
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var parts = lines[0].Split(' ');
                    var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon > 0) h[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                    }
                    string path = parts.Length >= 2 ? parts[1] : "";
                    // File chunks are bigger; and slower
                    int cap = path == "/phone/put" ? 1024 * 1024 : path == "/phone/push" ? 256 * 1024 : 64 * 1024;
                    if (path == "/phone/put") client.ReceiveTimeout = 20000;
                    if (parts[0] != "POST" || h.ContainsKey("Origin") || !AgentHub.ReadBody(stream, head, early, path == "/phone/put" ? Environment.TickCount : started, cap, out body)) { AgentHub.Reply(stream, 400, ""); return; }
                    string time, nonce, mac;
                    h.TryGetValue("X-Tepi-Time", out time);
                    h.TryGetValue("X-Tepi-Nonce", out nonce);
                    h.TryGetValue("X-Tepi-Mac", out mac);
                    long ms;
                    if (!long.TryParse(time, out ms) || nonce == null || nonce.Length != 32 || mac == null) { AgentHub.Reply(stream, 403, ""); return; }
                    long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                    if (Math.Abs(now - ms) > 60000) { AgentHub.Reply(stream, 403, ""); return; }
                    string text = Encoding.UTF8.GetString(body);
                    string signed = "POST\n" + path + "\n" + time + "\n" + nonce + "\n" + text;

                    byte[] k;
                    bool pairing = path == "/phone/pair";
                    lock (sync)
                    {
                        if (pairing) k = code != null && DateTime.UtcNow < codeUntilUtc ? codeKey : null;
                        else k = key;
                        if (k == null || !Same(mac, Mac(k, signed)))
                        {
                            // Wrong code: five tries, then a new code is needed
                            if (pairing && k != null && ++misses >= 5) { code = null; codeKey = null; }
                            k = null;
                        }
                        else
                        {
                            var cut = DateTime.UtcNow.AddMinutes(-2);
                            var old = new List<string>();
                            foreach (var kv in nonces) if (kv.Value < cut) old.Add(kv.Key);
                            foreach (var o in old) nonces.Remove(o);
                            if (nonces.ContainsKey(nonce)) k = null; else nonces[nonce] = DateTime.UtcNow;
                        }
                    }
                    if (k == null) { AgentHub.Reply(stream, 403, ""); return; }
                    var req = Json.Parse(Open(k, text)) as Dictionary<string, object>;
                    if (req == null) { AgentHub.Reply(stream, 400, ""); return; }
                    SeenUtc = DateTime.UtcNow;

                    string reply;
                    if (pairing)
                    {
                        lock (sync)
                        {
                            key = k;
                            Device = Fmt.OneLine(Json.Str(req, "device"), 40);
                            code = null;
                            codeKey = null;
                        }
                        Vault.Save(KeyFile, Json.Encode(new Dictionary<string, object> { { "key", Convert.ToBase64String(k) }, { "device", Device } }));
                        var p = Paired;
                        if (p != null) p();
                        reply = Json.Encode(new Dictionary<string, object> { { "ok", true }, { "pc", Environment.MachineName } });
                    }
                    else if (path == "/phone/events")
                    {
                        int since = (int)Json.Num(req, "since", -1);
                        lock (sync)
                        {
                            // Long poll: until something moves
                            var until = DateTime.UtcNow.AddSeconds(25);
                            while (version == since && DateTime.UtcNow < until && listener != null)
                                Monitor.Wait(sync, 1000);
                            reply = "{\"v\":" + version + ",\"s\":" + state + ",\"o\":" + OffersJson() + "}";
                        }
                    }
                    else if (path == "/phone/push")
                    {
                        var ev = Pushed;
                        if (ev != null) ev(req);
                        reply = "{\"ok\":true}";
                    }
                    else if (path == "/phone/put") reply = Put(req);
                    else if (path == "/phone/get") reply = Get(req);
                    else if (path == "/phone/decide")
                    {
                        string d = Json.Str(req, "decision");
                        var ev = Decided;
                        if ((d == "allow" || d == "deny") && ev != null) ev((int)Json.Num(req, "id", 0), d);
                        reply = "{\"ok\":true}";
                    }
                    else { AgentHub.Reply(stream, 404, ""); return; }

                    string out64 = Seal(k, reply);
                    string head2 = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nX-Tepi-Mac: " + Mac(k, "reply\n" + nonce + "\n" + out64) +
                        "\r\nContent-Length: " + Encoding.ASCII.GetByteCount(out64) + "\r\nConnection: close\r\n\r\n";
                    var hb = Encoding.ASCII.GetBytes(head2 + out64);
                    stream.Write(hb, 0, hb.Length);
                    stream.Flush();
                }
                catch (Exception ex) { Log.Write("phone serve: " + ex.Message); }
            }
        }
    }
}
