using System.Collections;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using CodeGraph.Data.MariaDb;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Data;

public sealed class GroupedRepositoryIndexingLockTests
{
    [Fact]
    public async Task MariaDbGroup_UsesOneConnection_ExactSingleLockNames_AndExistingTimeouts()
    {
        var connection = new RecordingConnection(); var opens = 0;
        var lease = await MySqlGraphStore.AcquireIndexingLocksAsync(["z", "repo", "Repo", "z"],
            _ => { opens++; return Task.FromResult<DbConnection>(connection); }, NullLogger.Instance);
        opens.ShouldBe(1);
        var commands = connection.Commands.ToList();
        commands.Select(command => command.Name).ShouldBe(new[] { Hash("Repo"), Hash("repo"), Hash("z") });
        commands.ShouldAllBe(command => command.Sql == "SELECT GET_LOCK(@LockName, 600)" && command.Timeout == 630);
        await Task.WhenAll(lease.DisposeAsync().AsTask(), lease.DisposeAsync().AsTask());
        connection.Commands.Skip(3).Select(command => command.Name).ShouldBe(commands.Select(command => command.Name).Reverse());
        connection.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task MariaDbTimeout_ReleasesSuccessfulAndUncertainAttempts_AndDisposesConnection()
    {
        var connection = new RecordingConnection { Acquire = (_, attempt) => attempt == 2 ? 0L : 1L };
        var error = await Should.ThrowAsync<TimeoutException>(() => Acquire(connection, ["a", "b", "c"]));
        error.Message.ShouldContain("'b'");
        connection.Commands.Select(command => command.Name).ShouldBe(new[] { Hash("a"), Hash("b"), Hash("b"), Hash("a") });
        connection.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task MariaDbCancellation_ReleasesAllAttemptedNames_WithUncancelledCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new RecordingConnection
        {
            Acquire = (ct, attempt) => { if (attempt == 2) cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return 1L; }
        };
        await Should.ThrowAsync<OperationCanceledException>(() => Acquire(connection, ["a", "b", "c"], cancellation.Token));
        connection.Commands.Count(command => command.Sql.StartsWith("SELECT GET_LOCK")).ShouldBe(2);
        var releases = connection.Commands.Where(command => command.Sql.StartsWith("SELECT RELEASE_LOCK")).ToList();
        releases.Select(command => command.Name).ShouldBe(new[] { Hash("b"), Hash("a") });
        releases.ShouldAllBe(command => !command.Token.IsCancellationRequested);
        connection.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task MariaDbReleaseFailure_DoesNotSkipOtherLocksOrConnectionDisposal()
    {
        var connection = new RecordingConnection { FailFirstRelease = true };
        var lease = await Acquire(connection, ["a", "b"]);
        await lease.DisposeAsync();
        connection.Commands.Count(command => command.Sql.StartsWith("SELECT RELEASE_LOCK")).ShouldBe(2);
        connection.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task MariaDbEmptyOrPreCancelledGroup_DoesNotOpenConnection()
    {
        var opens = 0;
        Task<DbConnection> Open(CancellationToken _) { opens++; return Task.FromResult<DbConnection>(new RecordingConnection()); }
        await using var empty = await MySqlGraphStore.AcquireIndexingLocksAsync([], Open, NullLogger.Instance);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => MySqlGraphStore.AcquireIndexingLocksAsync(["a"], Open, NullLogger.Instance, cancellation.Token));
        opens.ShouldBe(0);
    }

    [Fact]
    public async Task InMemoryGroup_ExcludesSingleApi_AndCancellationReleasesPartialAcquisition()
    {
        var first = new InMemoryGraphStore(); var second = new InMemoryGraphStore();
        var prefix = Guid.NewGuid().ToString("N");
        var held = await first.AcquireRepositoryIndexingLockAsync(prefix + "b");
        using var cancellation = new CancellationTokenSource();
        var group = second.AcquireRepositoryIndexingLocksAsync([prefix + "b", prefix + "a", prefix + "A"], cancellation.Token);
        group.IsCompleted.ShouldBeFalse();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => group);
        await using var released = await first.AcquireRepositoryIndexingLockAsync(prefix + "a").WaitAsync(TimeSpan.FromSeconds(2));
        await held.DisposeAsync();
        await held.DisposeAsync();
        await using var next = await second.AcquireRepositoryIndexingLocksAsync([prefix + "b", prefix + "B"]).WaitAsync(TimeSpan.FromSeconds(2));
        using var blocked = new CancellationTokenSource();
        var waiting = first.AcquireRepositoryIndexingLockAsync(prefix + "b", blocked.Token);
        waiting.IsCompleted.ShouldBeFalse(); blocked.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
    }

    private static Task<IAsyncDisposable> Acquire(RecordingConnection connection, IReadOnlyCollection<string> names, CancellationToken ct = default) =>
        MySqlGraphStore.AcquireIndexingLocksAsync(names, _ => Task.FromResult<DbConnection>(connection), NullLogger.Instance, ct);
    private static string Hash(string name) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
    private sealed record Command(string Sql, string Name, int Timeout, CancellationToken Token);

    private sealed class RecordingConnection : DbConnection
    {
        public List<Command> Commands { get; } = [];
        public Func<CancellationToken, int, long?> Acquire { get; init; } = (_, _) => 1L;
        public bool FailFirstRelease { get; init; }
        public int DisposeCount;
        private int _acquisitions, _releases;
        public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "11.4";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new RecordingCommand(this);
        public override ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
        private sealed class RecordingCommand(RecordingConnection owner) : DbCommand
        {
            private readonly Parameters _parameters = new();
            public override string CommandText { get; set; } = "";
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; } = owner;
            protected override DbParameterCollection DbParameterCollection => _parameters;
            protected override DbTransaction? DbTransaction { get; set; }
            public override void Cancel() { }
            public override int ExecuteNonQuery() => throw new NotSupportedException();
            public override object? ExecuteScalar() => throw new NotSupportedException();
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => new Parameter();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
            public override Task<object?> ExecuteScalarAsync(CancellationToken ct)
            {
                Record(ct);
                return Task.FromResult<object?>(owner.Acquire(ct, ++owner._acquisitions));
            }
            public override Task<int> ExecuteNonQueryAsync(CancellationToken ct)
            {
                Record(ct);
                if (++owner._releases == 1 && owner.FailFirstRelease) throw new IOException("release failure");
                return Task.FromResult(1);
            }
            private void Record(CancellationToken ct) => owner.Commands.Add(new(CommandText, Convert.ToString(_parameters["LockName"].Value)!, CommandTimeout, ct));
        }
        private sealed class Parameter : DbParameter
        {
            public override DbType DbType { get; set; }
            public override ParameterDirection Direction { get; set; }
            public override bool IsNullable { get; set; }
            public override string ParameterName { get; set; } = "";
            public override string SourceColumn { get; set; } = "";
            public override object? Value { get; set; }
            public override bool SourceColumnNullMapping { get; set; }
            public override int Size { get; set; }
            public override void ResetDbType() { }
        }
        private sealed class Parameters : DbParameterCollection
        {
            private readonly List<DbParameter> _items = [];
            public override int Count => _items.Count;
            public override object SyncRoot => this;
            public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
            public override void AddRange(Array values) { foreach (var value in values) Add(value!); }
            public override void Clear() => _items.Clear();
            public override bool Contains(object value) => _items.Contains((DbParameter)value);
            public override bool Contains(string value) => IndexOf(value) >= 0;
            public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
            public override IEnumerator GetEnumerator() => _items.GetEnumerator();
            public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
            public override int IndexOf(string value) => _items.FindIndex(parameter => parameter.ParameterName == value);
            public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
            public override void Remove(object value) => _items.Remove((DbParameter)value);
            public override void RemoveAt(int index) => _items.RemoveAt(index);
            public override void RemoveAt(string name) => RemoveAt(IndexOf(name));
            protected override DbParameter GetParameter(int index) => _items[index];
            protected override DbParameter GetParameter(string name) => _items[IndexOf(name)];
            protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
            protected override void SetParameter(string name, DbParameter value) => _items[IndexOf(name)] = value;
        }
    }
}
