using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Tepi
{
    // Spotify Web API: find a song, play it
    static class SpotifyLink
    {
        const string Accounts = "https://accounts.spotify.com", Api = "https://api.spotify.com/v1";
        const string Scopes = "user-modify-playback-state user-read-playback-state user-read-private";
        static string FilePath { get { return Path.Combine(Settings.Dir, "spotify.vault"); } }

        static readonly object gate = new object();
        static bool loaded;
        static string client, refresh, access, user, product;
        static DateTime expiresUtc;

        public static bool Connected { get { lock (gate) { Load(); return !string.IsNullOrEmpty(refresh); } } }
        public static string User { get { lock (gate) { Load(); return user ?? ""; } } }
        public static bool Premium { get { lock (gate) { Load(); return product == "premium"; } } }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            var d = Json.Parse(Vault.Load(FilePath) ?? "");
            client = Json.Str(d, "client");
            refresh = Json.Str(d, "refresh");
            user = Json.Str(d, "user");
            product = Json.Str(d, "product");
        }

        static void Save()
        {
            try
            {
                Vault.Save(FilePath, Json.Encode(new Dictionary<string, object> { { "client", client }, { "refresh", refresh }, { "user", user ?? "" }, { "product", product ?? "" } }));
            }
            catch (Exception ex) { Log.Write("spotify simpan: " + ex.GetType().Name); }
        }

        public static void Forget()
        {
            lock (gate)
            {
                Vault.Delete(FilePath);
                client = refresh = access = user = product = null;
                loaded = true;
            }
        }

        static string Base64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        static string Random(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Base64Url(b);
        }

        static string Form(Dictionary<string, string> fields)
        {
            var sb = new StringBuilder();
            foreach (var f in fields)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(f.Key)).Append('=').Append(Uri.EscapeDataString(f.Value));
            }
            return sb.ToString();
        }

        // PKCE sign-in via loopback; null on success
        public static string SignIn(string clientId)
        {
            clientId = (clientId ?? "").Trim();
            if (!Regex.IsMatch(clientId, "^[0-9a-fA-F]{32}$")) return Lang.T("Client ID Spotify berisi 32 huruf dan angka");
            string verifier = Random(48), state = Random(16);
            string challenge;
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                string redirect = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/callback";
                string url = Accounts + "/authorize?" + Form(new Dictionary<string, string>
                {
                    { "client_id", clientId }, { "response_type", "code" }, { "redirect_uri", redirect }, { "scope", Scopes },
                    { "code_challenge_method", "S256" }, { "code_challenge", challenge }, { "state", state },
                });
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                var query = WaitForCallback(listener, TimeSpan.FromMinutes(3), "/callback", q => q.ContainsKey("code")
                    ? new[] { Lang.T("Tepi tersambung ke Spotify"), Lang.T("Tab ini boleh ditutup.") }
                    : new[] { Lang.T("Login Spotify dibatalkan"), Lang.T("Tab ini boleh ditutup.") });
                if (query == null) return Lang.T("Login Spotify tidak selesai dalam 3 menit");
                string err;
                if (query.TryGetValue("error", out err)) return err == "access_denied" ? Lang.T("Izin Spotify ditolak") : Lang.F("Spotify menolak: {0}", err);
                string got, code;
                if (!query.TryGetValue("state", out got) || got != state || !query.TryGetValue("code", out code)) return Lang.T("Login Spotify tidak cocok, coba lagi");
                var r = Web.Send("POST", Accounts + "/api/token", new Dictionary<string, string> { { "Content-Type", "application/x-www-form-urlencoded" } },
                    Form(new Dictionary<string, string> { { "grant_type", "authorization_code" }, { "code", code }, { "redirect_uri", redirect }, { "client_id", clientId }, { "code_verifier", verifier } }), 20000);
                if (!r.Ok) { Log.Write("spotify token " + r.Status); return r.Status == 0 ? Lang.F("{0} tidak terjangkau", "Spotify") : Lang.T("Spotify menolak login, cek Client ID dan alamat redirect"); }
                lock (gate)
                {
                    client = clientId;
                    TakeTokens(Json.Parse(r.Body));
                }
                var me = Call("GET", "/me", null);
                lock (gate)
                {
                    user = Json.Str(Json.Parse(me.Body), "display_name");
                    product = Json.Str(Json.Parse(me.Body), "product");
                    Save();
                }
                return null;
            }
            catch (Exception ex) { Log.Write("spotify masuk: " + ex.Message); return Lang.T("Login Spotify gagal"); }
            finally { try { listener.Stop(); } catch (Exception) { } }
        }

        // One redirect to path; answer gives title and text; null on timeout or Stop()
        internal static Dictionary<string, string> WaitForCallback(TcpListener listener, TimeSpan wait, string path, Func<Dictionary<string, string>, string[]> answer)
        {
            var until = DateTime.UtcNow + wait;
            try
            {
                while (DateTime.UtcNow < until)
                {
                    if (!listener.Pending()) { Thread.Sleep(100); continue; }
                    using (var c = listener.AcceptTcpClient())
                    {
                        c.ReceiveTimeout = 5000;
                        var s = c.GetStream();
                        string line;
                        // Dropped connection: wait for the next one
                        try { line = new StreamReader(s, Encoding.ASCII).ReadLine() ?? ""; }
                        catch (IOException) { continue; }
                        var m = Regex.Match(line, "^GET " + Regex.Escape(path) + @"\?(\S*) HTTP");
                        // Favicon and the like: not ours
                        if (!m.Success) { try { Reply(s, 404, "", ""); } catch (IOException) { } continue; }
                        var q = new Dictionary<string, string>();
                        foreach (var pair in m.Groups[1].Value.Split('&'))
                        {
                            int eq = pair.IndexOf('=');
                            if (eq > 0) q[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                        }
                        var say = answer(q);
                        try { Reply(s, 200, say[0], say[1]); } catch (IOException) { }
                        return q;
                    }
                }
            }
            catch (InvalidOperationException) { }
            catch (SocketException) { }
            return null;
        }

        static void Reply(Stream s, int status, string title, string text)
        {
            string html = status != 200 ? "" : "<!doctype html><meta charset=utf-8><title>Tepi</title><body style=\"font:16px system-ui;margin:3em\"><h1 style=\"font-size:20px\">"
                + WebUtility.HtmlEncode(title) + "</h1><p>" + WebUtility.HtmlEncode(text) + "</p>";
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found") + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            s.Write(head, 0, head.Length);
            s.Write(body, 0, body.Length);
        }

        static void TakeTokens(object d)
        {
            access = Json.Str(d, "access_token");
            expiresUtc = DateTime.UtcNow.AddSeconds(Json.Num(d, "expires_in", 3600) - 60);
            // Spotify may rotate the refresh token
            string next = Json.Str(d, "refresh_token");
            if (next.Length > 0) refresh = next;
        }

        // A live access token, refreshed when stale
        static string Token(bool force)
        {
            lock (gate)
            {
                Load();
                if (string.IsNullOrEmpty(refresh)) return null;
                if (!force && !string.IsNullOrEmpty(access) && DateTime.UtcNow < expiresUtc) return access;
                var r = Web.Send("POST", Accounts + "/api/token", new Dictionary<string, string> { { "Content-Type", "application/x-www-form-urlencoded" } },
                    Form(new Dictionary<string, string> { { "grant_type", "refresh_token" }, { "refresh_token", refresh }, { "client_id", client } }), 20000);
                if (!r.Ok) { Log.Write("spotify refresh " + r.Status); return null; }
                TakeTokens(Json.Parse(r.Body));
                Save();
                return access;
            }
        }

        static WebResult Call(string method, string path, string body)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = Token(attempt > 0);
                if (token == null) return new WebResult { Status = 401 };
                var h = new Dictionary<string, string> { { "Authorization", "Bearer " + token } };
                if (body != null) h["Content-Type"] = "application/json";
                var r = Web.Send(method, Api + path, h, body ?? (method == "PUT" ? "" : null), 15000);
                if (r.Status != 401) return r;
            }
            return new WebResult { Status = 401 };
        }

        static string Failure(WebResult r)
        {
            if (r.Status == 0) return Lang.F("{0} tidak terjangkau", "Spotify");
            if (r.Status == 401) return Lang.T("Login Spotify kedaluwarsa, masuk lagi di Pengaturan › Integrasi");
            if (r.Status == 403 && r.Body.Contains("PREMIUM_REQUIRED")) return Lang.T("Memutar lagu pilihan butuh Spotify Premium");
            if (r.Status == 429) return Lang.T("Spotify sedang membatasi, coba lagi sebentar");
            Log.Write("spotify " + r.Status + " " + Fmt.OneLine(r.Body, 200));
            return Lang.F("Spotify menjawab {0}", r.Status);
        }

        // Active device, else this PC's app
        static string Device(out bool none)
        {
            none = false;
            var r = Call("GET", "/me/player/devices", null);
            if (!r.Ok) return null;
            string pick = null, pc = null, any = null;
            foreach (var o in Json.Arr(Json.Parse(r.Body), "devices"))
            {
                string id = Json.Str(o, "id");
                if (id.Length == 0 || Json.Str(o, "is_restricted") == "True") continue;
                if (Json.Str(o, "is_active") == "True") pick = id;
                if (Json.Str(o, "type") == "Computer" && (pc == null || string.Equals(Json.Str(o, "name"), Environment.MachineName, StringComparison.OrdinalIgnoreCase))) pc = id;
                if (any == null) any = id;
            }
            none = pick == null && pc == null && any == null;
            return pick ?? pc ?? any;
        }

        // Null when playing; title is song · artist
        public static string Play(string query, out string title)
        {
            title = "";
            var s = Call("GET", "/search?type=track&limit=1&market=from_token&q=" + Uri.EscapeDataString(query), null);
            if (!s.Ok) return Failure(s);
            var items = Json.Arr(Json.Obj(Json.Parse(s.Body), "tracks"), "items");
            if (items.Length == 0) return Lang.F("Lagu tidak ketemu: {0}", query);
            string uri = Json.Str(items[0], "uri");
            var artists = Json.Arr(items[0], "artists");
            title = Json.Str(items[0], "name") + (artists.Length > 0 ? " · " + Json.Str(artists[0], "name") : "");
            bool none;
            string device = Device(out none);
            // App closed: open it, then wait
            if (device == null && none)
            {
                string target, name;
                if (Island.ResolveApp("spotify", out target, out name)) ShellApp.Launch(target, false);
                for (int i = 0; i < 12 && device == null; i++)
                {
                    Thread.Sleep(1000);
                    device = Device(out none);
                }
            }
            if (device == null) return Lang.T("Buka aplikasi Spotify dulu");
            var p = Call("PUT", "/me/player/play?device_id=" + Uri.EscapeDataString(device), Json.Encode(new Dictionary<string, object> { { "uris", new[] { uri } } }));
            return p.Ok ? null : Failure(p);
        }
    }

    // No Premium: top result on YouTube Music
    static class YouTubeLink
    {
        static readonly Regex Video = new Regex(@"""videoRenderer"":\{""videoId"":""(?<id>[\w-]{11})""", RegexOptions.Compiled);
        static readonly Regex Title = new Regex(@"""title"":\{""runs"":\[\{""text"":""(?<t>(?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

        // First video; ads and Shorts differ
        internal static string FirstVideo(string html, out string title)
        {
            title = "";
            var v = Video.Match(html ?? "");
            if (!v.Success) return null;
            var t = Title.Match(html, v.Index, Math.Min(4000, html.Length - v.Index));
            if (t.Success)
            {
                try { title = Json.Parse("\"" + t.Groups["t"].Value + "\"") as string ?? ""; }
                catch (Exception) { title = ""; }
            }
            return v.Groups["id"].Value;
        }

        // Null when playing; title is the video's
        public static string Play(string query, out string title)
        {
            title = "";
            string search = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
            var r = Web.Send("GET", search, new Dictionary<string, string> { { "Accept-Language", "id,en;q=0.8" } }, null, 15000);
            string id = r.Ok ? FirstVideo(r.Body, out title) : null;
            if (r.Status == 0) return Lang.F("{0} tidak terjangkau", "YouTube");
            // Page changed: show the results instead
            if (id == null) Log.Write("youtube: hasil tidak terbaca " + r.Status);
            if (id == null)
            {
                try { Process.Start(new ProcessStartInfo("https://music.youtube.com/search?q=" + Uri.EscapeDataString(query)) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("youtube buka: " + ex.Message); return Lang.T("Browser tidak bisa dibuka"); }
                return Lang.F("Hasil YouTube Music untuk {0} dibuka, pilih sendiri", query);
            }
            string video = title;
            if (title.Length == 0) title = query;
            try { Island.OpenSong("https://music.youtube.com/watch?v=" + id, video); }
            catch (Exception ex) { Log.Write("youtube buka: " + ex.Message); return Lang.T("Browser tidak bisa dibuka"); }
            return null;
        }
    }

    // "putar Evaluasi dari Hindia"
    partial class Island
    {
        static readonly Regex SongAsk = new Regex(@"^(?:putar|puter|putarin|puterin|play|mainkan|setel|setelin)\s+(?:(?:lagu|lagunya|musik|song|the song)\s+)?(?<q>.+?)(?:\s+(?:dari|oleh|by|punya)\s+(?<by>.+))?$", RegexOptions.IgnoreCase);
        // Player words, not titles
        static readonly Regex NotATitle = new Regex(@"^(?:lagi|lagu|musik|music|song|berikutnya|selanjutnya|sebelumnya|tadi|next|previous|it|the music|spotify|di spotify|youtube|di youtube|youtube music|di youtube music)$|^(?:volume|vol|suara|timer|alarm|pengingat|kecerahan|brightness|wi-?fi|bluetooth|mode)\b", RegexOptions.IgnoreCase);
        static readonly Regex Where = new Regex(@"\s+(?:di|on|pakai|lewat|in)\s+(?<app>spotify|youtube music|yt music|ytmusic|youtube|yt)$", RegexOptions.IgnoreCase);

        internal static string SongQuery(string raw) { string app; return SongQuery(raw, out app); }

        // app: "spotify", "youtube", or "" when unsaid
        internal static string SongQuery(string raw, out string app)
        {
            app = "";
            var m = SongAsk.Match(Perintah.Plain(raw, true));
            if (!m.Success) return null;
            string q = m.Groups["q"].Value, by = m.Groups["by"].Success ? m.Groups["by"].Value : "";
            var w = Where.Match(by.Length > 0 ? by : q);
            if (w.Success)
            {
                app = w.Groups["app"].Value.ToLowerInvariant() == "spotify" ? "spotify" : "youtube";
                if (by.Length > 0) by = by.Substring(0, w.Index); else q = q.Substring(0, w.Index);
            }
            q = q.Trim();
            if (q.Length < 2 || NotATitle.IsMatch(q)) return null;
            return by.Trim().Length > 0 ? q + " " + by.Trim() : q;
        }

        // Spotify Premium, else YouTube; null if playing
        string PlaySong(string query, string app, out string said)
        {
            string title, err;
            bool spotify = SpotifyLink.Connected && SpotifyLink.Premium;
            if (app == "spotify" && !spotify)
            {
                // Free Spotify: open its search only
                said = Lang.F("Hasil Spotify untuk {0} dibuka", query);
                try { Process.Start(new ProcessStartInfo("spotify:search:" + query) { UseShellExecute = true }); return null; }
                catch (Exception) { return Lang.T("Aplikasi Spotify tidak terpasang"); }
            }
            if (spotify && app != "youtube")
            {
                err = SpotifyLink.Play(query, out title);
                said = Lang.F("Memutar {0}", title);
                return err;
            }
            err = YouTubeLink.Play(query, out title);
            said = Lang.F("Memutar {0} di YouTube Music", title);
            return err;
        }

        // Network call, so off the UI thread
        bool StartSong(ChatMsg me)
        {
            string app, q = SongQuery(me.Text, out app);
            if (q == null) return false;
            suruhBusy = true;
            dictState = DictState.Working;
            Kick();
            ChatChanged(null);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string said, err = PlaySong(q, app, out said);
                Post(delegate
                {
                    suruhBusy = false;
                    dictState = DictState.Off;
                    Kick();
                    SuruhDone(me, err != null ? Said(err) : Did(IcoPlaySong, said));
                });
            });
            return true;
        }

        // AI path: reply now, report later
        ChatAct PlaySongLater(string query)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string said, err = PlaySong(query, "", out said);
                Post(delegate { HearShow(err == null ? Done(IcoPlaySong, said) : Failed(err)); });
            });
            return new ChatAct { Glyph = IcoPlaySong, Text = Lang.F("Mencari {0}", query) };
        }
    }
}
