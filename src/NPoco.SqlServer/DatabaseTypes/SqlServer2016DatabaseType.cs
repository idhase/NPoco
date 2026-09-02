using System;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using NPoco.Expressions;
using NPoco.SqlServer;

namespace NPoco.DatabaseTypes
{
    public class SqlServer2016DatabaseType : SqlServer2012DatabaseType
    {
        /// <summary>
        /// When true (default), IN clauses with 32 or more values are rewritten to use OPENJSON,
        /// passing the whole list as one JSON parameter instead of one parameter per value.
        /// Applies to int, long, short, byte, string, Guid, DateTime and enums over any of the
        /// integer types, plus columns marked AnsiString or LegacyDateTime. Every other type
        /// falls back to the parameterised IN path.
        /// Requires database compatibility level 130 or higher (SQL Server 2016+).
        /// Set to false if the target database has a lower compatibility level.
        /// This controls the OPENJSON rewrite only. Padding smaller lists to a power-of-two
        /// parameter count still happens either way.
        /// </summary>
        public bool UseOpenJsonForInClauses { get; }

        /// <summary>
        /// Creates the type with the OPENJSON rewrite for large IN clauses enabled.
        /// </summary>
        /// <remarks>
        /// Requires the target database to be at compatibility level 130 or higher. On a lower
        /// level, use <see cref="SqlServer2016DatabaseType(bool)"/> with false instead.
        /// </remarks>
        public SqlServer2016DatabaseType() : this(true) { }

        /// <summary>
        /// Creates the type, choosing whether large IN clauses are rewritten to use OPENJSON.
        /// </summary>
        /// <param name="useOpenJsonForInClauses">
        /// False turns the OPENJSON rewrite off, leaving every IN clause on the parameterised
        /// path. Pass false when the target database is below compatibility level 130, where
        /// OPENJSON does not exist. See <see cref="UseOpenJsonForInClauses"/>.
        /// </param>
        /// <remarks>
        /// The instance is not read from anywhere global, so it only takes effect once it is
        /// handed to a <see cref="Database"/> constructor. Mapping DateTime to
        /// <see cref="DbType.DateTime2"/> applies either way, since it is not part of the rewrite.
        /// </remarks>
        public SqlServer2016DatabaseType(bool useOpenJsonForInClauses)
        {
            UseOpenJsonForInClauses = useOpenJsonForInClauses;

            // A DateTime column is datetime2 unless it says otherwise with LegacyDateTime.
            // NPoco's default of DbType.DateTime would silently round datetime2 values to 1/300s.
            AddTypeMap(typeof(DateTime), DbType.DateTime2);
        }

        /// <inheritdoc />
        public override void SetParameterValue(DbParameter p, object value)
        {
            if (value is LegacyDateTime legacy)
            {
                p.Value = legacy.Value;
                p.DbType = DbType.DateTime;
                return;
            }

            base.SetParameterValue(p, value);
        }

        public new static Database Create(string connectionString)
        {
            return new Database(connectionString, new SqlServer2016DatabaseType(), SqlClientFactory.Instance);
        }

        public override ISqlExpression<T> ExpressionVisitor<T>(IDatabase db, PocoData pocoData, bool prefixTableName)
        {
            return new SqlServer2016Expression<T>(db, pocoData, prefixTableName, UseOpenJsonForInClauses);
        }
    }
}
