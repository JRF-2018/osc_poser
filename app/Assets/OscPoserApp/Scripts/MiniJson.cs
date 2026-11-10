// 小さな JSON の読み取り(pose_muscles.json・emote_poses.json・poses/*.json 用)。
// オブジェクト → Dictionary<string, object>、配列 → List<object>、数 → double、文字列、bool、null。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public static class MiniJson
{
    public static object Parse(string s)
    {
        int i = 0;
        var v = Value(s, ref i);
        return v;
    }

    static void Ws(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    static object Value(string s, ref int i)
    {
        Ws(s, ref i);
        if (i >= s.Length) throw new FormatException("JSON が途中で終わっています");
        char c = s[i];
        if (c == '{')
        {
            var d = new Dictionary<string, object>();
            i++;
            Ws(s, ref i);
            if (s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                var key = Str(s, ref i);
                Ws(s, ref i);
                i++;   // ':'
                d[key] = Value(s, ref i);
                Ws(s, ref i);
                if (s[i++] == '}') return d;
            }
        }
        if (c == '[')
        {
            var l = new List<object>();
            i++;
            Ws(s, ref i);
            if (s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(Value(s, ref i));
                Ws(s, ref i);
                if (s[i++] == ']') return l;
            }
        }
        if (c == '"') return Str(s, ref i);
        if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
        if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
        if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
        int start = i;
        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    static string Str(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++;   // '"'
        while (s[i] != '"')
        {
            char c = s[i++];
            if (c != '\\') { sb.Append(c); continue; }
            c = s[i++];
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                default: sb.Append(c); break;
            }
        }
        i++;
        return sb.ToString();
    }
}
