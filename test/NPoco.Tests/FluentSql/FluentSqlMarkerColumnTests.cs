using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using NPoco.FluentSql;
using NUnit.Framework;

namespace NPoco.Tests.FluentSqlTests
{
    /// <summary>
    /// Marker column types (AnsiString, LegacyDateTime) set PocoColumn.ColumnType, which FluentSql
    /// uses for both parameter conversion and read-back. Both directions have to cope.
    /// </summary>
    [TestFixture]
    public class FluentSqlMarkerColumnTests
    {
        private string _file, _cs;

        [SetUp]
        public void SetUp()
        {
            _file = Path.Combine(Path.GetTempPath(), "npoco-marker-" + Guid.NewGuid().ToString("N") + ".db");
            _cs = "Data Source=" + _file;
            using var c = new SqliteConnection(_cs);
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = "create table marked(id integer primary key, code text, at text, plain text);" +
                              "insert into marked values(1,'abc','2024-01-01 10:30:15','2024-01-01 10:30:15');";
            cmd.ExecuteNonQuery();
        }

        [TearDown]
        public void TearDown() { try { File.Delete(_file); } catch { } }

        private Database Db() => new Database(_cs, DatabaseType.SQLite, SqliteFactory.Instance);

        [Test]
        public void AnsiStringColumn_CanBeReadBack()
        {
            using var db = Db();
            var rows = db.FluentQuery().From<Marked>(out var m).Select(m).Fetch();
            Assert.That(rows.Single().Code, Is.EqualTo("abc"));
        }

        [Test]
        public void LegacyDateTimeColumn_CanBeReadBack()
        {
            using var db = Db();
            var rows = db.FluentQuery().From<Marked>(out var m).Select(m).Fetch();
            Assert.That(rows.Single().At, Is.EqualTo(new DateTime(2024, 1, 1, 10, 30, 15)));
        }

        [Test]
        public void LegacyDateTimeColumn_ParameterIsWrapped()
        {
            using var db = Db();
            var when = new DateTime(2024, 1, 1, 10, 30, 15);
            var sql = db.FluentQuery().From<Marked>(out var m)
                        .Where(() => m.Row.At == when).Select(m).ToSql();
            Assert.That(sql.Arguments[0], Is.InstanceOf<LegacyDateTime>());
        }

        [Test]
        public void AnsiStringColumn_ScalarProjection()
        {
            using var db = Db();
            var v = db.FluentQuery().From<Marked>(out var m).Select(() => m.Row.Code).Fetch();
            Assert.That(v.Single(), Is.EqualTo("abc"));
        }

        [Test]
        public void LegacyDateTimeColumn_ScalarProjection()
        {
            using var db = Db();
            var v = db.FluentQuery().From<Marked>(out var m).Select(() => m.Row.At).Fetch();
            Assert.That(v.Single(), Is.EqualTo(new DateTime(2024, 1, 1, 10, 30, 15)));
        }

        [Test]
        public void MarkedColumns_AnonymousProjection()
        {
            using var db = Db();
            var v = db.FluentQuery().From<Marked>(out var m)
                      .Select(() => new { m.Row.Code, m.Row.At }).Fetch().Single();
            Assert.That(v.Code, Is.EqualTo("abc"));
            Assert.That(v.At, Is.EqualTo(new DateTime(2024, 1, 1, 10, 30, 15)));
        }

        [Test]
        public void ForceToUtc_AppliesToMarkedAndUnmarkedAlike()
        {
            // ForceToUtc's converter is gated on dstType == typeof(DateTime). FluentSql passes
            // PocoColumn.ColumnType as dstType, which is LegacyDateTime for a marked column, so
            // the two could disagree on Kind.
            using var db = Db();
            var row = db.FluentQuery().From<Marked>(out var m).Select(m).Fetch().Single();
            var scalar = db.FluentQuery().From<Marked>(out var m2).Select(() => m2.Row.At).Fetch().Single();

            // Compare marked against unmarked: if both agree, the marker is not the variable.
            TestContext.Out.WriteLine($"marked entity={row.At.Kind} unmarked entity={row.Plain.Kind} marked scalar={scalar.Kind}");
            Assert.That(row.At.Kind, Is.EqualTo(row.Plain.Kind), "marked vs unmarked must agree");
        }

        [TableName("marked")]
        public class Marked
        {
            [Column("id")] public int Id { get; set; }
            [Column("code"), ColumnType(typeof(AnsiString))] public string Code { get; set; }
            [Column("at"), ColumnType(typeof(LegacyDateTime))] public DateTime At { get; set; }
            [Column("plain")] public DateTime Plain { get; set; }
        }
    }
}
