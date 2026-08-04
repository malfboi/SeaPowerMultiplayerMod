using System.Globalization;
using System.Text;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Just enough JSON to write the eleven fixed line shapes in the analytics
    /// payload.
    ///
    /// Deliberately not Newtonsoft: that assembly lives in the game's Managed
    /// folder, and on the Anchor Chain path an unresolvable reference throws
    /// ReflectionTypeLoadException during GetExportedTypes() and the whole mod is
    /// silently skipped. A payload with no dynamic shapes does not justify that
    /// risk.
    /// </summary>
    internal sealed class Json
    {
        private readonly StringBuilder _sb = new(512);
        private bool _needComma;

        internal Json Obj()   { _sb.Append('{'); _needComma = false; return this; }
        internal Json End()   { _sb.Append('}'); _needComma = true;  return this; }

        internal Json Key(string k)
        {
            if (_needComma) _sb.Append(',');
            _sb.Append('"').Append(k).Append("\":");
            _needComma = false;
            return this;
        }

        internal Json Raw(string k, string rawJson) { Key(k); _sb.Append(rawJson); _needComma = true; return this; }
        internal Json Str(string k, string? v)      { Key(k); Escape(v); _needComma = true; return this; }
        internal Json Num(string k, double v)       { Key(k); Number(v);  _needComma = true; return this; }
        internal Json Num(string k, long v)         { Key(k); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); _needComma = true; return this; }
        internal Json Bool(string k, bool v)        { Key(k); _sb.Append(v ? "true" : "false"); _needComma = true; return this; }
        internal Json Null(string k)                { Key(k); _sb.Append("null"); _needComma = true; return this; }

        /// <summary>Opens a nested object under <paramref name="k"/>.</summary>
        internal Json Sub(string k) { Key(k); _sb.Append('{'); _needComma = false; return this; }

        private void Number(double v)
        {
            // NaN/Infinity are not valid JSON and do occur here (an EMA before its
            // first sample, a divide by a zero window). Null is the honest answer.
            if (double.IsNaN(v) || double.IsInfinity(v)) { _sb.Append("null"); return; }
            _sb.Append(v.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private void Escape(string? s)
        {
            if (s == null) { _sb.Append("null"); return; }
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n");  break;
                    case '\r': _sb.Append("\\r");  break;
                    case '\t': _sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }

        public override string ToString() => _sb.ToString();
    }
}
