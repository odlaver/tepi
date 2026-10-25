using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Tepi
{
    enum AgentState { Ready, Working, Waiting, Done, Ended }

    class AgentSession
    {
        public string Id = "", Project = "", Cwd = "", Profile = "";
        public AgentState State;
        public string Tool = "", Activity = "", LastMessage = "";
        public bool Failed;
        public DateTime StartedUtc, LastUtc, TurnStartedUtc;
    }

    // Permission prompt held until answered
    class Approval
    {
        public string SessionId = "", Project = "", Profile = "", Tool = "", Detail = "", Note = "", Key = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
        public readonly ManualResetEvent Answered = new ManualResetEvent(false);
        public string Decision; // allow, deny, or null for the terminal prompt
        public volatile bool Closed;

        public void Answer(string decision)
        {
            if (Closed) return;
            Decision = decision;
            Closed = true;
            Answered.Set();
        }
    }

    class HookEvent
    {
        public string Name = "", SessionId = "";
        public Dictionary<string, object> Data;
        public Approval Approval;
    }

    // Hook server; approvals wait for clicks
    class AgentHub
    {
        public event Action<HookEvent> Received;
        public event Action<Approval> Expired;

        readonly int port;
        readonly string token;
        readonly int waitSeconds;
        TcpListener listener;
        public string Error;

        public AgentHub(int port, string token, int waitSeconds)
        {
            this.port = port;
            this.token = token;
            this.waitSeconds = waitSeconds;
        }

        public int Port { get { return port; } }

        public void Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
            }
            catch (Exception ex)
            {
                Error = "Port " + port + " sudah dipakai aplikasi lain";
                Log.Write("hook server: " + ex.Message);
                return;
            }
            var t = new Thread(Accept) { IsBackground = true, Name = "hooks" };
            t.Start();
        }

        void Accept()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch (Exception ex) { Log.Write("hook accept: " + ex.Message); return; }
                ThreadPool.QueueUserWorkItem(delegate { Serve(c); });
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
                    string head;
                    byte[] body;
                    if (!ReadRequest(stream, out head, out body)) { Reply(stream, 400, ""); return; }
                    var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var parts = lines[0].Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                    }
                    string got;
                    // Browsers send Origin; hooks never do
                    if (parts.Length < 2 || parts[0] != "POST" || parts[1] != "/hook" || headers.ContainsKey("Origin") ||
                        !headers.TryGetValue("X-Tepi-Token", out got) || got != token)
                    {
                        Reply(stream, 403, "");
                        return;
                    }
                    Dictionary<string, object> data;
                    try { data = Json.Parse(Encoding.UTF8.GetString(body)) as Dictionary<string, object>; }
                    catch (ArgumentException) { data = null; }
                    if (data == null) { Reply(stream, 400, ""); return; }
                    var ev = new HookEvent { Name = Json.Str(data, "hook_event_name"), SessionId = Json.Str(data, "session_id"), Data = data };

                    if (ev.Name != "PermissionRequest")
                    {
                        Reply(stream, 200, "");
                        Raise(ev);
                        return;
                    }

                    ev.Approval = new Approval { SessionId = ev.SessionId };
                    Raise(ev);
                    var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
                    while (!ev.Approval.Answered.WaitOne(250))
                    {
                        if (DateTime.UtcNow > deadline || Gone(client)) { ev.Approval.Answer(null); Expire(ev.Approval); break; }
                    }
                    Reply(stream, 200, Decision(ev.Approval));
                }
                catch (Exception ex) { Log.Write("hook serve: " + ex.Message); }
            }
        }

        static string Decision(Approval a)
        {
            if (a.Decision == "allow")
                return "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}";
            if (a.Decision == "deny")
                return "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"Ditolak dari Tepi.\"}}}";
            return "";
        }

        // Claude Code dropped the hook request
        static bool Gone(TcpClient c)
        {
            try { return c.Client.Poll(0, SelectMode.SelectRead) && c.Client.Available == 0; }
            catch (Exception) { return true; }
        }

        void Raise(HookEvent ev)
        {
            var h = Received;
            if (h != null) h(ev);
        }

        void Expire(Approval a)
        {
            var h = Expired;
            if (h != null) h(a);
        }

        static bool ReadRequest(NetworkStream s, out string head, out byte[] body)
        {
            head = null;
            body = null;
            var buf = new MemoryStream();
            var one = new byte[4096];
            int headEnd = -1;
            while (headEnd < 0)
            {
                int n = s.Read(one, 0, one.Length);
                if (n <= 0) return false;
                buf.Write(one, 0, n);
                if (buf.Length > 64 * 1024) return false;
                headEnd = IndexOf(buf.GetBuffer(), (int)buf.Length);
            }
            var all = buf.ToArray();
            head = Encoding.ASCII.GetString(all, 0, headEnd);
            int length = 0;
            bool expect = false;
            foreach (var l in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l.Substring(15).Trim(), out length);
                if (l.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase) && l.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) > 0) expect = true;
            }
            if (length < 0 || length > 8 * 1024 * 1024) return false;
            var rest = new MemoryStream();
            rest.Write(all, headEnd + 4, all.Length - headEnd - 4);
            if (expect && rest.Length < length)
            {
                var go = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                s.Write(go, 0, go.Length);
            }
            while (rest.Length < length)
            {
                int n = s.Read(one, 0, one.Length);
                if (n <= 0) return false;
                rest.Write(one, 0, n);
            }
            body = rest.ToArray();
            return true;
        }

        static int IndexOf(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        static void Reply(NetworkStream s, int status, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            string reason = status == 200 ? "OK" : status == 403 ? "Forbidden" : "Bad Request";
            var head = "HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            s.Write(h, 0, h.Length);
            s.Write(payload, 0, payload.Length);
            s.Flush();
        }

        // Short line describing a tool call
        public static string Describe(string tool, Dictionary<string, object> input)
        {
            if (input == null) return tool;
            switch (tool)
            {
                case "Bash":
                case "PowerShell": return Fmt.OneLine(Json.Str(input, "command"), 400);
                case "Read":
                case "Write":
                case "Edit":
                case "NotebookEdit": return Path.GetFileName(Json.Str(input, "file_path").TrimEnd('\\', '/'));
                case "Grep": return Json.Str(input, "pattern");
                case "Glob": return Json.Str(input, "pattern");
                case "WebFetch":
                    Uri u;
                    return Uri.TryCreate(Json.Str(input, "url"), UriKind.Absolute, out u) ? u.Host + u.AbsolutePath : Json.Str(input, "url");
                case "WebSearch": return Json.Str(input, "query");
                case "Agent":
                case "Task": return Fmt.OneLine(Json.Str(input, "description"), 120);
                case "Skill": return Json.Str(input, "skill");
            }
            if (tool.StartsWith("mcp__"))
            {
                var bits = tool.Split(new[] { "__" }, StringSplitOptions.None);
                return bits.Length >= 3 ? bits[1] + " · " + bits[2] : tool;
            }
            return "";
        }

        public static string ToolLabel(string tool)
        {
            switch (tool)
            {
                case "Bash": return "Bash";
                case "PowerShell": return "PowerShell";
                case "Read": return "Baca";
                case "Write": return "Tulis";
                case "Edit": return "Edit";
                case "Grep": return "Cari";
                case "Glob": return "Cari berkas";
                case "WebFetch": return "Buka web";
                case "WebSearch": return "Cari web";
                case "Agent":
                case "Task": return "Subagent";
                case "TodoWrite": return "Todo";
            }
            return tool.StartsWith("mcp__") ? "MCP" : tool;
        }

        public static string ProfileOf(string transcriptPath)
        {
            string p = (transcriptPath ?? "").Replace('/', '\\').ToLowerInvariant();
            int i = p.IndexOf("\\.claude");
            if (i < 0) return "";
            int end = p.IndexOf('\\', i + 1);
            string dir = end > 0 ? p.Substring(i + 1, end - i - 1) : p.Substring(i + 1);
            if (dir == ".claude") return "";
            return dir.StartsWith(".claude-") ? dir.Substring(8) : dir;
        }
    }
}
