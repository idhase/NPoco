using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using NPoco.Expressions;
using NPoco.Internal;

namespace NPoco.SqlServer
{
    public class SqlServer2016Expression<T> : DefaultSqlExpression<T>
    {
        // Below this count the parameterised IN path is cheaper; above it, one JSON
        // parameter beats N parameters for plan reuse.
        public const int OpenJsonThreshold = 32;

        private readonly bool _useOpenJson;
        private readonly IDatabase _db;

        public SqlServer2016Expression(IDatabase database, PocoData pocoData, bool prefixTableName, bool useOpenJson)
            : base(database, pocoData, prefixTableName)
        {
            _useOpenJson = useOpenJson;
            _db = database;
        }

        protected override string BuildInStatement(Expression m, object quotedColName)
        {
            var member = Expression.Convert(m, typeof(object));
            var lambda = Expression.Lambda<Func<object>>(member);
            var getter = lambda.Compile();

            quotedColName ??= Visit(m);

            var inArgs = ((IEnumerable)getter()).Cast<object>().ToList();
            return BuildInStatementFromList(inArgs, quotedColName);
        }

        // Route static-array Contains (e.g. int[].Contains via Enumerable.Contains) through
        // BuildInStatementFromList so the OPENJSON optimisation applies consistently with List<T>.Contains.
        protected override object VisitStaticArrayMethodCall(MethodCallExpression m)
        {
            var args = VisitExpressionList(m.Arguments);

            // args[0] is the already-evaluated collection; args[1] is the column
            if (!(args[0] is PartialSqlString) && args[0] is IEnumerable enumerable)
            {
                var inArgs = enumerable.Cast<object>().ToList();
                return new PartialSqlString(BuildInStatementFromList(inArgs, args[1]));
            }

            // Collection did not evaluate to a value, so it is SQL. Same as the base, but
            // without delegating to it: base would re-run VisitExpressionList over the same
            // arguments, visiting them twice.
            return new PartialSqlString(BuildInStatement(m.Arguments[0], args[1]));
        }

        private string BuildInStatementFromList(List<object> inArgs, object quotedColName)
        {
            if (inArgs.Count == 0)
                return "1 = 0";

            var sIn = new StringBuilder();
            var columnType = GetColumnType(quotedColName);

            // Resolving the SQL type is what decides eligibility, so the emitted type can
            // never disagree with the check that allowed it.
            // Columns with a registered IMapper use custom to-DB conversion that JSON.ToJSON
            // doesn't know about, so fall back to the standard parameterised IN path for them.
            var isJsonSerialisable = TryResolveJsonColumnType(columnType, out var databaseString, out var enumUnderlyingType);

            var useOpenJson = _useOpenJson
                && isJsonSerialisable
                && inArgs.Count >= OpenJsonThreshold
                && !ColumnHasCustomMapper(quotedColName);

            if (useOpenJson)
            {
                // Apply built-in value mapping (e.g. string-stored enum → name string) and
                // filter nulls — null never matches a SQL IN predicate anyway.
                IEnumerable<object> toSerialize = inArgs
                    .Where(x => x != null)
                    .Select(x => FormatParameters(quotedColName, x));

                // Explicitly convert enum values to their underlying int for reliable JSON output.
                if (enumUnderlyingType != null)
                    toSerialize = toSerialize.Select(x => Convert.ChangeType(x, enumUnderlyingType));

                var paramPlaceholder = CreateParam(InListJson.Serialize(toSerialize.ToList()));
                sIn.Append($"SELECT [s0].[value] FROM OPENJSON({paramPlaceholder}) WITH ([value] {databaseString} '$') AS [s0]");
            }
            else
            {
                // Padding to a power-of-two parameter count is a plan-cache win in its own
                // right, so it applies whether or not the OPENJSON rewrite is enabled.
                inArgs = RepeatFirstItem(inArgs);
                sIn.Append(FlattenList(inArgs, quotedColName));
            }

            return $"{quotedColName} IN ({sIn})";
        }

        // Base NPoco wraps AnsiString values only in VisitBinary, so IN clauses send
        // nvarchar to varchar columns. That makes the column side convert and turns an
        // Index Seek into an Index Scan. Wrap here so the parameterised IN path matches
        // the column type, as the OPENJSON path already does via _sqlServerTypeMap.
        protected override object FormatParameters(object partialSqlString, object e)
        {
            var value = base.FormatParameters(partialSqlString, e);

            var columnType = GetColumnType(partialSqlString);

            if (value is string s && columnType == typeof(AnsiString))
                return new AnsiString(s);

            if (value is DateTime d && columnType == typeof(LegacyDateTime))
                return new LegacyDateTime(d);

            return value;
        }

        private bool ColumnHasCustomMapper(object quotedColName)
            => quotedColName is MemberAccessString mas && _db.TryGetMapper(mas.PocoColumn, out _);

        // Single source of truth: CLR type -> SQL Server OPENJSON column type.
        // Deliberately excluded:
        //   DateTimeOffset
        //       Untested against a real datetimeoffset column, not known to be impossible.
        //       By analogy with datetime2 a single datetimeoffset(7) WITH clause should serve
        //       every scale, but that was never measured, and InListJson has no case for it.
        //       Both would have to be added together.
        //   decimal
        //       decimal(38,18) in the WITH clause overflows above ~1e20 and truncates beyond
        //       18 decimal places. A SQL-side limit, not a serialisation one.
        //   double, float
        //       Equality on binary floating point after a decimal-string round-trip, and
        //       NaN/Infinity have no JSON number form.
        //   bool
        //       An IN list needs 32+ entries to qualify, so it cannot arise in practice.
        private static readonly Dictionary<Type, string> _sqlServerTypeMap = new()
        {
            { typeof(int),    "int" },
            { typeof(long),   "bigint" },
            { typeof(short),  "smallint" },
            { typeof(byte),   "tinyint" },
            { typeof(string), "nvarchar(max)" },
            // An AnsiString column is varchar. Emitting nvarchar here would make the column
            // side convert (nvarchar wins type precedence) and cost the index seek.
            { typeof(AnsiString), "varchar(max)" },
            { typeof(Guid),   "uniqueidentifier" },
            // An unmarked DateTime column is assumed to be datetime2; LegacyDateTime declares the
            // legacy datetime type, whose 1/300s ticks never compare equal to a datetime2 value.
            { typeof(DateTime),       "datetime2(7)" },
            { typeof(LegacyDateTime), "datetime" },
        };

        /// <summary>
        /// Resolves the OPENJSON column type for a POCO column type, or returns false if the
        /// type has no safe JSON representation. On true, <paramref name="sqlType"/> is non-null;
        /// <paramref name="enumUnderlyingType"/> is non-null only when the column is an enum.
        /// </summary>
        private static bool TryResolveJsonColumnType(Type type, out string? sqlType, out Type? enumUnderlyingType)
        {
            sqlType = null;
            enumUnderlyingType = null;

            if (type == null)
                return false;

            type = UnwrapNullable(type);
            if (type.IsEnum)
            {
                enumUnderlyingType = Enum.GetUnderlyingType(type);
                type = enumUnderlyingType;
            }

            return _sqlServerTypeMap.TryGetValue(type, out sqlType);
        }

        private static Type UnwrapNullable(Type type)
            => Nullable.GetUnderlyingType(type) ?? type;

        private static List<TS> RepeatFirstItem<TS>(List<TS> list)
        {
            var max = Math.Min(GetNextPowerOfTwo(Math.Max(5, list.Count)), 1024);
            if (list.Count >= max)
                return list;
            var repeated = new List<TS>(max);
            for (var i = 0; i < max; i++)
                repeated.Add(list[i % list.Count]);
            return repeated;
        }

        private static int GetNextPowerOfTwo(int input)
        {
            input--;
            input |= input >> 1;
            input |= input >> 2;
            input |= input >> 4;
            input |= input >> 8;
            input |= input >> 16;
            return input + 1;
        }
    }
}
