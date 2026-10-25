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
                case "gemini": return "Gemini";
                case "antigravity": return "Antigravity";
            }
            return "Claude";
        }

        public static bool Normalize(HookEvent ev)
        {
            switch (ev.Agent)
            {
                case "codex": return true; // same schema as Claude Code
                case "gemini": return Gemini(ev);
                case "antigravity": return Antigravity(ev);
            }
            return false;
        }

        // Monitor only: Gemini hooks can't approve
        static bool Gemini(HookEvent ev)
        {
            var d = ev.Data;
            switch (ev.Name)
            {
                case "SessionStart":
                case "SessionEnd":
                    break;
                case "BeforeAgent": ev.Name = "UserPromptSubmit"; break;
                case "BeforeTool": ev.Name = "PreToolUse"; break;
                case "AfterTool": ev.Name = "PostToolUse"; break;
                case "AfterAgent":
                    ev.Name = "Stop";
                    d["last_assistant_message"] = Json.Str(d, "prompt_response");
                    break;
                case "Notification":
                    if (Json.Str(d, "notification_type") != "ToolPermission") return false;
                    d["notification_type"] = "permission_prompt";
                    break;
                default: return false;
            }
            string tool = Json.Str(d, "tool_name");
            if (tool.Length > 0) d["tool_name"] = GeminiTool(tool, Json.Obj(d, "tool_input"));
            return true;
        }

        static string GeminiTool(string tool, Dictionary<string, object> input)
        {
            if (input != null && !input.ContainsKey("file_path") && input.ContainsKey("absolute_path")) input["file_path"] = input["absolute_path"];
            switch (tool)
            {
                case "run_shell_command": return "Bash";
                case "write_file": return "Write";
                case "replace": return "Edit";
                case "read_file":
                case "read_many_files": return "Read";
                case "glob": return "Glob";
                case "search_file_content": return "Grep";
                case "web_fetch": return "WebFetch";
                case "google_web_search": return "WebSearch";
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
