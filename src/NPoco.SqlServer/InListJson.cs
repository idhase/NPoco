using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NPoco.SqlServer
{
    /// <summary>
    /// Writes an IN list as a bare JSON array for the OPENJSON rewrite.
    ///
    /// Hand-written rather than delegated to a JSON library on purpose. The output has to be a
    /// flat array of scalars in the exact formats OPENJSON can convert, and a general purpose
    /// serializer is configured for round-tripping objects: type name handling wraps the array in
    /// an envelope, date settings change formats, and escaping choices differ. Any of those
    /// silently produce SQL that OPENJSON cannot parse. Only types in
    /// <see cref="SqlServer2016Expression{T}"/>'s type map ever reach here.
    /// </summary>
    public static class InListJson
    {
        public static string Serialize(IReadOnlyList<object> values)
        {
            var sb = new StringBuilder("[");

            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Write(sb, values[i]);
            }

            return sb.Append(']').ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case AnsiString a:
                    WriteString(sb, a.Value);
                    break;
                // Dashed form: OPENJSON WITH (uniqueidentifier) cannot convert base64.
                case Guid g:
                    WriteString(sb, g.ToString());
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                // Explicit formats, not "O": that appends a Kind suffix, and datetime2 has no
                // offset. Legacy datetime is capped at 3 fractional digits - more is Msg 241.
                case LegacyDateTime l:
                    WriteString(sb, l.Value.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
                    break;
                case DateTime d:
                    WriteString(sb, d.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                    break;
                case byte or short or int or long:
                    sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)
                                     .ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new NotSupportedException(
                        $"{value.GetType()} has no defined OPENJSON representation. Types are gated by " +
                        "SqlServer2016Expression's type map, so reaching this means the two disagree.");
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');

            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ')
                            sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(ch);
                        break;
                }
            }

            sb.Append('"');
        }
    }
}
