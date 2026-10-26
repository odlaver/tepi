using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tepi
{
    // UI text: Indonesian source, English on request
    static class Lang
    {
        public static bool English;
        static readonly Dictionary<string, string> en = new Dictionary<string, string>();

        public static string T(string id)
        {
            string s;
            return English && en.TryGetValue(id, out s) ? s : id;
        }

        public static string F(string id, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, T(id), args);
        }
    }
}
