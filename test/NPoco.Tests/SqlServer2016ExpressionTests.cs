using System;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using Microsoft.Data.SqlClient;
using NPoco.DatabaseTypes;
using NPoco.Expressions;
using NPoco.SqlServer;
using NUnit.Framework;

namespace NPoco.Tests
{
    [TestFixture]
    public class SqlServer2016ExpressionTests
    {
        private const int Threshold = SqlServer2016Expression<object>.OpenJsonThreshold;

        private TestDb _db;
        private PocoData _pocoData;

        [SetUp]
        public void SetUp()
        {
            _db = new TestDb();
            _pocoData = _db.PocoDataFactory.ForType(typeof(TestModel));
        }

        private SqlServer2016Expression<TestModel> Expr(bool useOpenJson = true)
            => new SqlServer2016Expression<TestModel>(_db, _pocoData, false, useOpenJson);

        private static List<int> Ints(int count) => Enumerable.Range(1, count).ToList();

        // ── threshold behaviour ──────────────────────────────────────────────

        [Test]
        public void EmptyList_Returns1Equals0()
        {
            var e = Expr();
            e.Where(x => new List<int>().Contains(x.Id));
            Assert.That(e.Context.ToWhereStatement(), Is.EqualTo("WHERE 1 = 0"));
        }

        [Test]
        public void JustBelowThreshold_UsesStandardIn_NotOpenJson()
        {
            var ids = Ints(Threshold - 1);
            var e = Expr();
            e.Where(x => ids.Contains(x.Id));
            var sql = e.Context.ToWhereStatement();
            Assert.That(sql, Does.Not.Contain("OPENJSON"));
            Assert.That(sql, Does.Contain("IN ("));
        }

        [Test]
        public void AtThreshold_UsesOpenJson()
        {
            var ids = Ints(Threshold);
            var e = Expr();
            e.Where(x => ids.Contains(x.Id));
            var sql = e.Context.ToWhereStatement();
            Assert.That(sql, Does.Contain("OPENJSON"));
            Assert.That(sql, Does.Contain("[value] int '$'"));
            // Entire JSON array is passed as one parameter
            Assert.That(e.Context.Params, Has.Length.EqualTo(1));
            Assert.That(e.Context.Params[0], Is.EqualTo("[" + string.Join(",", Ints(Threshold)) + "]"));
        }

        // ── consistent behaviour for array (Enumerable.Contains) vs List.Contains ──

        [Test]
        public void ArrayContains_AtThreshold_UsesOpenJson()
        {
            // int[] desugars to static Enumerable.Contains; must still hit the OPENJSON path
            var ids = Ints(Threshold).ToArray();
            var e = Expr();
            e.Where(x => ids.Contains(x.Id));
            var sql = e.Context.ToWhereStatement();
            Assert.That(sql, Does.Contain("OPENJSON"));
            Assert.That(sql, Does.Contain("[value] int '$'"));
        }

        [Test]
        public void ArrayContains_BelowThreshold_UsesStandardIn()
        {
            var ids = new[] { 1, 2, 3 };
            var e = Expr();
            e.Where(x => ids.Contains(x.Id));
            var sql = e.Context.ToWhereStatement();
            Assert.That(sql, Does.Not.Contain("OPENJSON"));
            Assert.That(sql, Does.Contain("IN ("));
        }

        // ── S.In helper ──────────────────────────────────────────────────────

        [Test]
        public void SIn_AtThreshold_UsesOpenJson()
        {
            var ids = Ints(Threshold);
            var e = Expr();
            e.Where(x => S.In(x.Id, ids));
            Assert.That(e.Context.ToWhereStatement(), Does.Contain("OPENJSON"));
        }

        // ── supported types ──────────────────────────────────────────────────

        [Test]
        public void NullableIntColumn_UsesOpenJson()
        {
            // PocoColumn.ColumnType strips Nullable<>, so int? maps to "int"
            var ids = Ints(Threshold);
            var e = Expr();
            e.Where(x => ids.Contains(x.NullableId.Value));
            var sql = e.Context.ToWhereStatement();
            Assert.That(sql, Does.Contain("OPENJSON"));
            Assert.That(sql, Does.Contain("[value] int '$'"));
        }

        [Test]
        public void LongColumn_UsesOpenJson()
        {
            var values = Ints(Threshold).Select(x => (long)x).ToList();
            var e = Expr();
            e.Where(x => values.Contains(x.BigId));
            Assert.That(e.Context.ToWhereStatement(), Does.Contain("[value] bigint '$'"));
        }

        [Test]
        public void ShortColumn_UsesOpenJson()
        {
            var values = Ints(Threshold).Select(x => (short)x).ToList();
            var e = Expr();
            e.Where(x => values.Contains(x.SmallId));
            Assert.That(e.Context.ToWhereStatement(), Does.Contain("[value] smallint '$'"));
        }

        [Test]
        public void ByteColumn_UsesOpenJson()
        {
            var values = Ints(Threshold).Select(x => (byte)x).ToList();
            var e = Expr();
            e.Where(x => values.Contains(x.TinyId));
            Assert.That(e.Context.ToWhereStatement(), Does.Contain("[value] tinyint '$'"));
        }

        [Test]
        public void EnumColumn_UsesOpenJson_AsUnderlyingInt()
        {
            var values = Ints(Threshold).Select(x => (Status)(x % 6)).ToList();
            var e = Expr();
            e.Where(x => values.Contains(x.MyStatus));
            var sql = e.Context.ToWhereStatement();
            Assert.That(sql, Does.Contain("OPENJSON"));
            Assert.That(sql, Does.Contain("[value] int '$'"));
        }

        [Test]
        public void StringColumn_UsesOpenJson()
        {
            var names = Ints(Threshold).Select(x => "n" + x).ToList();
            var e = Expr();
            e.Where(x => names.Contains(x.Name));
            var sql = e.Context.ToWhereStatement();
            Assert.That(sql, Does.Contain("OPENJSON"));
            Assert.That(sql, Does.Contain("[value] nvarchar(max) '$'"));
        }

        [Test]
        public void StringColumn_EscapesJsonSpecialCharacters()
        {
            var names = Ints(Threshold - 1).Select(x => "n" + x).Concat(new[] { "a\"b\\c" }).ToList();
            var e = Expr();
            e.Where(x => names.Contains(x.Name));
            Assert.That(e.Context.ToWhereStatement(), Does.Contain("OPENJSON"));
            Assert.That((string)e.Context.Params[0], Does.Contain("\"a\\\"b\\\\c\""));
        }

        [Test]
        public void GuidColumn_UsesOpenJson_AsDashedString()
        {
            var ids = Ints(Threshold).Select(x => new Guid(x, 0, 0, new byte[8])).ToList();
            var e = Expr();
            e.Where(x => ids.Contains(x.GuidId));
            var sql = e.Context.ToWhereStatement();
            Assert.That(sql, Does.Contain("[value] uniqueidentifier '$'"));
            Assert.That(e.Context.Params, Has.Length.EqualTo(1));
            // UseFastGuid=false keeps the dashed form; base64 would not convert
            Assert.That((string)e.Context.Params[0], Does.Contain(ids[0].ToString()));
        }

        // ── excluded types must fall back to the parameterised path ──────────
        // These round-trip incorrectly through fastJSON, so they are deliberately
        // absent from _sqlServerTypeMap. See the comment on that field.

        [Test]
        public void UnmarkedDateTimeColumn_UsesDatetime2WithFullTicks()
        {
            var dates = Ints(Threshold).Select(x => new DateTime(2024, 1, 1).AddSeconds(x).AddTicks(1234567)).ToList();
            var e = Expr();
            e.Where(x => dates.Contains(x.CreatedAt));

            Assert.That(e.Context.ToWhereStatement(), Does.Contain("[value] datetime2(7) '$'"));
            Assert.That((string)e.Context.Params[0], Does.Contain("2024-01-01T00:00:01.1234567"));
        }

        [Test]
        public void LegacyDateTimeColumn_UsesDatetimeWithThreeDigits()
        {
            // More than 3 fractional digits against a datetime column is Msg 241.
            var dates = Ints(Threshold).Select(x => new DateTime(2024, 1, 1).AddSeconds(x).AddTicks(1234567)).ToList();
            var e = Expr();
            e.Where(x => dates.Contains(x.LegacyCreatedAt));

            var sql = e.Context.ToWhereStatement();
            Assert.That(sql, Does.Contain("[value] datetime '$'"));
            Assert.That(sql, Does.Not.Contain("datetime2"));
            Assert.That((string)e.Context.Params[0], Does.Contain("2024-01-01T00:00:01.123"));
            Assert.That((string)e.Context.Params[0], Does.Not.Contain("1234567"));
        }

        [Test]
        public void LegacyDateTimeColumn_EqualityWrapsTheParameter()
        {
            // VisitBinary never routes through FormatParameters, so == needs its own wrap.
            var when = new DateTime(2024, 1, 1, 10, 30, 15, 123);
            var e = Expr();
            e.Where(x => x.LegacyCreatedAt == when);

            Assert.That(e.Context.Params[0], Is.InstanceOf<LegacyDateTime>());
            Assert.That(((LegacyDateTime)e.Context.Params[0]).Value, Is.EqualTo(when));
        }

        [Test]
        public void UnmarkedDateTimeColumn_EqualityStaysUnwrapped()
        {
            var when = new DateTime(2024, 1, 1, 10, 30, 15, 123);
            var e = Expr();
            e.Where(x => x.CreatedAt == when);

            Assert.That(e.Context.Params[0], Is.InstanceOf<DateTime>());
        }

        [Test]
        public void LegacyDateTimeColumn_BelowThreshold_WrapsInListParameters()
        {
            var dates = new List<DateTime> { new(2024, 1, 1), new(2024, 2, 1), new(2024, 3, 1) };
            var e = Expr();
            e.Where(x => dates.Contains(x.LegacyCreatedAt));

            Assert.That(e.Context.ToWhereStatement(), Does.Not.Contain("OPENJSON"));
            Assert.That(e.Context.Params, Is.All.InstanceOf<LegacyDateTime>());
        }

        [Test]
        public void SetParameterValue_PicksTheDbTypeMatchingTheColumn()
        {
            var dbType = new SqlServer2016DatabaseType();
            var when = new DateTime(2024, 1, 1, 10, 30, 15, 123);

            var legacy = new SqlParameter();
            dbType.SetParameterValue(legacy, new LegacyDateTime(when));
            Assert.That(legacy.DbType, Is.EqualTo(DbType.DateTime));
            Assert.That(legacy.Value, Is.EqualTo(when));

            var modern = new SqlParameter();
            dbType.SetParameterValue(modern, when);
            Assert.That(modern.DbType, Is.EqualTo(DbType.DateTime2));
        }

        [Test]
        public void DateTimeOffsetColumn_NeverUsesOpenJson()
        {
            var dates = Ints(Threshold)
                .Select(x => new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5)).AddSeconds(x))
                .ToList();
            var e = Expr();
            e.Where(x => dates.Contains(x.UpdatedAt));
            Assert.That(e.Context.ToWhereStatement(), Does.Not.Contain("OPENJSON"));
        }

        [Test]
        public void DecimalColumn_NeverUsesOpenJson()
        {
            var values = Ints(Threshold).Select(x => (decimal)x).ToList();
            var e = Expr();
            e.Where(x => values.Contains(x.Amount));
            Assert.That(e.Context.ToWhereStatement(), Does.Not.Contain("OPENJSON"));
        }

        [Test]
        public void DoubleColumn_NeverUsesOpenJson()
        {
            var values = Ints(Threshold).Select(x => (double)x).ToList();
            var e = Expr();
            e.Where(x => values.Contains(x.Ratio));
            Assert.That(e.Context.ToWhereStatement(), Does.Not.Contain("OPENJSON"));
        }

        [Test]
        public void BoolColumn_NeverUsesOpenJson()
        {
            var values = Ints(Threshold).Select(x => x % 2 == 0).ToList();
            var e = Expr();
            e.Where(x => values.Contains(x.IsActive));
            Assert.That(e.Context.ToWhereStatement(), Does.Not.Contain("OPENJSON"));
        }

        // ── UseOpenJsonForInClauses = false: OPENJSON off, padding still on ──

        [Test]
        public void OpenJsonDisabled_AtThreshold_UsesStandardIn()
        {
            var ids = Ints(Threshold);
            var e = Expr(useOpenJson: false);
            var sql = e.Where(x => ids.Contains(x.Id)).Context.ToWhereStatement();
            Assert.That(sql, Does.Not.Contain("OPENJSON"));
            Assert.That(sql, Does.Contain("IN ("));
        }

        [Test]
        public void OpenJsonDisabled_SmallList_StillPadsParamsViaPowerOfTwo()
        {
            // 3 items → RepeatFirstItem pads to next power-of-2 ≥ 5 → 8
            var ids = new List<int> { 1, 2, 3 };
            var e = Expr(useOpenJson: false);
            e.Where(x => ids.Contains(x.Id));
            Assert.That(e.Context.Params, Has.Length.EqualTo(8));
        }

        [Test]
        public void OpenJsonEnabled_SmallList_StillPadsParamsViaPowerOfTwo()
        {
            var ids = new List<int> { 1, 2, 3 };
            var e = Expr(useOpenJson: true);
            e.Where(x => ids.Contains(x.Id));
            Assert.That(e.Context.ToWhereStatement(), Does.Not.Contain("OPENJSON"));
            Assert.That(e.Context.Params, Has.Length.EqualTo(8));
        }

        [Test]
        public void AnsiStringColumn_UsesVarcharNotNvarchar()
        {
            // nvarchar against a varchar column makes the column side convert and costs the
            // index seek; varchar keeps it.
            var codes = Ints(Threshold).Select(x => "c" + x).ToList();
            var e = Expr();
            e.Where(x => codes.Contains(x.AnsiCode));
            var sql = e.Context.ToWhereStatement();
            Assert.That(sql, Does.Contain("OPENJSON"));
            Assert.That(sql, Does.Contain("[value] varchar(max) '$'"));
            Assert.That(sql, Does.Not.Contain("nvarchar"));
        }

        [Test]
        public void AnsiStringColumn_BelowThreshold_SendsAnsiStringParameters()
        {
            // Base NPoco wraps AnsiString only in VisitBinary, so IN lists used to send
            // nvarchar to a varchar column and lose the index seek.
            var codes = new List<string> { "a", "b", "c" };
            var e = Expr();
            e.Where(x => codes.Contains(x.AnsiCode));

            Assert.That(e.Context.ToWhereStatement(), Does.Not.Contain("OPENJSON"));
            Assert.That(e.Context.Params, Is.All.InstanceOf<AnsiString>());
            Assert.That(e.Context.Params.Cast<AnsiString>().Select(p => p.Value),
                Is.SupersetOf(codes));
        }

        [Test]
        public void NonAnsiStringColumn_BelowThreshold_SendsPlainStrings()
        {
            var names = new List<string> { "a", "b", "c" };
            var e = Expr();
            e.Where(x => names.Contains(x.Name));

            Assert.That(e.Context.Params, Is.All.InstanceOf<string>());
        }

        [Test]
        public void InListJson_WritesScalarsInOpenJsonCompatibleForms()
        {
            Assert.That(InListJson.Serialize(new object[] { 1, 2L, (short)3, (byte)4 }), Is.EqualTo("[1,2,3,4]"));
            Assert.That(InListJson.Serialize(new object[] { Guid.Empty }),
                Is.EqualTo("[\"00000000-0000-0000-0000-000000000000\"]"));
            Assert.That(InListJson.Serialize(new object[] { new AnsiString("a") }), Is.EqualTo("[\"a\"]"));
            Assert.That(InListJson.Serialize(new object[] { true, false }), Is.EqualTo("[true,false]"));
            Assert.That(InListJson.Serialize(Array.Empty<object>()), Is.EqualTo("[]"));
        }

        [Test]
        public void InListJson_EscapesControlCharactersAndQuotes()
        {
            Assert.That(InListJson.Serialize(new object[] { "a\"b\\c" }), Is.EqualTo("[\"a\\\"b\\\\c\"]"));
            Assert.That(InListJson.Serialize(new object[] { "l1\nl2\tx" }), Is.EqualTo("[\"l1\\nl2\\tx\"]"));
            Assert.That(InListJson.Serialize(new object[] { "\u0001" }), Is.EqualTo("[\"\\u0001\"]"));
            // non-ASCII stays literal: the parameter is nvarchar, so no escaping is needed
            Assert.That(InListJson.Serialize(new object[] { "\u00e5" }), Is.EqualTo("[\"\u00e5\"]"));
        }

        [Test]
        public void InListJson_RejectsATypeTheMapShouldHaveExcluded()
        {
            Assert.Throws<NotSupportedException>(() => InListJson.Serialize(new object[] { TimeSpan.FromHours(1) }));
        }

        [Test]
        public void DatabaseType_ResolvesToSqlServerDialect()
        {
            // Upstream resolves the dialect off the DatabaseType. If a future refactor stops
            // this type inheriting SqlServerDatabaseType.SqlDialect it silently falls back to
            // AnsiSqlDialect and starts emitting different SQL for Upper/Lower/Trim/Substring.
            Assert.That(SqlDialects.For(new SqlServer2016DatabaseType()),
                Is.SameAs(SqlServerSqlDialect.Instance));
        }

        [Test]
        public void DatabaseType_ParameterlessCtor_EnablesOpenJson()
        {
            Assert.That(new SqlServer2016DatabaseType().UseOpenJsonForInClauses, Is.True);
        }

        [Test]
        public void DatabaseType_ConstructedDisabled_FlowsThroughToExpression()
        {
            var dbType = new SqlServer2016DatabaseType(false);
            using var db = new Database("test", dbType, SqlClientFactory.Instance);
            var e = (SqlServer2016Expression<TestModel>)dbType.ExpressionVisitor<TestModel>(
                db, db.PocoDataFactory.ForType(typeof(TestModel)), false);

            var ids = Ints(Threshold);
            e.Where(x => ids.Contains(x.Id));

            Assert.That(e.Context.ToWhereStatement(), Does.Not.Contain("OPENJSON"));
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private enum Status { A, B, C, D, E, F }

        [TableName("TestModels")]
        [PrimaryKey("Id")]
        private class TestModel
        {
            public int Id { get; set; }
            public int? NullableId { get; set; }
            public long BigId { get; set; }
            public short SmallId { get; set; }
            public byte TinyId { get; set; }
            public Status MyStatus { get; set; }
            public string Name { get; set; }

            [ColumnType(typeof(AnsiString))]
            public string AnsiCode { get; set; }
            public Guid GuidId { get; set; }
            public DateTime CreatedAt { get; set; }

            [ColumnType(typeof(LegacyDateTime))]
            public DateTime LegacyCreatedAt { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
            public decimal Amount { get; set; }
            public double Ratio { get; set; }
            public bool IsActive { get; set; }
        }

        private class TestDb : Database
        {
            public TestDb() : base("test", new SqlServer2016DatabaseType(), SqlClientFactory.Instance) { }
        }
    }
}
