using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using NPoco.DatabaseTypes;
using NPoco.FluentSql;
using NPoco.Tests.Common;
using NUnit.Framework;

namespace NPoco.Tests.DecoratedTests.CRUDTests
{
    /// <summary>
    /// An enum property on a varchar column declared AnsiString must reach SQL Server as the enum
    /// name in a varchar parameter. Sent as an int, SQL Server converts the column side and fails
    /// with "Conversion failed when converting the varchar value 'Html' to data type int", and an
    /// insert silently stores the number.
    /// </summary>
    [TestFixture]
    public class EnumAnsiStringTests : BaseDBDecoratedTest
    {
        [OneTimeSetUp]
        public void UseSqlServer2016DatabaseType()
        {
            if (!(TestDatabase is SqlServerDatabase || TestDatabase is SQLLocalDatabase))
                Assert.Ignore("Needs a SQL Server test database (TestDBType 2 or 3).");

            Database = new Database(TestDatabase.Connection, new SqlServer2016DatabaseType(), IsolationLevel.ReadUncommitted);
        }

        [SetUp]
        public void CreateTable()
        {
            Database.Execute(@"
                CREATE TABLE EnumAnsiStringObjects(
                    Name varchar(50) NOT NULL,
                    Type varchar(10) NOT NULL,
                    NullableType varchar(10) NULL,
                    Contents nvarchar(50) NULL,
                    Priority int NOT NULL,
                    PRIMARY KEY (Name, Type)
                )");
            Database.Execute("INSERT INTO EnumAnsiStringObjects (Name, Type, NullableType, Contents, Priority) VALUES ('A', 'Html', NULL, 'html', 1), ('A', 'Text', 'Json', 'text', 2)");
        }

        [Test]
        public void InsertWritesEnumName()
        {
            Database.Insert(new EnumAnsiStringObject { Name = "B", Type = AnsiEnum.Json, NullableType = AnsiEnum.Text, Contents = "json" });

            var stored = Database.Single<(string, string)>("SELECT Type, NullableType FROM EnumAnsiStringObjects WHERE Name = 'B'");
            Assert.AreEqual(("Json", "Text"), stored);
        }

        [Test]
        public void InsertWritesNullForNullableEnum()
        {
            Database.Insert(new EnumAnsiStringObject { Name = "B", Type = AnsiEnum.Json, NullableType = null });

            var stored = Database.Single<string>("SELECT NullableType FROM EnumAnsiStringObjects WHERE Name = 'B'");
            Assert.IsNull(stored);
        }

        [Test]
        public void SingleOrDefaultByIdWithPocoAsCompositeKey()
        {
            var poco = Database.SingleOrDefaultById<EnumAnsiStringObject>(new EnumAnsiStringObject { Name = "A", Type = AnsiEnum.Text });

            Assert.IsNotNull(poco);
            Assert.AreEqual(AnsiEnum.Text, poco.Type);
            Assert.AreEqual(AnsiEnum.Json, poco.NullableType);
            Assert.AreEqual("text", poco.Contents);
        }

        [Test]
        public void SingleOrDefaultByIdWithStringInAnonymousKey()
        {
            var poco = Database.SingleOrDefaultById<EnumAnsiStringObject>(new { Name = "A", Type = "Html" });

            Assert.IsNotNull(poco);
            Assert.AreEqual("html", poco.Contents);
        }

        [Test]
        public void UpdateByCompositeKey()
        {
            Database.Update(new EnumAnsiStringObject { Name = "A", Type = AnsiEnum.Html, NullableType = AnsiEnum.Text, Contents = "changed" });

            var stored = Database.Single<(string, string)>("SELECT NullableType, Contents FROM EnumAnsiStringObjects WHERE Name = 'A' AND Type = 'Html'");
            Assert.AreEqual(("Text", "changed"), stored);
            Assert.AreEqual("text", Database.Single<string>("SELECT Contents FROM EnumAnsiStringObjects WHERE Name = 'A' AND Type = 'Text'"));
        }

        [Test]
        public void DeleteByCompositeKey()
        {
            Database.Delete(new EnumAnsiStringObject { Name = "A", Type = AnsiEnum.Html });

            var remaining = Database.Fetch<string>("SELECT Type FROM EnumAnsiStringObjects");
            CollectionAssert.AreEquivalent(new[] { "Text" }, remaining);
        }

        [Test]
        public void QueryWhereEnumEqualsConstant()
        {
            var pocos = Database.Query<EnumAnsiStringObject>().Where(x => x.Type == AnsiEnum.Text).ToList();

            Assert.AreEqual(1, pocos.Count);
            Assert.AreEqual("text", pocos[0].Contents);
        }

        [Test]
        public void QueryWhereEnumEqualsCastIntVariable()
        {
            var requestedType = 0;
            var pocos = Database.Query<EnumAnsiStringObject>().Where(x => x.Type == (AnsiEnum)requestedType).ToList();

            Assert.AreEqual(1, pocos.Count);
            Assert.AreEqual("html", pocos[0].Contents);
        }

        [Test]
        public void QueryWhereConstantEqualsEnum()
        {
            var pocos = Database.Query<EnumAnsiStringObject>().Where(x => AnsiEnum.Html == x.Type).ToList();

            Assert.AreEqual(1, pocos.Count);
            Assert.AreEqual("html", pocos[0].Contents);
        }

        [Test]
        public void QueryWhereNullableEnumEqualsConstant()
        {
            var pocos = Database.Query<EnumAnsiStringObject>().Where(x => x.NullableType == AnsiEnum.Json).ToList();

            Assert.AreEqual(1, pocos.Count);
            Assert.AreEqual("text", pocos[0].Contents);
        }

        [Test]
        public void QueryWhereEnumIn()
        {
            var types = new List<AnsiEnum> { AnsiEnum.Html, AnsiEnum.Json };
            var pocos = Database.Query<EnumAnsiStringObject>().Where(x => types.Contains(x.Type)).ToList();

            Assert.AreEqual(1, pocos.Count);
            Assert.AreEqual("html", pocos[0].Contents);
        }

        [Test]
        public void QueryWhereEnumInOpenJson()
        {
            var types = Enumerable.Range(0, 32).Select(i => i % 2 == 0 ? AnsiEnum.Html : AnsiEnum.Json).ToList();
            var interceptor = new ParameterCapturingInterceptor();
            Database.Interceptors.Add(interceptor);
            List<EnumAnsiStringObject> pocos;
            try
            {
                pocos = Database.Query<EnumAnsiStringObject>().Where(x => types.Contains(x.Type)).ToList();
            }
            finally
            {
                Database.Interceptors.Remove(interceptor);
            }

            Assert.That(interceptor.CommandTexts.Single(), Does.Contain("OPENJSON"));
            Assert.AreEqual(1, pocos.Count);
            Assert.AreEqual("html", pocos[0].Contents);
        }

        [Test]
        public void FluentQueryWhereEnumEqualsConstant()
        {
            var rows = Database.FluentQuery().From<EnumAnsiStringObject>(out var o)
                .Where(() => o.Row.Type == AnsiEnum.Text)
                .Select(o)
                .Fetch();

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("text", rows[0].Contents);
        }

        [Test]
        public void EnumParametersAreSentAsAnsiString()
        {
            var types = new List<AnsiEnum> { AnsiEnum.Text };
            var interceptor = new ParameterCapturingInterceptor();
            Database.Interceptors.Add(interceptor);
            try
            {
                Database.SingleOrDefaultById<EnumAnsiStringObject>(new EnumAnsiStringObject { Name = "A", Type = AnsiEnum.Text });
                Database.Query<EnumAnsiStringObject>().Where(x => x.Type == AnsiEnum.Text).ToList();
                Database.Query<EnumAnsiStringObject>().Where(x => types.Contains(x.Type)).ToList();
                Database.FluentQuery().From<EnumAnsiStringObject>(out var o).Where(() => o.Row.Type == AnsiEnum.Text).Select(o).Fetch();
                Database.Insert(new EnumAnsiStringObject { Name = "C", Type = AnsiEnum.Text });
            }
            finally
            {
                Database.Interceptors.Remove(interceptor);
            }

            // The IN path pads the list to a power-of-two parameter count, so it contributes more than one.
            var enumParameters = interceptor.Parameters.Where(p => Equals(p.Value, "Text")).ToList();
            Assert.That(enumParameters.Count, Is.GreaterThanOrEqualTo(5));
            Assert.That(enumParameters.Select(p => p.DbType), Is.All.EqualTo(DbType.AnsiString));
        }

        // An enum on an int column is untouched by the AnsiString handling and still goes out as a number.

        [Test]
        public void IntEnumInsertWritesNumber()
        {
            Database.Insert(new EnumAnsiStringObject { Name = "B", Type = AnsiEnum.Json, Priority = IntEnum.High });

            Assert.AreEqual(2, Database.Single<int>("SELECT Priority FROM EnumAnsiStringObjects WHERE Name = 'B'"));
        }

        [Test]
        public void IntEnumUpdateWritesNumber()
        {
            Database.Update(new EnumAnsiStringObject { Name = "A", Type = AnsiEnum.Html, Priority = IntEnum.High });

            Assert.AreEqual(2, Database.Single<int>("SELECT Priority FROM EnumAnsiStringObjects WHERE Name = 'A' AND Type = 'Html'"));
        }

        [Test]
        public void IntEnumIsReadBack()
        {
            var poco = Database.SingleOrDefaultById<EnumAnsiStringObject>(new EnumAnsiStringObject { Name = "A", Type = AnsiEnum.Text });

            Assert.AreEqual(IntEnum.High, poco.Priority);
        }

        [Test]
        public void IntEnumQueryWhereEquals()
        {
            var pocos = Database.Query<EnumAnsiStringObject>().Where(x => x.Priority == IntEnum.High).ToList();

            Assert.AreEqual(1, pocos.Count);
            Assert.AreEqual("text", pocos[0].Contents);
        }

        [Test]
        public void IntEnumQueryWhereIn()
        {
            var priorities = new List<IntEnum> { IntEnum.Low };
            var pocos = Database.Query<EnumAnsiStringObject>().Where(x => priorities.Contains(x.Priority)).ToList();

            Assert.AreEqual(1, pocos.Count);
            Assert.AreEqual("html", pocos[0].Contents);
        }

        [Test]
        public void IntEnumQueryWhereInOpenJson()
        {
            var priorities = Enumerable.Repeat(IntEnum.Low, 32).ToList();
            var interceptor = new ParameterCapturingInterceptor();
            Database.Interceptors.Add(interceptor);
            List<EnumAnsiStringObject> pocos;
            try
            {
                pocos = Database.Query<EnumAnsiStringObject>().Where(x => priorities.Contains(x.Priority)).ToList();
            }
            finally
            {
                Database.Interceptors.Remove(interceptor);
            }

            Assert.That(interceptor.CommandTexts.Single(), Does.Contain("OPENJSON"));
            Assert.AreEqual(1, pocos.Count);
            Assert.AreEqual("html", pocos[0].Contents);
        }

        [Test]
        public void IntEnumFluentQueryWhereEquals()
        {
            var rows = Database.FluentQuery().From<EnumAnsiStringObject>(out var o)
                .Where(() => o.Row.Priority == IntEnum.High)
                .Select(o)
                .Fetch();

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("text", rows[0].Contents);
        }

        [Test]
        public void IntEnumParametersAreSentAsInt()
        {
            var priorities = new List<IntEnum> { IntEnum.High };
            var interceptor = new ParameterCapturingInterceptor();
            Database.Interceptors.Add(interceptor);
            try
            {
                Database.Query<EnumAnsiStringObject>().Where(x => x.Priority == IntEnum.High).ToList();
                Database.Query<EnumAnsiStringObject>().Where(x => priorities.Contains(x.Priority)).ToList();
                Database.FluentQuery().From<EnumAnsiStringObject>(out var o).Where(() => o.Row.Priority == IntEnum.High).Select(o).Fetch();
                Database.Insert(new EnumAnsiStringObject { Name = "C", Type = AnsiEnum.Text, Priority = IntEnum.High });
            }
            finally
            {
                Database.Interceptors.Remove(interceptor);
            }

            var priorityParameters = interceptor.Parameters.Where(p => Equals(p.Value, 2) || Equals(p.Value, IntEnum.High)).ToList();
            Assert.That(priorityParameters.Count, Is.GreaterThanOrEqualTo(4));
            Assert.That(priorityParameters.Select(p => p.DbType), Is.All.EqualTo(DbType.Int32));
        }

        private class ParameterCapturingInterceptor : IExecutingInterceptor
        {
            public List<(object Value, DbType DbType)> Parameters { get; } = new List<(object, DbType)>();
            public List<string> CommandTexts { get; } = new List<string>();

            public void OnExecutingCommand(IDatabase database, DbCommand cmd)
            {
                CommandTexts.Add(cmd.CommandText);
                Parameters.AddRange(cmd.Parameters.Cast<DbParameter>().Select(p => (p.Value, p.DbType)));
            }

            public void OnExecutedCommand(IDatabase database, DbCommand cmd)
            {
            }
        }

        [TableName("EnumAnsiStringObjects")]
        [PrimaryKey("Name,Type", AutoIncrement = false)]
        public class EnumAnsiStringObject
        {
            [ColumnType(typeof(AnsiString))]
            public string Name { get; set; }

            [ColumnType(typeof(AnsiString))]
            public AnsiEnum Type { get; set; }

            [ColumnType(typeof(AnsiString))]
            public AnsiEnum? NullableType { get; set; }

            public string Contents { get; set; }

            public IntEnum Priority { get; set; } = IntEnum.Low;
        }

        public enum IntEnum
        {
            Low = 1,
            High = 2
        }

        public enum AnsiEnum
        {
            Html,
            Text,
            Json
        }
    }
}
