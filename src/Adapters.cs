using System;
using System.Collections.Generic;

namespace Tepi
{
    // Other agents' events as Claude events
    static class AgentAdapters
    {
        public static string Label(string agent)
        {
            switch (agent)
            {
                case "codex": return "Codex";
                case "antigravity": return "Antigravity";
                case "copilot": return "Copilot";
            }
            return "Claude";
        }

        public static bool Normalize(HookEvent ev)
        {
            switch (ev.Agent)
            {
                case "codex": return true; // same schema as Claude Code
                case "antigravity": return Antigravity(ev);
                case "copilot": return Copilot(ev);
            }
            return false;
        }

        // camelCase payload; relay adds event name
        static bool Copilot(HookEvent ev)
        {
            var d = ev.Data;
            ev.SessionId = Json.Str(d, "sessionId");
            switch (ev.Name)
            {
                case "sessionStart": ev.Name = "SessionStart"; break;
                case "userPromptSubmitted": ev.Name = "UserPromptSubmit"; break;
                case "preToolUse": ev.Name = "PreToolUse"; break;
                case "postToolUse": ev.Name = "PostToolUse"; break;
                case "agentStop": ev.Name = "Stop"; break;
                case "sessionEnd": ev.Name = "SessionEnd"; break;
                default: return false;
            }
            var args = Json.Obj(d, "toolArgs");
            if (args != null)
            {
                if (!args.ContainsKey("file_path") && args.ContainsKey("path")) args["file_path"] = args["path"];
                d["tool_name"] = CopilotTool(Json.Str(d, "toolName"));
                d["tool_input"] = args;
            }
            d["hook_event_name"] = ev.Name;
            return true;
        }

        static string CopilotTool(string tool)
        {
            switch (tool)
            {
                case "powershell":
                case "bash": return "Bash";
                case "edit": return "Edit";
                case "create": return "Write";
                case "view": return "Read";
                case "glob": return "Glob";
                case "grep": return "Grep";
                case "web_fetch": return "WebFetch";
            }
            return tool;
        }

        // camelCase payload; relay adds event name
        static bool Antigravity(HookEvent ev)
        {
            var d = ev.Data;
            ev.SessionId = Json.Str(d, "conversationId");
            var roots = Json.Arr(d, "workspacePaths");
            if (roots.Length > 0) d["cwd"] = LocalPath(roots[0] as string);
            switch (ev.Name)
            {
                case "SessionStart": break;
                case "PreInvocation": ev.Name = "UserPromptSubmit"; break;
                case "PreToolUse":
                case "PostToolUse":
                    var call = Json.Obj(d, "toolCall");
                    var args = Json.Obj(call, "args") ?? new Dictionary<string, object>();
                    string name = Json.Str(call, "name");
                    if (name == "run_command")
                    {
                        name = "Bash";
                        args["command"] = Json.Str(args, "CommandLine");
                    }
                    d["tool_name"] = name;
                    d["tool_input"] = args;
                    break;
                case "Stop":
                    if (Json.Str(d, "terminationReason") == "error") ev.Name = "StopFailure";
                    break;
                default: return false;
            }
            d["hook_event_name"] = ev.Name;
            return true;
        }

        static string LocalPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            Uri u;
            return p.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(p, UriKind.Absolute, out u) ? u.LocalPath : p;
        }
    }
}
