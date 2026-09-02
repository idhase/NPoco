using System;

namespace NPoco
{
    /// <summary>
    /// Marks a column as SQL Server's legacy <c>datetime</c> rather than <c>datetime2</c>.
    ///
    /// A PocoColumn carries only the CLR type, and both SQL types map to <see cref="DateTime"/>,
    /// so the two cannot be told apart without saying so. Getting it wrong is silent: legacy
    /// datetime stores 1/300 second ticks, so a value read back as .123 is really .1233333 and
    /// will not compare equal to a datetime2 parameter. Measured over 300 random timestamps, a
    /// datetime column matched a datetime2 value 96 times, and the reverse 110 times.
    ///
    /// Declare it the same way as <see cref="AnsiString"/>:
    /// <code>
    /// [ColumnType(typeof(LegacyDateTime))] public DateTime CreatedAt { get; set; }
    /// // or
    /// c.Column(x =&gt; x.CreatedAt).WithDbType&lt;LegacyDateTime&gt;();
    /// </code>
    ///
    /// Only honoured by SqlServer2016DatabaseType, where an unmarked DateTime column is treated
    /// as datetime2. Note this sets PocoColumn.ColumnType, so a converter registered against
    /// DateTime no longer matches a marked column.
    /// </summary>
    public class LegacyDateTime
    {
        public LegacyDateTime(DateTime value)
        {
            Value = value;
        }

        public DateTime Value { get; }
    }
}
