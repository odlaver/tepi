using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Tepi
{
    // Hook bridge for command-based agents:
    // Tepi.exe --relay <agent> [--reply] [--json] [--event <name>] < event.json
    static class Relay
    {
        public static int Run(string[] args)
        {
            string agent = args.Length > 1 ? args[1] : "agent";
            bool reply = Array.IndexOf(args, "--reply") > 0, json = Array.IndexOf(args, "--json") > 0;
            int ei = Array.IndexOf(args, "--event");
            string ev = ei > 0 && ei + 1 < args.Length ? args[ei + 1] : null;

            byte[] body = null;
            if (Console.IsInputRedirected)
            {
                try
                {
                    var ms = new MemoryStream();
                    Console.OpenStandardInput().CopyTo(ms);
                    body = ms.ToArray();
                }
                catch (Exception) { }
            }

            string answer = "";
            if (body != null && body.Length > 0 && Running())
            {
                if (ev != null) body = WithEvent(body, ev);
                answer = Post(agent, body, reply ? 125000 : 2000);
            }
            if (!reply) answer = "";
            if (json && answer.Trim().Length == 0) answer = "{}";
            if (answer.Length > 0)
            {
                try
                {
                    var o = Encoding.UTF8.GetBytes(answer);
                    using (var so = Console.OpenStandardOutput()) so.Write(o, 0, o.Length);
                }
                catch (Exception) { }
            }
            return 0; // never block the agent
        }

        // Refused localhost connects stall ~1 s
        static bool Running()
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(@"Local\TepiIsland", out m)) return false;
            m.Dispose();
            return true;
        }

        // Event name for agents that omit it
        static byte[] WithEvent(byte[] body, string ev)
        {
            string s = Encoding.UTF8.GetString(body);
            int open = s.IndexOf('{');
            if (open < 0) return body;
            int next = open + 1;
            while (next < s.Length && char.IsWhiteSpace(s[next])) next++;
            string field = "\"hook_event_name\":\"" + ev.Replace("\\", "").Replace("\"", "") + "\"" + (next < s.Length && s[next] == '}' ? "" : ",");
            return Encoding.UTF8.GetBytes(s.Substring(0, open + 1) + field + s.Substring(open + 1));
        }

        // Raw socket skips proxy lookup
        static string Post(string agent, byte[] body, int timeoutMs)
        {
            var s = new Settings();
            string token = s.Get("agent.token", "");
            if (token.Length == 0) return "";
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(IPAddress.Loopback, s.GetInt("agent.port", 47821), null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1000)) return "";
                    c.EndConnect(ar);
                    c.SendTimeout = c.ReceiveTimeout = timeoutMs;
                    var st = c.GetStream();
                    var head = Encoding.ASCII.GetBytes("POST /hook/" + Uri.EscapeDataString(agent) + " HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Tepi-Token: " + token +
                                                      "\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    st.Write(head, 0, head.Length);
                    st.Write(body, 0, body.Length);
                    var ms = new MemoryStream();
                    var buf = new byte[8192];
                    int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    string all = Encoding.UTF8.GetString(ms.ToArray());
                    int at = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    return all.StartsWith("HTTP/1.1 200") && at > 0 ? all.Substring(at + 4) : "";
                }
            }
            catch (Exception) { return ""; } // island not answering: stay silent
        }
    }
}
