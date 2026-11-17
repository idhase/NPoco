using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using NPoco.Tests.Common;
using NUnit.Framework;

namespace NPoco.Tests.DecoratedTests
{
    /// <summary>
    /// SQL Server rolls a transaction back by itself after a deadlock, an error under XACT_ABORT or an error in a
    /// trigger. SqlClient then clears the transaction's Connection, and a command given that transaction runs with no
    /// transaction at all, so it commits on its own while the rest of the work is gone.
    /// </summary>
    [TestFixture]
    public class EndedTransactionTests : BaseDBDecoratedTest
    {
        const string DoomWithXactAbort = "SET XACT_ABORT ON; SELECT 1/0";
        const string DoomWithRollback = "ROLLBACK TRANSACTION";

        string _connectionString;

        [OneTimeSetUp]
        public void RequireSqlServer()
        {
            if (!(TestDatabase is SqlServerDatabase || TestDatabase is SQLLocalDatabase))
                Assert.Ignore("Needs a SQL Server test database (TestDBType 2 or 3).");

            _connectionString = TestDatabase.ConnectionString;
        }

        [SetUp]
        public void CreateTable()
        {
            ExecuteOnOwnConnection(@"
                IF OBJECT_ID('EndedTransactionRows') IS NOT NULL DROP TABLE EndedTransactionRows;
                CREATE TABLE EndedTransactionRows (Id int IDENTITY(1,1) PRIMARY KEY, Value nvarchar(50) NOT NULL)");
        }

        [TearDown]
        public void DropTable()
        {
            ExecuteOnOwnConnection("IF OBJECT_ID('EndedTransactionRows') IS NOT NULL DROP TABLE EndedTransactionRows");
        }

        [TestCase(DoomWithXactAbort)]
        [TestCase(DoomWithRollback)]
        public void ExecuteThrowsOnceTheServerHasRolledBackTheTransaction(string doomSql)
        {
            using var db = NewDatabase();
            db.BeginTransaction();
            db.Execute("INSERT INTO EndedTransactionRows (Value) VALUES ('before')");

            Doom(db, doomSql);

            Assert.Throws<InvalidOperationException>(() => db.Execute("INSERT INTO EndedTransactionRows (Value) VALUES ('after')"));
            Assert.AreEqual(0, CountRows());
        }

        [Test]
        public void ExecuteAsyncThrowsOnceTheServerHasRolledBackTheTransaction()
        {
            using var db = NewDatabase();
            db.BeginTransaction();

            Doom(db, DoomWithXactAbort);

            Assert.ThrowsAsync<InvalidOperationException>(() => db.ExecuteAsync("INSERT INTO EndedTransactionRows (Value) VALUES ('after')"));
            Assert.AreEqual(0, CountRows());
        }

        [Test]
        public void InsertThrowsOnceTheServerHasRolledBackTheTransaction()
        {
            using var db = NewDatabase();
            db.BeginTransaction();

            Doom(db, DoomWithXactAbort);

            Assert.Throws<InvalidOperationException>(() => db.Insert(new EndedTransactionRow { Value = "after" }));
            Assert.AreEqual(0, CountRows());
        }

        [Test]
        public void InsertBulkThrowsOnceTheServerHasRolledBackTheTransaction()
        {
            using var db = NewDatabase();
            db.BeginTransaction();

            Doom(db, DoomWithXactAbort);

            Assert.Throws<InvalidOperationException>(() => db.InsertBulk(new List<EndedTransactionRow> { new EndedTransactionRow { Value = "after" } }));
            Assert.AreEqual(0, CountRows());
        }

        [Test]
        [Description("A transaction its owner committed has no Connection either, so the database must be told it has no transaction any more")]
        public void ExecuteThrowsInATransactionItsOwnerCommitted()
        {
            using var connection = new SqlConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using var db = new Database(connection, DatabaseType.SqlServer2012);
            db.SetTransaction(transaction);
            db.Execute("INSERT INTO EndedTransactionRows (Value) VALUES ('in transaction')");

            transaction.Commit();

            Assert.Throws<InvalidOperationException>(() => db.Execute("INSERT INTO EndedTransactionRows (Value) VALUES ('after commit')"));

            db.SetTransaction(null!);
            db.Execute("INSERT INTO EndedTransactionRows (Value) VALUES ('without transaction')");

            Assert.AreEqual(2, CountRows());
        }

        [Test]
        public void QueryThrowsOnceTheServerHasRolledBackTheTransaction()
        {
            using var db = NewDatabase();
            db.BeginTransaction();

            Doom(db, DoomWithXactAbort);

            Assert.Throws<InvalidOperationException>(() => db.Fetch<int>("SELECT 1"));
        }

        [Test]
        [Description("The guard throws before the command runs, so it must not leave a one-time timeout armed for the next command")]
        public void TheOneTimeCommandTimeoutIsUsedUpByTheRefusedCommand()
        {
            using var db = NewDatabase();
            db.BeginTransaction();
            Doom(db, DoomWithXactAbort);

            db.OneTimeCommandTimeout = 1;
            Assert.Throws<InvalidOperationException>(() => db.Execute("INSERT INTO EndedTransactionRows (Value) VALUES ('after')"));

            Assert.AreEqual(0, db.OneTimeCommandTimeout);
        }

        [Test]
        [Description("The connection belongs to the caller, so completing a transaction its owner already committed must not close and reopen it")]
        public void CompleteTransactionOnATransactionItsOwnerCommittedLeavesTheConnectionAlone()
        {
            using var connection = new SqlConnection(_connectionString);
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE #SessionMarker (Id int)";
                command.ExecuteNonQuery();
            }

            using var transaction = connection.BeginTransaction();
            using var db = new Database(connection, DatabaseType.SqlServer2012);
            db.SetTransaction(transaction);
            transaction.Commit();

            Assert.Throws<InvalidOperationException>(() => db.CompleteTransaction());

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT OBJECT_ID('tempdb..#SessionMarker')";
                Assert.IsNotNull(command.ExecuteScalar() as int?, "Expected the caller's connection to be the same session, not closed and reopened");
            }
        }

        [TestCase(DoomWithXactAbort)]
        [TestCase(DoomWithRollback)]
        public void CompleteTransactionThrowsInsteadOfSkippingTheCommit(string doomSql)
        {
            var interceptor = new RecordingTransactionInterceptor();
            using var db = NewDatabase();
            db.Interceptors.Add(interceptor);
            db.BeginTransaction();
            db.Execute("INSERT INTO EndedTransactionRows (Value) VALUES ('before')");

            Doom(db, doomSql);

            Assert.Throws<InvalidOperationException>(() => db.CompleteTransaction());
            Assert.IsNull(db.Transaction);
            Assert.AreEqual(new[] { "begin", "abort" }, interceptor.Events);
            Assert.AreEqual(0, CountRows());
        }

        [Test]
        public void CompleteTransactionAsyncThrowsInsteadOfSkippingTheCommit()
        {
            var interceptor = new RecordingTransactionInterceptor();
            using var db = NewDatabase();
            db.Interceptors.Add(interceptor);
            db.BeginTransaction();

            Doom(db, DoomWithXactAbort);

            Assert.ThrowsAsync<InvalidOperationException>(() => db.CompleteTransactionAsync());
            Assert.IsNull(db.Transaction);
            Assert.AreEqual(new[] { "begin", "abort" }, interceptor.Events);
        }

        [Test]
        public void TheDatabaseWorksAgainAfterTheFailedComplete()
        {
            using var db = NewDatabase();
            db.BeginTransaction();
            Doom(db, DoomWithXactAbort);
            Assert.Throws<InvalidOperationException>(() => db.CompleteTransaction());

            db.BeginTransaction();
            db.Execute("INSERT INTO EndedTransactionRows (Value) VALUES ('next transaction')");
            db.CompleteTransaction();

            Assert.AreEqual(1, CountRows());
        }

        [Test]
        public void TransactionScopeSurfacesOnlyTheGuardsException()
        {
            using var db = NewDatabase();

            var transaction = db.GetTransaction();
            Doom(db, DoomWithXactAbort);

            Assert.Throws<InvalidOperationException>(() => transaction.Complete());
            Assert.DoesNotThrow(() => transaction.Dispose());
            Assert.IsNull(db.Transaction);
        }

        [Test]
        public void NestedTransactionThrowsWhenTheOutermostCompletes()
        {
            using var db = NewDatabase();
            db.BeginTransaction();
            db.BeginTransaction();

            Doom(db, DoomWithXactAbort);

            Assert.DoesNotThrow(() => db.CompleteTransaction());
            Assert.Throws<InvalidOperationException>(() => db.CompleteTransaction());
            Assert.IsNull(db.Transaction);
        }

        [Test]
        public void AbortTransactionStaysSilent()
        {
            using var db = NewDatabase();
            db.BeginTransaction();

            Doom(db, DoomWithXactAbort);

            Assert.DoesNotThrow(() => db.AbortTransaction());
            Assert.IsNull(db.Transaction);
        }

        Database NewDatabase() => new Database(_connectionString, DatabaseType.SqlServer2012, SqlClientFactory.Instance);

        static void Doom(Database db, string doomSql)
        {
            try
            {
                db.Execute(doomSql);
            }
            catch (SqlException)
            {
            }

            Assert.IsNotNull(db.Transaction, "Expected the database to still hold its transaction");
            Assert.IsNull(db.Transaction!.Connection, "Expected the server to have rolled back the transaction");
        }

        int CountRows()
        {
            using var connection = new SqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM EndedTransactionRows";
            return (int)command.ExecuteScalar();
        }

        void ExecuteOnOwnConnection(string sql)
        {
            using var connection = new SqlConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        [TableName("EndedTransactionRows")]
        [PrimaryKey("Id")]
        public class EndedTransactionRow
        {
            public int Id { get; set; }
            public string Value { get; set; }
        }

        class RecordingTransactionInterceptor : ITransactionInterceptor
        {
            public List<string> Events { get; } = new List<string>();

            public void OnBeginTransaction(IDatabase database) => Events.Add("begin");
            public void OnAbortTransaction(IDatabase database) => Events.Add("abort");
            public void OnCompleteTransaction(IDatabase database) => Events.Add("complete");
        }
    }
}
