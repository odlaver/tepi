using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Tepi
{
    // PDF values: dict, array (List<object>), name, ref, stream, raw token
    class PdfName
    {
        public readonly string Name;
        public PdfName(string name) { Name = name; }
    }

    class PdfRef
    {
        public readonly int Num, Gen;
        public PdfRef(int num, int gen) { Num = num; Gen = gen; }
    }

    // Numbers, strings, true/false/null: written back byte for byte
    class PdfRaw
    {
        public readonly byte[] Bytes;
        public PdfRaw(byte[] bytes) { Bytes = bytes; }
        public PdfRaw(string text) { Bytes = Encoding.ASCII.GetBytes(text); }
        public override string ToString() { return Encoding.ASCII.GetString(Bytes); }
    }

    // A bare keyword met while reading a value
    class PdfWord
    {
        public readonly string Text;
        public PdfWord(string text) { Text = text; }
    }

    class PdfDict
    {
        public readonly List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();

        public object Get(string key)
        {
            for (int i = Items.Count - 1; i >= 0; i--) if (Items[i].Key == key) return Items[i].Value;
            return null;
        }

        public bool Has(string key) { return Get(key) != null; }

        public void Set(string key, object value)
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Key == key) { Items[i] = new KeyValuePair<string, object>(key, value); return; }
            Items.Add(new KeyValuePair<string, object>(key, value));
        }

        public void Remove(string key) { Items.RemoveAll(kv => kv.Key == key); }
    }

    class PdfStream
    {
        public PdfDict Dict;
        public byte[] Data;
    }

    // Reads one PDF: xref tables or streams, object streams
    class PdfFile
    {
        readonly byte[] b;
        readonly Dictionary<int, long> offsets = new Dictionary<int, long>();
        readonly Dictionary<int, int[]> packed = new Dictionary<int, int[]>();
        readonly HashSet<int> freed = new HashSet<int>();
        readonly Dictionary<int, object> cache = new Dictionary<int, object>();
        readonly Dictionary<int, KeyValuePair<byte[], int[]>> objStms = new Dictionary<int, KeyValuePair<byte[], int[]>>();
        Dictionary<int, long> scanned;
        int headerAt;
        public PdfDict Trailer;
        public string Version = "1.4";

        public PdfFile(byte[] data)
        {
            b = data;
            headerAt = Find(b, "%PDF-", 0, Math.Min(b.Length, 1024));
            if (headerAt < 0) throw new InvalidDataException(Lang.T("Bukan berkas PDF"));
            Version = Encoding.ASCII.GetString(b, headerAt + 5, 3);
            try { LoadXref(); }
            catch (Exception ex) { Log.Write("pdf xref: " + ex.Message); Trailer = null; }
            // Broken or missing xref: rebuild from the objects
            if (Trailer == null || !(Resolve(Trailer.Get("Root")) is PdfDict)) Rebuild();
            if (Trailer == null || !(Resolve(Trailer.Get("Root")) is PdfDict)) throw new InvalidDataException(Lang.T("PDF tidak bisa dibaca"));
            var cat = (PdfDict)Resolve(Trailer.Get("Root"));
            var v = cat.Get("Version") as PdfName;
            if (v != null && string.CompareOrdinal(v.Name, Version) > 0) Version = v.Name;
        }

        public static PdfFile Open(string path) { return new PdfFile(File.ReadAllBytes(path)); }

        public bool Encrypted { get { return Trailer.Has("Encrypt"); } }

        public PdfDict Root { get { return (PdfDict)Resolve(Trailer.Get("Root")); } }

        // Xref

        void LoadXref()
        {
            int sx = FindLast(b, "startxref");
            if (sx < 0) throw new InvalidDataException("startxref");
            var lx = new PdfLexer(b, sx + 9);
            long pos = lx.Int();
            var seen = new HashSet<long>();
            while (pos > 0 && seen.Add(pos))
            {
                long at = pos < b.Length && Starts((int)pos) ? pos : pos + headerAt;
                if (at >= b.Length) break;
                var t = new PdfLexer(b, (int)at);
                t.Skip();
                PdfDict tr = t.Peek("xref") ? ReadTable(t) : ReadXrefStream((int)at);
                if (Trailer == null) Trailer = tr;
                // Hybrid files keep a stream beside the table
                long xs = PdfLexer.Long(tr.Get("XRefStm"));
                if (xs > 0 && seen.Add(xs)) ReadXrefStream((int)(xs + (Starts((int)xs) ? 0 : headerAt)));
                pos = PdfLexer.Long(tr.Get("Prev"));
            }
        }

        bool Starts(int pos)
        {
            var t = new PdfLexer(b, pos);
            t.Skip();
            return t.Pos < b.Length && (t.Peek("xref") || char.IsDigit((char)b[t.Pos]));
        }

        PdfDict ReadTable(PdfLexer t)
        {
            t.Pos += 4;
            while (true)
            {
                t.Skip();
                if (t.Peek("trailer")) { t.Pos += 7; return (PdfDict)t.Value(); }
                int start = (int)t.Int(), count = (int)t.Int();
                for (int i = 0; i < count; i++)
                {
                    long off = t.Int();
                    t.Int();
                    string kind = t.Word();
                    Note(start + i, kind == "n" ? off : -1, null);
                }
            }
        }

        PdfDict ReadXrefStream(int at)
        {
            int num, gen;
            var s = ReadAt(at, out num, out gen) as PdfStream;
            if (s == null) throw new InvalidDataException("xref stream");
            var data = Decode(s);
            if (data == null) throw new InvalidDataException("xref filter");
            var w = s.Dict.Get("W") as List<object>;
            int w0 = (int)PdfLexer.Long(w[0]), w1 = (int)PdfLexer.Long(w[1]), w2 = (int)PdfLexer.Long(w[2]);
            var index = s.Dict.Get("Index") as List<object>;
            if (index == null) index = new List<object> { new PdfRaw("0"), s.Dict.Get("Size") };
            int p = 0, row = w0 + w1 + w2;
            for (int k = 0; k + 1 < index.Count; k += 2)
            {
                int first = (int)PdfLexer.Long(index[k]), count = (int)PdfLexer.Long(index[k + 1]);
                for (int i = 0; i < count && p + row <= data.Length; i++, p += row)
                {
                    long type = w0 == 0 ? 1 : Field(data, p, w0), f1 = Field(data, p + w0, w1), f2 = Field(data, p + w0 + w1, w2);
                    if (type == 1) Note(first + i, f1, null);
                    else if (type == 2) Note(first + i, -1, new[] { (int)f1, (int)f2 });
                    else Note(first + i, -1, null);
                }
            }
            return s.Dict;
        }

        static long Field(byte[] d, int at, int width)
        {
            long v = 0;
            for (int i = 0; i < width; i++) v = (v << 8) | d[at + i];
            return v;
        }

        // Newest section wins: older entries never override
        void Note(int num, long off, int[] inStm)
        {
            if (offsets.ContainsKey(num) || packed.ContainsKey(num) || freed.Contains(num)) return;
            if (inStm != null) packed[num] = inStm;
            else if (off > 0) offsets[num] = off;
            else freed.Add(num);
        }

        // Every "n g obj" in the file; the last one wins
        Dictionary<int, long> Scan()
        {
            if (scanned != null) return scanned;
            scanned = new Dictionary<int, long>();
            for (int i = Find(b, "obj", 0, b.Length); i >= 0; i = Find(b, "obj", i + 3, b.Length))
            {
                int j = i - 1;
                while (j >= 0 && PdfLexer.White(b[j])) j--;
                int ge = j;
                while (j >= 0 && char.IsDigit((char)b[j])) j--;
                if (j == ge) continue;
                int gs = j + 1;
                while (j >= 0 && PdfLexer.White(b[j])) j--;
                int ne = j;
                while (j >= 0 && char.IsDigit((char)b[j])) j--;
                if (j == ne || ne - j > 9) continue;
                if (j >= 0 && !PdfLexer.White(b[j]) && !PdfLexer.Delim(b[j])) continue;
                int num = int.Parse(Encoding.ASCII.GetString(b, j + 1, ne - j), CultureInfo.InvariantCulture);
                scanned[num] = j + 1;
            }
            return scanned;
        }

        void Rebuild()
        {
            offsets.Clear();
            packed.Clear();
            freed.Clear();
            cache.Clear();
            foreach (var kv in Scan()) offsets[kv.Key] = kv.Value;
            PdfDict found = null;
            // Last trailer, else an xref stream, else the catalog
            int tr = FindLast(b, "trailer");
            if (tr >= 0) try { var t = new PdfLexer(b, tr + 7); found = t.Value() as PdfDict; } catch (Exception) { }
            foreach (var kv in offsets)
            {
                if (found != null && Resolve(found.Get("Root")) is PdfDict) break;
                PdfDict d = null;
                try { var o = Get(kv.Key); var st = o as PdfStream; d = st != null ? st.Dict : o as PdfDict; } catch (Exception) { }
                if (d == null) continue;
                var type = d.Get("Type") as PdfName;
                if (type != null && type.Name == "XRef" && d.Has("Root")) found = d;
                else if (type != null && type.Name == "Catalog") { found = new PdfDict(); found.Set("Root", new PdfRef(kv.Key, 0)); }
            }
            Trailer = found;
        }

        // Objects

        public object Get(int num)
        {
            object v;
            if (cache.TryGetValue(num, out v)) return v;
            cache[num] = null;
            v = null;
            long off;
            int[] at;
            try
            {
                if (offsets.TryGetValue(num, out off)) v = Load(num, off);
                else if (packed.TryGetValue(num, out at)) v = Packed(at[0], at[1], num);
            }
            catch (Exception ex) { Log.Write("pdf obj " + num + ": " + ex.Message); v = null; }
            // Bad offset: the scan knows better
            if (v == null && Scan().TryGetValue(num, out off) && (!offsets.ContainsKey(num) || offsets[num] != off))
                try { v = Load(num, off); } catch (Exception) { }
            cache[num] = v;
            return v;
        }

        object Load(int num, long off)
        {
            foreach (long at in new[] { off, off + headerAt })
            {
                if (at < 0 || at >= b.Length) continue;
                int n, g;
                try
                {
                    var o = ReadAt((int)at, out n, out g);
                    if (n == num) return o;
                }
                catch (Exception) { }
                if (headerAt == 0) break;
            }
            return null;
        }

        public object Resolve(object v)
        {
            for (int hops = 0; hops < 32; hops++)
            {
                var r = v as PdfRef;
                if (r == null) return v;
                v = Get(r.Num);
            }
            return null;
        }

        object ReadAt(int at, out int num, out int gen)
        {
            var t = new PdfLexer(b, at);
            num = (int)t.Int();
            gen = (int)t.Int();
            t.Expect("obj");
            object v = t.Value();
            t.Skip();
            var dict = v as PdfDict;
            if (dict == null || !t.Peek("stream")) return v;
            t.Pos += 6;
            if (t.Pos < b.Length && b[t.Pos] == '\r') t.Pos++;
            if (t.Pos < b.Length && b[t.Pos] == '\n') t.Pos++;
            int start = t.Pos;
            long len = -1;
            var lv = dict.Get("Length");
            // Lengths may sit in another object
            if (lv is PdfRef) { var lr = (PdfRef)lv; if (lr.Num != num) lv = Get(lr.Num); }
            if (lv is PdfRaw) len = PdfLexer.Long(lv);
            int end = start + (int)Math.Max(0, len);
            if (len < 0 || end > b.Length || !EndsAt(end))
            {
                end = Find(b, "endstream", start, b.Length);
                if (end < 0) throw new InvalidDataException("endstream");
                while (end > start && (b[end - 1] == '\n' || b[end - 1] == '\r')) end--;
            }
            var data = new byte[end - start];
            Buffer.BlockCopy(b, start, data, 0, data.Length);
            return new PdfStream { Dict = dict, Data = data };
        }

        bool EndsAt(int end)
        {
            var t = new PdfLexer(b, end);
            t.Skip();
            return t.Peek("endstream");
        }

        object Packed(int stmNum, int index, int num)
        {
            KeyValuePair<byte[], int[]> entry;
            if (!objStms.TryGetValue(stmNum, out entry))
            {
                var s = Get(stmNum) as PdfStream;
                if (s == null) return null;
                var data = Decode(s);
                if (data == null) return null;
                int n = (int)PdfLexer.Long(s.Dict.Get("N")), first = (int)PdfLexer.Long(s.Dict.Get("First"));
                var head = new PdfLexer(data, 0);
                var pairs = new int[n * 2 + 1];
                for (int i = 0; i < n; i++) { pairs[i * 2] = (int)head.Int(); pairs[i * 2 + 1] = (int)head.Int() + first; }
                pairs[n * 2] = data.Length;
                entry = new KeyValuePair<byte[], int[]>(data, pairs);
                objStms[stmNum] = entry;
            }
            var pr = entry.Value;
            int count = (pr.Length - 1) / 2;
            int k = index >= 0 && index < count && pr[index * 2] == num ? index : -1;
            for (int i = 0; k < 0 && i < count; i++) if (pr[i * 2] == num) k = i;
            if (k < 0) return null;
            return new PdfLexer(entry.Key, pr[k * 2 + 1]).Value();
        }

        // Pages in order, with what they inherit
        public List<KeyValuePair<PdfRef, PdfDict>> Pages()
        {
            var list = new List<KeyValuePair<PdfRef, PdfDict>>();
            var seen = new HashSet<int>();
            Walk(Root.Get("Pages"), new PdfDict(), list, seen, 0);
            return list;
        }

        void Walk(object node, PdfDict inherit, List<KeyValuePair<PdfRef, PdfDict>> list, HashSet<int> seen, int depth)
        {
            var r = node as PdfRef;
            if (r == null || depth > 64 || !seen.Add(r.Num)) return;
            var d = Resolve(r) as PdfDict;
            if (d == null) return;
            var mine = new PdfDict();
            foreach (var kv in inherit.Items) mine.Set(kv.Key, kv.Value);
            foreach (var key in Inherited) if (d.Has(key)) mine.Set(key, d.Get(key));
            var kids = Resolve(d.Get("Kids")) as List<object>;
            var type = d.Get("Type") as PdfName;
            if (kids != null && (type == null || type.Name != "Page")) { foreach (var k in kids) Walk(k, mine, list, seen, depth + 1); return; }
            list.Add(new KeyValuePair<PdfRef, PdfDict>(r, mine));
        }

        public static readonly string[] Inherited = { "Resources", "MediaBox", "CropBox", "Rotate" };

        // Filters

        public byte[] Decode(PdfStream s)
        {
            var filter = Resolve(s.Dict.Get("Filter"));
            var parms = Resolve(s.Dict.Get("DecodeParms"));
            var names = new List<string>();
            if (filter is PdfName) names.Add(((PdfName)filter).Name);
            else if (filter is List<object>) foreach (var f in (List<object>)filter) { var n = Resolve(f) as PdfName; if (n != null) names.Add(n.Name); }
            byte[] data = s.Data;
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i] != "FlateDecode" && names[i] != "Fl") return null;
                var p = parms is List<object> ? Resolve(((List<object>)parms)[Math.Min(i, ((List<object>)parms).Count - 1)]) as PdfDict : parms as PdfDict;
                data = PdfLite.Inflate(data);
                if (data == null) return null;
                if (p != null) data = Unpredict(data, p);
                if (data == null) return null;
            }
            return data;
        }

        // PNG predictors, as used by xref and object streams
        byte[] Unpredict(byte[] d, PdfDict p)
        {
            long pred = PdfLexer.Long(Resolve(p.Get("Predictor")));
            if (pred < 10) return pred <= 1 ? d : null;
            int colors = Math.Max(1, (int)PdfLexer.Long(Resolve(p.Get("Colors")), 1));
            int bpc = Math.Max(1, (int)PdfLexer.Long(Resolve(p.Get("BitsPerComponent")), 8));
            int cols = Math.Max(1, (int)PdfLexer.Long(Resolve(p.Get("Columns")), 1));
            return PdfLite.Unpng(d, (cols * colors * bpc + 7) / 8, Math.Max(1, colors * bpc / 8));
        }

        // Bytes

        public static int Find(byte[] d, string s, int from, int to)
        {
            to = Math.Min(to, d.Length);
            for (int i = Math.Max(0, from); i <= to - s.Length; i++)
            {
                int k = 0;
                while (k < s.Length && d[i + k] == s[k]) k++;
                if (k == s.Length) return i;
            }
            return -1;
        }

        static int FindLast(byte[] d, string s)
        {
            for (int i = d.Length - s.Length; i >= 0; i--)
            {
                int k = 0;
                while (k < s.Length && d[i + k] == s[k]) k++;
                if (k == s.Length) return i;
            }
            return -1;
        }
    }

    class PdfLexer
    {
        readonly byte[] b;
        public int Pos;

        public PdfLexer(byte[] data, int pos) { b = data; Pos = pos; }

        // Names keep every byte, even unescaped high ones
        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static bool White(byte c) { return c == 0 || c == 9 || c == 10 || c == 12 || c == 13 || c == 32; }
        public static bool Delim(byte c) { return c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%'; }

        public void Skip()
        {
            while (Pos < b.Length)
            {
                if (White(b[Pos])) { Pos++; continue; }
                if (b[Pos] != '%') return;
                while (Pos < b.Length && b[Pos] != '\n' && b[Pos] != '\r') Pos++;
            }
        }

        public bool Peek(string word)
        {
            if (Pos + word.Length > b.Length) return false;
            for (int i = 0; i < word.Length; i++) if (b[Pos + i] != word[i]) return false;
            int end = Pos + word.Length;
            return end >= b.Length || White(b[end]) || Delim(b[end]);
        }

        public void Expect(string word)
        {
            Skip();
            if (!Peek(word)) throw new InvalidDataException(word);
            Pos += word.Length;
        }

        public string Word()
        {
            Skip();
            int start = Pos;
            while (Pos < b.Length && !White(b[Pos]) && !Delim(b[Pos])) Pos++;
            if (Pos == start) throw new InvalidDataException("token");
            return Latin1.GetString(b, start, Pos - start);
        }

        public long Int()
        {
            long v;
            string w = Word();
            if (!long.TryParse(w, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) throw new InvalidDataException("int " + w);
            return v;
        }

        public static long Long(object v) { return Long(v, 0); }

        public static long Long(object v, long fallback)
        {
            var r = v as PdfRaw;
            if (r == null) return fallback;
            double d;
            return double.TryParse(r.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? (long)d : fallback;
        }

        static bool IsInt(string w)
        {
            if (w.Length == 0) return false;
            for (int i = 0; i < w.Length; i++) if (!char.IsDigit(w[i]) && !(i == 0 && (w[i] == '-' || w[i] == '+'))) return false;
            return true;
        }

        static bool IsNumber(string w)
        {
            double d;
            return w.Length > 0 && (char.IsDigit(w[0]) || w[0] == '-' || w[0] == '+' || w[0] == '.') && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        public object Value()
        {
            Skip();
            if (Pos >= b.Length) throw new InvalidDataException("eof");
            byte c = b[Pos];
            if (c == '/')
            {
                int start = ++Pos;
                while (Pos < b.Length && !White(b[Pos]) && !Delim(b[Pos])) Pos++;
                return new PdfName(Latin1.GetString(b, start, Pos - start));
            }
            if (c == '(') return new PdfRaw(Slice(Pos, LiteralEnd()));
            if (c == '<' && Pos + 1 < b.Length && b[Pos + 1] == '<')
            {
                Pos += 2;
                var d = new PdfDict();
                while (true)
                {
                    Skip();
                    if (Pos + 1 >= b.Length) throw new InvalidDataException("dict");
                    if (b[Pos] == '>' && b[Pos + 1] == '>') { Pos += 2; return d; }
                    var key = Value() as PdfName;
                    if (key == null) throw new InvalidDataException("key");
                    Skip();
                    // A key with nothing after it
                    if (b[Pos] == '>' && Pos + 1 < b.Length && b[Pos + 1] == '>') { Pos += 2; return d; }
                    d.Set(key.Name, Value());
                }
            }
            if (c == '<')
            {
                int start = Pos;
                while (Pos < b.Length && b[Pos] != '>') Pos++;
                Pos++;
                return new PdfRaw(Slice(start, Pos));
            }
            if (c == '[')
            {
                Pos++;
                var list = new List<object>();
                while (true)
                {
                    Skip();
                    if (Pos >= b.Length) throw new InvalidDataException("array");
                    if (b[Pos] == ']') { Pos++; return list; }
                    list.Add(Value());
                }
            }
            if (c == ']' || c == '>' || c == ')' || c == '{' || c == '}') { Pos++; return new PdfWord(((char)c).ToString()); }
            string w = Word();
            if (IsInt(w))
            {
                // "n g R" is a reference
                int save = Pos;
                try
                {
                    Skip();
                    int s2 = Pos;
                    while (Pos < b.Length && char.IsDigit((char)b[Pos])) Pos++;
                    if (Pos > s2)
                    {
                        string g = Encoding.ASCII.GetString(b, s2, Pos - s2);
                        Skip();
                        if (Peek("R"))
                        {
                            Pos++;
                            return new PdfRef(int.Parse(w, CultureInfo.InvariantCulture), int.Parse(g, CultureInfo.InvariantCulture));
                        }
                    }
                }
                catch (Exception) { }
                Pos = save;
                return new PdfRaw(w);
            }
            if (IsNumber(w) || w == "true" || w == "false" || w == "null") return new PdfRaw(w);
            return new PdfWord(w);
        }

        int LiteralEnd()
        {
            int depth = 0;
            for (; Pos < b.Length; Pos++)
            {
                byte c = b[Pos];
                if (c == '\\') { Pos++; continue; }
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) { Pos++; return Pos; }
            }
            return Pos;
        }

        byte[] Slice(int from, int to)
        {
            var r = new byte[Math.Max(0, to - from)];
            Buffer.BlockCopy(b, from, r, 0, r.Length);
            return r;
        }
    }

    // Rewrites, shrinks, and merges PDFs; no outside tools
    static class PdfLite
    {
        // Built output: objects numbered from 1
        class Doc
        {
            public readonly List<object> Objs = new List<object>();
            public int Add(object o) { Objs.Add(o); return Objs.Count; }
            public void Put(int num, object o) { Objs[num - 1] = o; }
        }

        // Deep copy into the new numbering; a queue, not recursion
        class Importer
        {
            readonly PdfFile src;
            readonly Doc doc;
            readonly Queue<KeyValuePair<int, object>> pending = new Queue<KeyValuePair<int, object>>();
            public readonly Dictionary<int, int> Map = new Dictionary<int, int>();

            public Importer(PdfFile src, Doc doc) { this.src = src; this.doc = doc; }

            public object Ref(PdfRef r)
            {
                int n;
                if (Map.TryGetValue(r.Num, out n)) return n == 0 ? (object)new PdfRaw("null") : new PdfRef(n, 0);
                var target = src.Get(r.Num);
                if (target == null) { Map[r.Num] = 0; return new PdfRaw("null"); }
                n = doc.Add(null);
                Map[r.Num] = n;
                pending.Enqueue(new KeyValuePair<int, object>(n, target));
                return new PdfRef(n, 0);
            }

            // Long chains (outlines, page trees) stay off the stack
            public void Drain()
            {
                while (pending.Count > 0)
                {
                    var kv = pending.Dequeue();
                    doc.Put(kv.Key, Copy(kv.Value));
                }
            }

            public object Copy(object v)
            {
                var r = v as PdfRef;
                if (r != null) return Ref(r);
                var d = v as PdfDict;
                if (d != null)
                {
                    var c = new PdfDict();
                    foreach (var kv in d.Items) c.Items.Add(new KeyValuePair<string, object>(kv.Key, Copy(kv.Value)));
                    return c;
                }
                var l = v as List<object>;
                if (l != null)
                {
                    var c = new List<object>(l.Count);
                    foreach (var o in l) c.Add(Copy(o));
                    return c;
                }
                var s = v as PdfStream;
                if (s != null) return new PdfStream { Dict = (PdfDict)Copy(s.Dict), Data = s.Data };
                return v;
            }
        }

        public class Result
        {
            public long Before, After;
            public int Images;
        }

        // Smaller copy: images resampled, objects packed
        public static Result Compress(string src, string dst, int maxSide, long quality, Action<float> progress)
        {
            var file = PdfFile.Open(src);
            if (file.Encrypted) throw new InvalidOperationException(Lang.T("PDF dikunci kata sandi"));
            var doc = new Doc();
            var imp = new Importer(file, doc);
            var root = imp.Copy(file.Trailer.Get("Root"));
            object info = file.Trailer.Has("Info") ? imp.Copy(file.Trailer.Get("Info")) : null;
            imp.Drain();
            var res = new Result { Before = new FileInfo(src).Length };
            // Masks stay exact: never JPEG them
            var masks = new HashSet<int>();
            foreach (var o in doc.Objs)
            {
                var s = o as PdfStream;
                if (s == null) continue;
                foreach (var key in new[] { "SMask", "Mask" }) { var mr = s.Dict.Get(key) as PdfRef; if (mr != null) masks.Add(mr.Num); }
            }
            int total = 0, done = 0;
            foreach (var o in doc.Objs) if (IsImage(o)) total++;
            for (int i = 0; i < doc.Objs.Count; i++)
            {
                var s = doc.Objs[i] as PdfStream;
                if (s == null) continue;
                if (IsImage(s))
                {
                    if (!masks.Contains(i + 1) && Shrink(doc, s, maxSide, quality)) res.Images++;
                    done++;
                    if (progress != null) progress(0.9f * done / Math.Max(1, total));
                }
                else Pack(s);
            }
            Write(dst, doc, root, info, file.Trailer.Get("ID"), file.Version);
            res.After = new FileInfo(dst).Length;
            return res;
        }

        // One PDF from many, pages in order
        public static void Merge(IList<string> files, string dst, Action<float> progress)
        {
            var doc = new Doc();
            int pagesNum = doc.Add(null);
            var kids = new List<object>();
            string version = "1.4";
            for (int f = 0; f < files.Count; f++)
            {
                var file = PdfFile.Open(files[f]);
                if (file.Encrypted) throw new InvalidOperationException(Path.GetFileName(files[f]) + ": " + Lang.T("PDF dikunci kata sandi"));
                if (string.CompareOrdinal(file.Version, version) > 0) version = file.Version;
                var imp = new Importer(file, doc);
                var pages = file.Pages();
                // Old tree nodes point at the new one
                MapTree(file, file.Root.Get("Pages"), imp, pagesNum, pages);
                var rootRef = file.Trailer.Get("Root") as PdfRef;
                if (rootRef != null) imp.Map[rootRef.Num] = 0;
                foreach (var pg in pages)
                {
                    var made = imp.Ref(pg.Key) as PdfRef;
                    if (made == null) continue;
                    imp.Drain();
                    var dict = doc.Objs[made.Num - 1] as PdfDict;
                    if (dict == null) continue;
                    foreach (var key in PdfFile.Inherited)
                        if (!dict.Has(key) && pg.Value.Has(key)) dict.Set(key, imp.Copy(pg.Value.Get(key)));
                    imp.Drain();
                    dict.Set("Parent", new PdfRef(pagesNum, 0));
                    kids.Add(made);
                }
                if (progress != null) progress(0.9f * (f + 1) / files.Count);
            }
            var pagesDict = new PdfDict();
            pagesDict.Set("Type", new PdfName("Pages"));
            pagesDict.Set("Kids", kids);
            pagesDict.Set("Count", new PdfRaw(kids.Count.ToString(CultureInfo.InvariantCulture)));
            doc.Put(pagesNum, pagesDict);
            var cat = new PdfDict();
            cat.Set("Type", new PdfName("Catalog"));
            cat.Set("Pages", new PdfRef(pagesNum, 0));
            int catNum = doc.Add(cat);
            foreach (var o in doc.Objs) { var s = o as PdfStream; if (s != null && !IsImage(s)) Pack(s); }
            Write(dst, doc, new PdfRef(catNum, 0), null, null, version);
        }

        static void MapTree(PdfFile file, object node, Importer imp, int pagesNum, List<KeyValuePair<PdfRef, PdfDict>> pages)
        {
            var leaves = new HashSet<int>();
            foreach (var pg in pages) leaves.Add(pg.Key.Num);
            var stack = new Stack<object>();
            stack.Push(node);
            var seen = new HashSet<int>();
            while (stack.Count > 0)
            {
                var r = stack.Pop() as PdfRef;
                if (r == null || !seen.Add(r.Num) || leaves.Contains(r.Num)) continue;
                imp.Map[r.Num] = pagesNum;
                var d = file.Resolve(r) as PdfDict;
                var kids = d != null ? file.Resolve(d.Get("Kids")) as List<object> : null;
                if (kids != null) foreach (var k in kids) stack.Push(k);
            }
        }

        public static int PageCount(string path)
        {
            var f = PdfFile.Open(path);
            return f.Pages().Count;
        }

        static bool IsImage(object o)
        {
            var s = o as PdfStream;
            if (s == null) return false;
            var st = s.Dict.Get("Subtype") as PdfName;
            return st != null && st.Name == "Image";
        }

        // Plain streams get Flate; metadata stays readable
        static void Pack(PdfStream s)
        {
            if (s.Dict.Has("Filter") || s.Data.Length < 64) return;
            var type = s.Dict.Get("Type") as PdfName;
            if (type != null && (type.Name == "Metadata" || type.Name == "XRef")) return;
            var z = Deflate(s.Data);
            if (z.Length >= s.Data.Length) return;
            s.Data = z;
            s.Dict.Set("Filter", new PdfName("FlateDecode"));
            s.Dict.Remove("DecodeParms");
        }

        // Photos: smaller and JPEG; masks, CMYK, odd ones untouched
        static bool Shrink(Doc doc, PdfStream s, int maxSide, long quality)
        {
            var d = s.Dict;
            if (s.Data.Length < 24 * 1024) return false;
            if (Raw(Deref(doc, d.Get("ImageMask"))) == "true" || d.Has("Decode") || d.Has("Mask")) return false;
            if (PdfLexer.Long(Deref(doc, d.Get("BitsPerComponent"))) != 8) return false;
            var smask = Deref(doc, d.Get("SMask")) as PdfStream;
            if (smask != null && smask.Dict.Has("Matte")) return false;
            int comps = Components(doc, Deref(doc, d.Get("ColorSpace")));
            if (comps != 1 && comps != 3) return false;
            int w = (int)PdfLexer.Long(Deref(doc, d.Get("Width"))), h = (int)PdfLexer.Long(Deref(doc, d.Get("Height")));
            if (w <= 0 || h <= 0 || (long)w * h > 60000000) return false;
            string filter = OnlyFilter(doc, Deref(doc, d.Get("Filter")));
            Bitmap bmp = null;
            try
            {
                if (filter == "DCTDecode") bmp = FromJpeg(s.Data, comps);
                else if (filter == "FlateDecode") bmp = FromFlate(doc, s, w, h, comps);
                if (bmp == null) return false;
                double k = Math.Min(1.0, (double)maxSide / Math.Max(bmp.Width, bmp.Height));
                int nw = Math.Max(1, (int)Math.Round(bmp.Width * k)), nh = Math.Max(1, (int)Math.Round(bmp.Height * k));
                byte[] jpg;
                using (var small = Resize(bmp, nw, nh)) jpg = Jpeg(small, quality);
                // Keep it only if it really saves
                if (jpg.Length > s.Data.Length * 0.9) return false;
                s.Data = jpg;
                d.Set("Filter", new PdfName("DCTDecode"));
                d.Remove("DecodeParms");
                d.Set("Width", new PdfRaw(nw.ToString(CultureInfo.InvariantCulture)));
                d.Set("Height", new PdfRaw(nh.ToString(CultureInfo.InvariantCulture)));
                d.Set("BitsPerComponent", new PdfRaw("8"));
                // Gray went out as RGB JPEG
                if (comps == 1) d.Set("ColorSpace", new PdfName("DeviceRGB"));
                return true;
            }
            catch (Exception ex) { Log.Write("pdf image: " + ex.Message); return false; }
            finally { if (bmp != null) bmp.Dispose(); }
        }

        static object Deref(Doc doc, object v)
        {
            for (int i = 0; i < 16; i++)
            {
                var r = v as PdfRef;
                if (r == null) return v;
                v = r.Num >= 1 && r.Num <= doc.Objs.Count ? doc.Objs[r.Num - 1] : null;
            }
            return null;
        }

        static string Raw(object v) { var r = v as PdfRaw; return r != null ? r.ToString() : ""; }

        static int Components(Doc doc, object cs)
        {
            var n = cs as PdfName;
            if (n != null) return n.Name == "DeviceRGB" || n.Name == "CalRGB" ? 3 : n.Name == "DeviceGray" || n.Name == "CalGray" ? 1 : 0;
            var l = cs as List<object>;
            if (l == null || l.Count < 2) return 0;
            var kind = Deref(doc, l[0]) as PdfName;
            if (kind == null || kind.Name != "ICCBased") return 0;
            var icc = Deref(doc, l[1]) as PdfStream;
            int c = icc != null ? (int)PdfLexer.Long(Deref(doc, icc.Dict.Get("N"))) : 0;
            return c == 1 || c == 3 ? c : 0;
        }

        static string OnlyFilter(Doc doc, object f)
        {
            var n = f as PdfName;
            if (n != null) return n.Name;
            var l = f as List<object>;
            if (l != null && l.Count == 1) { var one = Deref(doc, l[0]) as PdfName; if (one != null) return one.Name; }
            return "";
        }

        static Bitmap FromJpeg(byte[] data, int comps)
        {
            using (var ms = new MemoryStream(data))
            using (var img = Image.FromStream(ms, false, false))
            {
                // CMYK JPEGs keep their own look
                if ((((ImageFlags)img.Flags) & ImageFlags.ColorSpaceCmyk) != 0 || (((ImageFlags)img.Flags) & ImageFlags.ColorSpaceYcck) != 0) return null;
                var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, img.Width, img.Height);
                return bmp;
            }
        }

        static Bitmap FromFlate(Doc doc, PdfStream s, int w, int h, int comps)
        {
            var raw = Inflate(s.Data);
            if (raw == null) return null;
            var parms = Deref(doc, s.Dict.Get("DecodeParms")) as PdfDict;
            if (parms != null)
            {
                long pred = PdfLexer.Long(Deref(doc, parms.Get("Predictor")));
                if (pred >= 10) raw = Unpng(raw, w * comps, comps);
                else if (pred > 1) return null;
            }
            if (raw == null || raw.Length < (long)w * h * comps) return null;
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[w * 3];
                for (int y = 0; y < h; y++)
                {
                    int src = y * w * comps;
                    for (int x = 0; x < w; x++)
                    {
                        if (comps == 3) { row[x * 3] = raw[src + x * 3 + 2]; row[x * 3 + 1] = raw[src + x * 3 + 1]; row[x * 3 + 2] = raw[src + x * 3]; }
                        else { byte v = raw[src + x]; row[x * 3] = v; row[x * 3 + 1] = v; row[x * 3 + 2] = v; }
                    }
                    Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row.Length);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        static Bitmap Resize(Bitmap src, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                using (var wrap = new ImageAttributes())
                {
                    wrap.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, wrap);
                }
            }
            return b;
        }

        public static byte[] Jpeg(Bitmap bmp, long quality)
        {
            ImageCodecInfo codec = null;
            foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") codec = c;
            using (var ms = new MemoryStream())
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bmp.Save(ms, codec, ep);
                return ms.ToArray();
            }
        }

        // Zlib

        public static byte[] Inflate(byte[] data)
        {
            if (data.Length < 2) return null;
            // Zlib header; a few files skip it
            int skip = (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0 ? 2 : 0;
            var outp = new MemoryStream(data.Length * 3);
            try
            {
                using (var z = new DeflateStream(new MemoryStream(data, skip, data.Length - skip), CompressionMode.Decompress))
                {
                    var buf = new byte[65536];
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0) outp.Write(buf, 0, n);
                }
            }
            catch (InvalidDataException)
            {
                // Damaged tail: keep what came out
                if (outp.Length == 0) return null;
            }
            return outp.ToArray();
        }

        public static byte[] Deflate(byte[] data)
        {
            using (var outp = new MemoryStream(data.Length / 2 + 64))
            {
                outp.WriteByte(0x78);
                outp.WriteByte(0xDA);
                using (var z = new DeflateStream(outp, CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
                uint a = 1, bsum = 0;
                foreach (byte x in data) { a = (a + x) % 65521; bsum = (bsum + a) % 65521; }
                uint adler = (bsum << 16) | a;
                outp.WriteByte((byte)(adler >> 24));
                outp.WriteByte((byte)(adler >> 16));
                outp.WriteByte((byte)(adler >> 8));
                outp.WriteByte((byte)adler);
                return outp.ToArray();
            }
        }

        // PNG row filters back to plain rows
        public static byte[] Unpng(byte[] d, int rowLen, int bpp)
        {
            int rows = d.Length / (rowLen + 1);
            var outp = new byte[rows * rowLen];
            var prev = new byte[rowLen];
            for (int r = 0; r < rows; r++)
            {
                int src = r * (rowLen + 1), dst = r * rowLen;
                byte f = d[src];
                for (int i = 0; i < rowLen; i++)
                {
                    int x = d[src + 1 + i];
                    int left = i >= bpp ? outp[dst + i - bpp] : 0, up = prev[i], ul = i >= bpp ? prev[i - bpp] : 0;
                    switch (f)
                    {
                        case 1: x += left; break;
                        case 2: x += up; break;
                        case 3: x += (left + up) / 2; break;
                        case 4:
                            int pa = Math.Abs(up - ul), pb = Math.Abs(left - ul), pc = Math.Abs(left + up - 2 * ul);
                            x += pa <= pb && pa <= pc ? left : pb <= pc ? up : ul;
                            break;
                    }
                    outp[dst + i] = (byte)x;
                }
                Buffer.BlockCopy(outp, dst, prev, 0, rowLen);
            }
            return outp;
        }

        // Writer: object streams and an xref stream

        static void Write(string path, Doc doc, object root, object info, object id, string version)
        {
            if (string.CompareOrdinal(version, "1.5") < 0) version = "1.5";
            int count = doc.Objs.Count;
            // Plain objects packed 100 to a stream
            var loose = new List<int>();
            for (int n = 1; n <= count; n++) if (!(doc.Objs[n - 1] is PdfStream) && doc.Objs[n - 1] != null) loose.Add(n);
            var where = new Dictionary<int, int[]>();
            var stms = new List<PdfStream>();
            for (int at = 0; at < loose.Count; at += 100)
            {
                int stmNum = count + stms.Count + 1;
                var head = new StringBuilder();
                var body = new MemoryStream();
                int take = Math.Min(100, loose.Count - at);
                for (int k = 0; k < take; k++)
                {
                    int n = loose[at + k];
                    head.Append(n).Append(' ').Append(body.Length).Append(' ');
                    Ser(doc.Objs[n - 1], body);
                    body.WriteByte((byte)'\n');
                    where[n] = new[] { stmNum, k };
                }
                var headBytes = Encoding.ASCII.GetBytes(head.ToString() + "\n");
                var all = new byte[headBytes.Length + body.Length];
                Buffer.BlockCopy(headBytes, 0, all, 0, headBytes.Length);
                Buffer.BlockCopy(body.ToArray(), 0, all, headBytes.Length, (int)body.Length);
                var dict = new PdfDict();
                dict.Set("Type", new PdfName("ObjStm"));
                dict.Set("N", new PdfRaw(take.ToString(CultureInfo.InvariantCulture)));
                dict.Set("First", new PdfRaw(headBytes.Length.ToString(CultureInfo.InvariantCulture)));
                dict.Set("Filter", new PdfName("FlateDecode"));
                stms.Add(new PdfStream { Dict = dict, Data = Deflate(all) });
            }
            int xrefNum = count + stms.Count + 1;
            var offsets = new long[xrefNum + 1];
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                Put(fs, "%PDF-" + version + "\n");
                fs.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);
                for (int n = 1; n <= count; n++)
                {
                    var s = doc.Objs[n - 1] as PdfStream;
                    if (s == null) continue;
                    offsets[n] = fs.Position;
                    WriteObj(fs, n, s);
                }
                for (int i = 0; i < stms.Count; i++)
                {
                    offsets[count + i + 1] = fs.Position;
                    WriteObj(fs, count + i + 1, stms[i]);
                }
                // Xref stream: 1 type byte, 4 offset, 2 index
                offsets[xrefNum] = fs.Position;
                var table = new byte[(xrefNum + 1) * 7];
                for (int n = 0; n <= xrefNum; n++)
                {
                    int p = n * 7;
                    int[] packedAt;
                    if (n == 0) { table[p] = 0; Big(table, p + 1, 0, 4); Big(table, p + 5, 65535, 2); }
                    else if (where.TryGetValue(n, out packedAt)) { table[p] = 2; Big(table, p + 1, packedAt[0], 4); Big(table, p + 5, packedAt[1], 2); }
                    else if (offsets[n] > 0) { table[p] = 1; Big(table, p + 1, offsets[n], 4); Big(table, p + 5, 0, 2); }
                    else { table[p] = 0; Big(table, p + 1, 0, 4); Big(table, p + 5, 0, 2); }
                }
                var x = new PdfDict();
                x.Set("Type", new PdfName("XRef"));
                x.Set("Size", new PdfRaw((xrefNum + 1).ToString(CultureInfo.InvariantCulture)));
                x.Set("W", new List<object> { new PdfRaw("1"), new PdfRaw("4"), new PdfRaw("2") });
                x.Set("Root", root);
                if (info != null) x.Set("Info", info);
                if (id is List<object>) x.Set("ID", id);
                x.Set("Filter", new PdfName("FlateDecode"));
                WriteObj(fs, xrefNum, new PdfStream { Dict = x, Data = Deflate(table) });
                Put(fs, "startxref\n" + offsets[xrefNum].ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
            }
        }

        static void Big(byte[] t, int at, long v, int width)
        {
            for (int i = width - 1; i >= 0; i--) { t[at + i] = (byte)(v & 0xFF); v >>= 8; }
        }

        static void WriteObj(Stream o, int num, PdfStream s)
        {
            Put(o, num.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            s.Dict.Set("Length", new PdfRaw(s.Data.Length.ToString(CultureInfo.InvariantCulture)));
            Ser(s.Dict, o);
            Put(o, "\nstream\n");
            o.Write(s.Data, 0, s.Data.Length);
            Put(o, "\nendstream\nendobj\n");
        }

        static void Put(Stream o, string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            o.Write(bytes, 0, bytes.Length);
        }

        static void Ser(object v, Stream o)
        {
            if (v == null) { Put(o, "null"); return; }
            var n = v as PdfName;
            if (n != null) { var nb = PdfLexer.Latin1.GetBytes("/" + n.Name); o.Write(nb, 0, nb.Length); return; }
            var r = v as PdfRef;
            if (r != null) { Put(o, r.Num.ToString(CultureInfo.InvariantCulture) + " " + r.Gen.ToString(CultureInfo.InvariantCulture) + " R"); return; }
            var raw = v as PdfRaw;
            if (raw != null) { o.Write(raw.Bytes, 0, raw.Bytes.Length); return; }
            var d = v as PdfDict;
            if (d != null)
            {
                Put(o, "<<");
                foreach (var kv in d.Items)
                {
                    var kb = PdfLexer.Latin1.GetBytes("/" + kv.Key + " ");
                    o.Write(kb, 0, kb.Length);
                    Ser(kv.Value, o);
                    Put(o, " ");
                }
                Put(o, ">>");
                return;
            }
            var l = v as List<object>;
            if (l != null)
            {
                Put(o, "[");
                for (int i = 0; i < l.Count; i++)
                {
                    if (i > 0) Put(o, " ");
                    Ser(l[i], o);
                }
                Put(o, "]");
                return;
            }
            var w = v as PdfWord;
            if (w != null) { var wb = PdfLexer.Latin1.GetBytes(w.Text); o.Write(wb, 0, wb.Length); return; }
            // A stream nested in a value cannot be written inline
            Put(o, "null");
        }
    }
}
