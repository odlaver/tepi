using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Tepi
{
    // One AI service and its endpoint
    class AiProvider
    {
        public string Id, Name, Base, KeyHint, KeyUrl, SttModel;
        public string[] Models;
        // Anthropic Messages instead of chat/completions
        public bool Anthropic, NoKey, Stt;
    }

    // Text and speech through any provider
    static class Ai
    {
        public static readonly AiProvider[] Providers =
        {
            new AiProvider { Id = "groq", Name = "Groq", Base = "https://api.groq.com/openai/v1/", KeyHint = "gsk_…", KeyUrl = "https://console.groq.com/keys", Stt = true, SttModel = "whisper-large-v3-turbo",
                             Models = new[] { "openai/gpt-oss-120b", "llama-3.3-70b-versatile", "openai/gpt-oss-20b", "llama-3.1-8b-instant" } },
            new AiProvider { Id = "openai", Name = "OpenAI", Base = "https://api.openai.com/v1/", KeyHint = "sk-…", KeyUrl = "https://platform.openai.com/api-keys", Stt = true, SttModel = "whisper-1",
                             Models = new[] { "gpt-5-mini", "gpt-4.1-mini", "gpt-4o-mini" } },
            new AiProvider { Id = "anthropic", Name = "Claude (Anthropic)", Base = "https://api.anthropic.com/v1/", KeyHint = "sk-ant-…", KeyUrl = "https://console.anthropic.com/settings/keys", Anthropic = true,
                             Models = new[] { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" } },
            new AiProvider { Id = "gemini", Name = "Gemini", Base = "https://generativelanguage.googleapis.com/v1beta/openai/", KeyHint = "AIza…", KeyUrl = "https://aistudio.google.com/apikey",
                             Models = new[] { "gemini-2.5-flash", "gemini-2.0-flash" } },
            new AiProvider { Id = "openrouter", Name = "OpenRouter", Base = "https://openrouter.ai/api/v1/", KeyHint = "sk-or-…", KeyUrl = "https://openrouter.ai/keys",
                             Models = new[] { "openrouter/auto" } },
            new AiProvider { Id = "deepseek", Name = "DeepSeek", Base = "https://api.deepseek.com/", KeyHint = "sk-…", KeyUrl = "https://platform.deepseek.com/api_keys",
                             Models = new[] { "deepseek-chat" } },
            new AiProvider { Id = "mistral", Name = "Mistral", Base = "https://api.mistral.ai/v1/", KeyHint = "…", KeyUrl = "https://console.mistral.ai/api-keys",
                             Models = new[] { "mistral-small-latest", "mistral-large-latest" } },
            new AiProvider { Id = "ollama", Name = "Ollama", Base = "http://localhost:11434/v1/", NoKey = true, Models = new string[0] },
            new AiProvider { Id = "lmstudio", Name = "LM Studio", Base = "http://localhost:1234/v1/", NoKey = true, Models = new string[0] },
            new AiProvider { Id = "custom", Name = "Kustom", Base = "", KeyHint = "…", Stt = true, SttModel = "whisper-1", Models = new string[0] },
        };

        // Speech choices: these plus offline
        public const string Offline = "lokal";

        static Settings settings;
        public static void Bind(Settings s) { settings = s; }

        public static AiProvider Find(string id)
        {
            foreach (var p in Providers) if (p.Id == id) return p;
            return null;
        }

        // Servers on this laptop say "Local" first
        public static string Label(AiProvider p)
        {
            if (p.Id == "custom") return Lang.T("Kustom");
            return p.NoKey ? Lang.F("Lokal ({0})", p.Name) : p.Name;
        }

        // Groq keeps groq.key: nothing to migrate
        static string KeyPath(string id) { return Path.Combine(Settings.Dir, id == "groq" ? "groq.key" : "ai-" + id + ".key"); }

        public static string Key(AiProvider p) { return p == null ? "" : p.NoKey ? "" : Vault.Load(KeyPath(p.Id)) ?? ""; }
        public static void SaveKey(AiProvider p, string key) { Vault.Save(KeyPath(p.Id), key); Forget(p); }
        public static void DropKey(AiProvider p) { Vault.Delete(KeyPath(p.Id)); Forget(p); }

        // Model list learned again next use
        public static void Forget(AiProvider p) { lock (models) models.Remove(p.Id); }

        // Usable: key saved, or none needed
        public static bool Ready(AiProvider p)
        {
            if (p == null) return false;
            if (p.Id == "custom") return BaseUrl(p).Length > 0;
            return p.NoKey || Key(p).Length > 0;
        }

        public static string BaseUrl(AiProvider p)
        {
            if (p.Id != "custom") return p.Base;
            string b = settings == null ? "" : settings.Get("ai.custom.url", "").Trim();
            if (b.Length > 0 && !b.EndsWith("/")) b += "/";
            return b;
        }

        // Text: summaries, commands, selected text
        public static AiProvider Text
        {
            get
            {
                if (settings == null) return Find("groq");
                return Find(settings.Get("ai.text", "groq")) ?? Find("groq");
            }
        }

        // Speech: provider id or "lokal"
        public static string SttId
        {
            get
            {
                if (settings == null) return "groq";
                if (settings.Has("ai.stt")) return settings.Get("ai.stt", "groq");
                // Before 2.6.1: dictate.engine only
                return settings.Get("dictate.engine", "groq") == Offline ? Offline : "groq";
            }
        }

        public static AiProvider Stt { get { var p = Find(SttId); return p != null && p.Stt ? p : null; } }

        static Dictionary<string, string> Auth(AiProvider p, string key)
        {
            var h = new Dictionary<string, string>();
            if (p.Anthropic)
            {
                h["x-api-key"] = key;
                h["anthropic-version"] = "2023-06-01";
            }
            else if (key.Length > 0) h["Authorization"] = "Bearer " + key;
            if (p.Id == "openrouter") { h["HTTP-Referer"] = "https://odlaver.github.io/tepi/"; h["X-Title"] = "Tepi"; }
            return h;
        }

        // Short, per provider, never the raw body
        public static string Error(AiProvider p, WebResult r)
        {
            string n = Label(p);
            if (r.Status == 0) return p.NoKey ? Lang.F("{0} tidak jalan di laptop ini", n) : Lang.F("Tidak tersambung ke {0}", n);
            if (r.Status == 401 || r.Status == 403) return Lang.F("Key {0} ditolak", n);
            if (r.Status == 402) return Lang.F("Saldo {0} habis", n);
            if (r.Status == 404) return Lang.F("Model tidak ada di {0}", n);
            if (r.Status == 429) return Lang.F("Batas {0} tercapai, coba lagi sebentar", n);
            if (r.Status == 413) return Lang.F("Rekaman terlalu panjang untuk {0}", n);
            Log.Write("ai " + p.Id + " " + r.Status + ": " + (r.Body.Length > 300 ? r.Body.Substring(0, 300) : r.Body));
            return Lang.F("{0} menjawab {1}", n, r.Status);
        }

        // Null when the key works
        public static string Check(AiProvider p, string key)
        {
            if (p.Id == "custom" && BaseUrl(p).Length == 0) return Lang.T("Alamat server belum diisi");
            string path = p.Id == "openrouter" ? "key" : "models";
            var r = Web.Send("GET", BaseUrl(p) + path, Auth(p, key ?? ""), null, 15000);
            if (r.Ok) { if (path == "models") Remember(p, r.Body); return null; }
            return Error(p, r);
        }

        // Models the key can use, learned once
        static readonly Dictionary<string, List<string>> models = new Dictionary<string, List<string>>();

        static void Remember(AiProvider p, string body)
        {
            var have = new List<string>();
            try
            {
                var d = Json.Parse(body) as Dictionary<string, object>;
                foreach (var o in Json.Arr(d, "data"))
                {
                    var m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    string id = Json.Str(m, "id");
                    if (id.StartsWith("models/")) id = id.Substring(7);
                    if (id.Length > 0) have.Add(id);
                }
            }
            catch (Exception) { }
            lock (models) models[p.Id] = have;
        }

        // Typed in settings, else first known
        public static string Model(AiProvider p, string key)
        {
            string set = settings == null ? "" : settings.Get("ai." + p.Id + ".model", "").Trim();
            if (set.Length > 0) return set;
            List<string> have;
            lock (models) models.TryGetValue(p.Id, out have);
            if (have == null)
            {
                var r = Web.Send("GET", BaseUrl(p) + "models", Auth(p, key), null, 15000);
                if (r.Ok) Remember(p, r.Body);
                lock (models) models.TryGetValue(p.Id, out have);
            }
            if (have != null)
            {
                foreach (var m in p.Models) if (have.Contains(m)) return m;
                foreach (var m in have)
                {
                    string low = m.ToLowerInvariant();
                    if (low.Contains("whisper") || low.Contains("guard") || low.Contains("tts") || low.Contains("embed") || low.Contains("transcribe")) continue;
                    return m;
                }
            }
            return p.Models.Length > 0 ? p.Models[0] : "";
        }

        public static string DefaultModel(AiProvider p) { return p.Models.Length > 0 ? p.Models[0] : Lang.T("model pertama di server"); }

        // One reply: text, or tool calls
        public class Reply
        {
            public string Text = "";
            public readonly List<KeyValuePair<string, Dictionary<string, object>>> Calls = new List<KeyValuePair<string, Dictionary<string, object>>>();
        }

        public static string Chat(string system, string user, out string error)
        {
            var r = Ask(system, user, null, out error);
            return r == null ? null : r.Text;
        }

        // Tools as OpenAI function specs
        public static Reply Ask(string system, string user, object[] tools, out string error)
        {
            var turns = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("user", user) };
            return Ask(system, turns, tools, out error);
        }

        // Turns: role and text, oldest first, the user last
        public static Reply Ask(string system, List<KeyValuePair<string, string>> turns, object[] tools, out string error)
        {
            error = null;
            var p = Text;
            if (!Ready(p)) { error = p.Id == "custom" && BaseUrl(p).Length == 0 ? Lang.T("Alamat server belum diisi") : Lang.F("Key {0} belum diisi", Label(p)); return null; }
            string key = Key(p);
            string model = Model(p, key);
            if (model.Length == 0) { error = Lang.F("Model {0} belum dipilih", Label(p)); return null; }
            return p.Anthropic ? AskAnthropic(p, key, model, system, turns, tools, out error) : AskOpenAi(p, key, model, system, turns, tools, out error);
        }

        static Reply AskOpenAi(AiProvider p, string key, string model, string system, List<KeyValuePair<string, string>> turns, object[] tools, out string error)
        {
            error = null;
            var messages = new List<object> { Msg("system", system) };
            foreach (var t in turns) messages.Add(Msg(t.Key, t.Value));
            var payload = new Dictionary<string, object>
            {
                { "model", model },
                { "messages", messages.ToArray() },
            };
            // Reasoning models reject a set temperature
            if (!model.StartsWith("gpt-5") && !model.StartsWith("o1") && !model.StartsWith("o3") && !model.StartsWith("o4")) payload["temperature"] = 0.2;
            if (tools != null) { payload["tools"] = tools; payload["tool_choice"] = "auto"; }
            var r = Web.Send("POST", BaseUrl(p) + "chat/completions", Auth(p, key), Json.Encode(payload), 90000);
            // Local models without tools: plain text instead
            if (!r.Ok && r.Status == 400 && tools != null && (r.Body ?? "").IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                payload.Remove("tools");
                payload.Remove("tool_choice");
                r = Web.Send("POST", BaseUrl(p) + "chat/completions", Auth(p, key), Json.Encode(payload), 90000);
            }
            if (!r.Ok) { error = Error(p, r); return null; }
            try
            {
                var d = (Dictionary<string, object>)Json.Parse(r.Body);
                var first = (Dictionary<string, object>)Json.Arr(d, "choices")[0];
                var msg = (Dictionary<string, object>)first["message"];
                var reply = new Reply { Text = (Json.Str(msg, "content") ?? "").Trim() };
                foreach (var o in Json.Arr(msg, "tool_calls"))
                {
                    var call = o as Dictionary<string, object>;
                    var fn = Json.Obj(call, "function");
                    if (fn == null) continue;
                    Dictionary<string, object> args = null;
                    try { args = Json.Parse(Json.Str(fn, "arguments").Length > 0 ? Json.Str(fn, "arguments") : "{}") as Dictionary<string, object>; } catch (Exception) { }
                    reply.Calls.Add(new KeyValuePair<string, Dictionary<string, object>>(Json.Str(fn, "name"), args));
                }
                return reply;
            }
            catch (Exception) { error = Lang.F("Jawaban {0} tidak terbaca", Label(p)); return null; }
        }

        static Dictionary<string, object> Msg(string role, string content) { return new Dictionary<string, object> { { "role", role }, { "content", content } }; }

        // Raw HTTP; no SDK on .NET 4.8
        static Reply AskAnthropic(AiProvider p, string key, string model, string system, List<KeyValuePair<string, string>> turns, object[] tools, out string error)
        {
            error = null;
            var messages = new List<object>();
            // Claude's turns open with the user
            foreach (var t in turns) if (messages.Count > 0 || t.Key == "user") messages.Add(Msg(t.Key, t.Value));
            var payload = new Dictionary<string, object>
            {
                { "model", model },
                { "max_tokens", 16000 },
                { "system", system },
                { "messages", messages.ToArray() },
                // Island tasks are short
                { "output_config", new Dictionary<string, object> { { "effort", "low" } } },
            };
            var headers = Auth(p, key);
            // Refusals retry on a sibling model
            if (model == "claude-opus-5-5" || model == "claude-opus-5" || model == "claude-fable-5-1" || model == "claude-sonnet-5-5")
            {
                headers["anthropic-beta"] = "server-side-fallback-2026-07-01";
                payload["fallbacks"] = "default";
            }
            if (tools != null)
            {
                var list = new List<object>();
                foreach (var t in tools)
                {
                    var fn = Json.Obj(t as Dictionary<string, object>, "function");
                    if (fn == null) continue;
                    list.Add(new Dictionary<string, object> { { "name", Json.Str(fn, "name") }, { "description", Json.Str(fn, "description") }, { "input_schema", fn["parameters"] } });
                }
                payload["tools"] = list.ToArray();
                payload["tool_choice"] = new Dictionary<string, object> { { "type", "auto" } };
            }
            var r = Web.Send("POST", BaseUrl(p) + "messages", headers, Json.Encode(payload), 120000);
            if (!r.Ok) { error = Error(p, r); return null; }
            try
            {
                var d = (Dictionary<string, object>)Json.Parse(r.Body);
                if (Json.Str(d, "stop_reason") == "refusal") { error = Lang.T("Claude menolak permintaan ini"); return null; }
                var reply = new Reply();
                var text = new System.Text.StringBuilder();
                foreach (var o in Json.Arr(d, "content"))
                {
                    var b = o as Dictionary<string, object>;
                    if (b == null) continue;
                    string type = Json.Str(b, "type");
                    if (type == "text") text.Append(Json.Str(b, "text"));
                    else if (type == "tool_use") reply.Calls.Add(new KeyValuePair<string, Dictionary<string, object>>(Json.Str(b, "name"), Json.Obj(b, "input")));
                }
                reply.Text = text.ToString().Trim();
                return reply;
            }
            catch (Exception) { error = Lang.F("Jawaban {0} tidak terbaca", Label(p)); return null; }
        }

        // Speech through the chosen provider
        public static SpeechResult Transcribe(byte[] wav, string lang, string prompt, bool segments)
        {
            var p = Stt;
            if (p == null) return new SpeechResult { Error = Lang.T("Mesin suara belum dipilih") };
            if (!Ready(p)) return new SpeechResult { Error = p.Id == "custom" && BaseUrl(p).Length == 0 ? Lang.T("Alamat server belum diisi") : Lang.F("Key {0} belum diisi", Label(p)) };
            string model = settings == null ? "" : settings.Get("ai." + p.Id + ".stt", "").Trim();
            if (model.Length == 0) model = p.SttModel;
            return Speech.Remote(p, Key(p), BaseUrl(p), model, wav, lang, prompt, segments);
        }

        public static string SttMissing()
        {
            if (SttId == Offline) return null;
            var p = Stt;
            if (p == null) return Lang.T("Mesin suara belum dipilih");
            if (!Ready(p)) return p.Id == "custom" && BaseUrl(p).Length == 0 ? Lang.T("Alamat server belum diisi") : Lang.F("Key {0} belum diisi", Label(p));
            return null;
        }

        internal static Dictionary<string, string> Headers(AiProvider p, string key) { return Auth(p, key); }
    }
}
