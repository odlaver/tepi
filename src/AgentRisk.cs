using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Tepi
{
    // Requests that always need a fresh yes
    static class AgentRisk
    {
        const RegexOptions I = RegexOptions.IgnoreCase;
        static readonly Regex Wipe = new Regex(@"(?<!\bgit\s+)\brm\s+(-\w*[rR]\w*|--recursive)\b|\bremove-item\b[^|;&\n]*-recurse|\b(rmdir|rd)\s+/s\b|\bdel\s+(/\w\s+)*/s\b|\bgit\s+clean\s+-\w*f|\bgit\s+reset\s+--hard\b|\bformat(-volume)?\s+[a-z]:|\bdiskpart\b|\bmkfs\b|\bdd\s+if=", I);
        static readonly Regex FetchRun = new Regex(@"\b(curl|wget|iwr|irm|invoke-webrequest|invoke-restmethod)\b[^|;&\n]*\|\s*(sudo\s+)?((sh|bash|zsh|iex|invoke-expression|pwsh|powershell)\b|(python3?|node)(\s+-)?\s*($|[;&|]))|\biex\s*\(\s*(new-object|iwr|irm|invoke-)|\b(pwsh|powershell)(\.exe)?\b[^|;&\n]*\s-e(nc|ncodedcommand)?\s", I);
        static readonly Regex SecretPath = new Regex(@"(^|[\\/\s""'=])(\.env(?!\.(example|sample|template|dist)\b)(\.[\w-]+)?|id_rsa|id_ed25519|id_ecdsa|[\w-]+\.pem|[\w-]+\.key|\.npmrc|\.pypirc|\.netrc|\.git-credentials|credentials\.json|kulia\.session|groq\.key)($|[\s""';|&])|[\\/]\.ssh[\\/]|[\\/]\.aws[\\/]", I);
        static readonly Regex ForcePush = new Regex(@"\bgit\s+push\b[^|;&\n]*(--force\b|-f\b|--force-with-lease\b)", I);

        // Short reason, or null when ordinary
        public static string Of(string tool, Dictionary<string, object> input)
        {
            if (input == null) return null;
            if (tool == "Bash" || tool == "PowerShell")
            {
                string cmd = Json.Str(input, "command");
                if (Wipe.IsMatch(cmd)) return Lang.T("hapus massal");
                if (FetchRun.IsMatch(cmd)) return Lang.T("unduh lalu jalankan");
                if (SecretPath.IsMatch(cmd)) return Lang.T("menyentuh rahasia");
                if (ForcePush.IsMatch(cmd)) return Lang.T("push paksa");
                return null;
            }
            string path = Json.Str(input, "file_path");
            if (path.Length > 0 && SecretPath.IsMatch(" " + path + " ")) return tool == "Read" ? Lang.T("membaca rahasia") : Lang.T("menyentuh rahasia");
            return null;
        }
    }
}
