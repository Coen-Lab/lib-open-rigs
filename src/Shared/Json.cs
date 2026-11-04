using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenRigs.Shared
{
    /// <summary>An ordered JSON object: keys in the order they were added, as the spec lists them.</summary>
    class JsonObject : List<KeyValuePair<string, object>>
    {
        public void Add(string key, object value) { Add(new KeyValuePair<string, object>(key, value)); }

        /// <summary>Adds, or replaces a key already there ignoring case, where the later value wins, as Bonsai
        /// takes a repeated --property. rig-sync's PowerShell refuses a file with keys differing only in case.</summary>
        public void Set(string key, object value)
        {
            var i = FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (i < 0) Add(key, value);
            else this[i] = new KeyValuePair<string, object>(key, value);
        }
    }

    /// <summary>Enough JSON to write the manifest, pretty-printed, with no dependency beyond the framework.</summary>
    static class Json
    {
        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value, int depth)
        {
            if (value == null) { sb.Append("null"); return; }
            var obj = value as JsonObject;
            if (obj != null)
            {
                if (obj.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                for (var i = 0; i < obj.Count; i++)
                {
                    Indent(sb, depth + 1);
                    WriteString(sb, obj[i].Key);
                    sb.Append(": ");
                    Write(sb, obj[i].Value, depth + 1);
                    sb.Append(i < obj.Count - 1 ? ",\n" : "\n");
                }
                Indent(sb, depth);
                sb.Append('}');
                return;
            }
            var list = value as List<object>;
            if (list != null)
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (var i = 0; i < list.Count; i++)
                {
                    Indent(sb, depth + 1);
                    Write(sb, list[i], depth + 1);
                    sb.Append(i < list.Count - 1 ? ",\n" : "\n");
                }
                Indent(sb, depth);
                sb.Append(']');
                return;
            }
            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is long || value is int) { sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double) { sb.Append(((double)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            throw new ArgumentException("Json cannot write a " + value.GetType().Name);
        }

        static void Indent(StringBuilder sb, int depth)
        {
            for (var i = 0; i < depth; i++) sb.Append("  ");
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>A file written whole or not at all, as every file here that another program reads.</summary>
    static class AtomicFile
    {
        /// <summary>&lt;path&gt;.tmp, then the rename, so no reader sees half a file, nor a moment with none. A write that fails takes its .tmp with it.</summary>
        public static void Write(string path, string text)
        {
            var tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                // Windows refuses the rename while a reader holds the file without delete sharing
                // (Python's open(); rig-control polls manifest.json every second): wait it out, about 0.5 s.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(tmp, path, null);
                        else File.Move(tmp, path);
                        break;
                    }
                    catch (IOException) when (attempt < 10)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                }
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception) { }
                throw;
            }
        }
    }
}
