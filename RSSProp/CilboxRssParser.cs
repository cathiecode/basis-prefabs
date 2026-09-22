using System;
using System.Text;

namespace SuperNekoya.RSSProp
{
    [Cilboxable]
    public struct RssItem
    {
        public string Title;
        public string Link;

        public RssItem(string title, string link)
        {
            Title = title;
            Link = link;
        }
    }

    /// <summary>Small dependency-free RSS 1.0/2.0 and Atom parser for interpreted code.</summary>
    [Cilboxable]
    public static class RssParser
    {
        public static RssItem[] Parse(string xml, int max = 100)
        {
            if (xml == null || xml.Length == 0) return new RssItem[0];
            RssItem[] results = new RssItem[8];
            int resultCount = 0, depth = 0, itemDepth = -1, fieldDepth = -1, field = 0;
            string title = "", link = "";
            StringBuilder value = new StringBuilder();
            int position = 0;

            while (position < xml.Length)
            {
                int tagStart = xml.IndexOf('<', position);
                if (tagStart < 0) { AppendText(xml, position, xml.Length, field, value); break; }
                AppendText(xml, position, tagStart, field, value);

                if (StartsAt(xml, tagStart, "<!--"))
                {
                    int end = xml.IndexOf("-->", tagStart + 4);
                    position = end < 0 ? xml.Length : end + 3;
                    continue;
                }
                if (StartsAt(xml, tagStart, "<![CDATA["))
                {
                    int end = xml.IndexOf("]]>", tagStart + 9);
                    int contentEnd = end < 0 ? xml.Length : end;
                    if (field != 0 && contentEnd > tagStart + 9)
                        value.Append(xml.Substring(tagStart + 9, contentEnd - tagStart - 9));
                    position = end < 0 ? xml.Length : end + 3;
                    continue;
                }

                int tagEnd = FindTagEnd(xml, tagStart + 1);
                if (tagEnd < 0 || tagStart + 1 >= xml.Length) break;
                char first = xml[tagStart + 1];
                if (first == '?' || first == '!') { position = tagEnd + 1; continue; }

                bool closing = first == '/';
                bool selfClosing = IsSelfClosing(xml, tagStart + 1, tagEnd);
                int nameStart = tagStart + (closing ? 2 : 1), nameEnd = nameStart;
                while (nameEnd < tagEnd && !IsNameEnd(xml[nameEnd])) nameEnd++;
                string name = LocalName(xml, nameStart, nameEnd);

                if (closing)
                {
                    if (field != 0 && depth == fieldDepth &&
                        ((field == 1 && EqualsAscii(name, "title")) || (field == 2 && EqualsAscii(name, "link"))))
                    {
                        string decoded = DecodeEntities(value.ToString()).Trim();
                        if (field == 1) title = decoded; else link = decoded;
                        value.Length = 0; field = 0; fieldDepth = -1;
                    }
                    if (itemDepth == depth && (EqualsAscii(name, "item") || EqualsAscii(name, "entry")))
                    {
                        if (title.Length > 0 && link.Length > 0)
                        {
                            if (resultCount == results.Length) results = Grow(results);
                            results[resultCount++] = new RssItem(title, link);
                            if (resultCount >= max) break; // breaks outermost while loop
                        }
                        itemDepth = -1; field = 0; fieldDepth = -1; value.Length = 0;
                    }
                    if (depth > 0) depth--;
                }
                else
                {
                    depth++;
                    if (itemDepth < 0 && (EqualsAscii(name, "item") || EqualsAscii(name, "entry")))
                    { itemDepth = depth; title = ""; link = ""; }
                    else if (itemDepth >= 0 && depth == itemDepth + 1 && EqualsAscii(name, "title"))
                    { field = 1; fieldDepth = depth; value.Length = 0; }
                    else if (itemDepth >= 0 && depth == itemDepth + 1 && EqualsAscii(name, "link"))
                    {
                        string href = Attribute(xml, nameEnd, tagEnd, "href");
                        string rel = Attribute(xml, nameEnd, tagEnd, "rel");
                        if (href.Length > 0 && link.Length == 0 && (rel.Length == 0 || EqualsAscii(rel, "alternate")))
                            link = DecodeEntities(href).Trim();
                        field = 2; fieldDepth = depth; value.Length = 0;
                    }
                    if (selfClosing)
                    {
                        if (fieldDepth == depth) { field = 0; fieldDepth = -1; value.Length = 0; }
                        depth--;
                    }
                }
                position = tagEnd + 1;
            }

            RssItem[] exact = new RssItem[resultCount];
            for (int i = 0; i < resultCount; i++) exact[i] = results[i];
            return exact;
        }

        private static void AppendText(string xml, int start, int end, int field, StringBuilder value)
        { if (field != 0 && end > start) value.Append(xml.Substring(start, end - start)); }

        private static RssItem[] Grow(RssItem[] old)
        {
            RssItem[] next = new RssItem[old.Length * 2];
            for (int i = 0; i < old.Length; i++) next[i] = old[i];
            return next;
        }

        private static int FindTagEnd(string text, int start)
        {
            char quote = '\0';
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c == '\'' || c == '"') quote = c;
                else if (c == '>') return i;
            }
            return -1;
        }

        private static bool IsSelfClosing(string text, int start, int end)
        {
            for (int i = end - 1; i >= start; i--) { char c = text[i]; if (!IsSpace(c)) return c == '/'; }
            return false;
        }

        private static bool IsNameEnd(char c) { return IsSpace(c) || c == '/' || c == '>'; }
        private static bool IsSpace(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

        private static string LocalName(string text, int start, int end)
        {
            int local = start;
            for (int i = start; i < end; i++) if (text[i] == ':') local = i + 1;
            return end > local ? text.Substring(local, end - local) : "";
        }

        private static string Attribute(string text, int start, int end, string wanted)
        {
            int p = start;
            while (p < end)
            {
                while (p < end && IsSpace(text[p])) p++;
                if (p >= end || text[p] == '/') break;
                int keyStart = p;
                while (p < end && !IsSpace(text[p]) && text[p] != '=' && text[p] != '/') p++;
                int keyEnd = p;
                while (p < end && IsSpace(text[p])) p++;
                if (p >= end || text[p] != '=') { while (p < end && !IsSpace(text[p])) p++; continue; }
                p++;
                while (p < end && IsSpace(text[p])) p++;
                char quote = p < end ? text[p] : '\0';
                int valueStart, valueEnd;
                if (quote == '\'' || quote == '"')
                {
                    p++; valueStart = p;
                    while (p < end && text[p] != quote) p++;
                    valueEnd = p; if (p < end) p++;
                }
                else
                {
                    valueStart = p;
                    while (p < end && !IsSpace(text[p]) && text[p] != '/') p++;
                    valueEnd = p;
                }
                if (EqualsAscii(LocalName(text, keyStart, keyEnd), wanted))
                    return text.Substring(valueStart, valueEnd - valueStart);
            }
            return "";
        }

        private static bool StartsAt(string text, int position, string value)
        {
            if (position + value.Length > text.Length) return false;
            for (int i = 0; i < value.Length; i++) if (text[position + i] != value[i]) return false;
            return true;
        }

        private static bool EqualsAscii(string a, string b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                char ca = a[i], cb = b[i];
                if (ca >= 'A' && ca <= 'Z') ca = (char)(ca + 32);
                if (cb >= 'A' && cb <= 'Z') cb = (char)(cb + 32);
                if (ca != cb) return false;
            }
            return true;
        }

        private static string DecodeEntities(string text)
        {
            if (text.IndexOf('&') < 0) return text;
            StringBuilder output = new StringBuilder();
            int p = 0;
            while (p < text.Length)
            {
                if (text[p] != '&') { output.Append(text[p++]); continue; }
                int semi = text.IndexOf(';', p + 1);
                if (semi < 0 || semi - p > 12) { output.Append('&'); p++; continue; }
                string entity = text.Substring(p + 1, semi - p - 1);
                if (entity == "amp") output.Append('&');
                else if (entity == "lt") output.Append('<');
                else if (entity == "gt") output.Append('>');
                else if (entity == "quot") output.Append('"');
                else if (entity == "apos") output.Append('\'');
                else
                {
                    int number = ParseEntityNumber(entity);
                    if (number >= 0 && number <= 65535) output.Append((char)number);
                    else output.Append(text.Substring(p, semi - p + 1));
                }
                p = semi + 1;
            }
            return output.ToString();
        }

        private static int ParseEntityNumber(string entity)
        {
            if (entity.Length < 2 || entity[0] != '#') return -1;
            int radix = 10, p = 1;
            if (p < entity.Length && (entity[p] == 'x' || entity[p] == 'X')) { radix = 16; p++; }
            if (p >= entity.Length) return -1;
            int value = 0;
            for (; p < entity.Length; p++)
            {
                char c = entity[p]; int digit;
                if (c >= '0' && c <= '9') digit = c - '0';
                else if (radix == 16 && c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                else if (radix == 16 && c >= 'A' && c <= 'F') digit = c - 'A' + 10;
                else return -1;
                value = value * radix + digit;
                if (value > 65535) return -1;
            }
            return value;
        }
    }
}
