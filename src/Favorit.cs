using System;
using System.Text.RegularExpressions;
using System.Threading;

namespace Tepi
{
    // "putar lagu biasa": opens the user's own YouTube link
    partial class Island
    {
        const string FavKey = "fav.music";

        static readonly Regex FavAsk = new Regex(
            @"(?:^|\s)(?:(?:lagu|musik|music|playlist|mix)\s+(?:yang\s+)?(?:(?:seperti|kayak|kaya|kek)\s+)?(?:biasa|favorit(?:ku|nya)?|favorite|kesukaan(?:ku)?|andalan(?:ku)?)" +
            @"|(?:my\s+|the\s+)?(?:usual|favou?rite)\s+(?:music|songs?|playlist|mix)" +
            @"|(?:style|gaya|genre|jenis)\s+(?:musik|lagu)\s+(?:yang\s+)?(?:(?:seperti|kayak|kaya|kek)\s+)?biasa" +
            @"|(?:putar|puter|putarin|puterin|setel|setelin|hidupin|hidupkan|nyalain|nyalakan|mainin|mainkan|play)\s+(?:yang\s+)?(?:seperti|kayak|kaya|kek)\s+biasa)(?:\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex ListId = new Regex(@"[?&]list=(?<id>[A-Za-z0-9_-]{10,64})", RegexOptions.Compiled);
        static readonly Regex VideoId = new Regex(@"(?:[?&]v=|youtu\.be/)(?<id>[A-Za-z0-9_-]{11})", RegexOptions.Compiled);

        internal static bool IsFavAsk(string raw) { return FavAsk.IsMatch(" " + Perintah.Plain(raw) + " "); }

        // A watch link that starts playing; null when it is not YouTube
        internal static string FavUrl(string link)
        {
            link = (link ?? "").Trim();
            Uri u;
            if (!Uri.TryCreate(link, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps) return null;
            string host = u.Host.ToLowerInvariant();
            if (host != "youtu.be" && host != "youtube.com" && !host.EndsWith(".youtube.com", StringComparison.Ordinal)) return null;
            var list = ListId.Match(link);
            var video = VideoId.Match(link);
            string v = video.Success ? video.Groups["id"].Value : null, l = list.Success ? list.Groups["id"].Value : null;
            // A Mix list is named after its first video
            if (v == null && l != null && l.StartsWith("RD", StringComparison.Ordinal) && l.Length == 13) v = l.Substring(2);
            if (v == null && l == null) return null;
            string url = "https://www.youtube.com/watch?";
            if (v != null) url += "v=" + v;
            if (l != null) url += (v != null ? "&" : "") + "list=" + l;
            return url;
        }

        // Network and browser work, so off the UI thread
        bool StartFavorite(ChatMsg me)
        {
            if (!IsFavAsk(me.Text)) return false;
            string url = FavUrl(settings.Get(FavKey, ""));
            if (url == null)
            {
                SuruhDone(me, Said(Lang.T("Belum ada lagu favorit. Tempel tautan YouTube di Pengaturan › Musik")));
                return true;
            }
            suruhBusy = true;
            dictState = DictState.Working;
            Kick();
            ChatChanged(null);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                try { OpenSong(url, ""); }
                catch (Exception ex) { Log.Write("favorit buka: " + ex.Message); err = Lang.T("Browser tidak bisa dibuka"); }
                Post(delegate
                {
                    suruhBusy = false;
                    dictState = DictState.Off;
                    Kick();
                    SuruhDone(me, err != null ? Said(err) : Did(IcoPlaySong, Lang.T("Memutar lagu favorit")));
                });
            });
            return true;
        }
    }
}
