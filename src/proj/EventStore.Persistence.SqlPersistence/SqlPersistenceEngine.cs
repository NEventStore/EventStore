namespace EventStore.Persistence.SqlPersistence
{
	using System;
	using System.Collections.Generic;
	using System.Data;
	using System.Linq;
	using System.Threading;
	using System.Transactions;
	using Persistence;
	using Serialization;

	public class SqlPersistenceEngine : IPersistStreams
	{
		private readonly IConnectionFactory connectionFactory;
		private readonly ISqlDialect dialect;
		private readonly ISerialize serializer;
		private readonly TransactionScopeOption scopeOption;
		private int initialized;

		protected virtual IConnectionFactory ConnectionFactory
		{
			get { return this.connectionFactory; }
		}
		protected virtual ISqlDialect Dialect
		{
			get { return this.dialect; }
		}
		protected virtual ISerialize Serializer
		{
			get { return this.serializer; }
		}

		public SqlPersistenceEngine(
			IConnectionFactory connectionFactory,
			ISqlDialect dialect,
			ISerialize serializer,
			TransactionScopeOption scopeOption)
		{
			if (connectionFactory == null)
				throw new ArgumentNullException("connectionFactory");

			if (dialect == null)
				throw new ArgumentNullException("dialect");

			if (serializer == null)
				throw new ArgumentNullException("serializer");

			this.connectionFactory = connectionFactory;
			this.dialect = dialect;
			this.serializer = serializer;
			this.scopeOption = scopeOption;
		}

		public void Dispose()
		{
			this.Dispose(true);
			GC.SuppressFinalize(this);
		}
		protected virtual void Dispose(bool disposing)
		{
			// no-op
		}

		public virtual void Initialize()
		{
			if (Interlocked.Increment(ref this.initialized) > 1)
				return;

			this.ExecuteCommand(Guid.Empty, statement =>
				statement.ExecuteWithSuppression(this.Dialect.InitializeStorage));
		}

		public virtual IEnumerable<Commit> GetFrom(Guid streamId, int minRevision, int maxRevision)
		{
			return this.ExecuteQuery(streamId, query =>
			{
				query.AddParameter(this.Dialect.StreamId, streamId);
				query.AddParameter(this.Dialect.StreamRevision, minRevision);
				query.AddParameter(this.Dialect.MaxStreamRevision, maxRevision);
				return query.ExecuteWithQuery(this.Dialect.GetCommitsFromStartingRevision, x => x.GetCommit(this.Serializer));
			});
		}
		public virtual IEnumerable<Commit> GetFrom(DateTime start)
		{
			return this.ExecuteQuery(Guid.Empty, query =>
			{
				var statement = this.Dialect.GetCommitsFromInstant;
				query.AddParameter(this.Dialect.CommitStamp, start);
				return query.ExecuteWithQuery(statement, x => x.GetCommit(this.Serializer));
			});
		}

		public virtual void Commit(Commit attempt)
		{
			this.ExecuteCommand(attempt.StreamId, cmd =>
			{
				cmd.AddParameter(this.Dialect.StreamId, attempt.StreamId);
				cmd.AddParameter(this.Dialect.StreamRevision, attempt.StreamRevision);
				cmd.AddParameter(this.Dialect.Items, attempt.Events.Count);
				cmd.AddParameter(this.Dialect.CommitId, attempt.CommitId);
				cmd.AddParameter(this.Dialect.CommitSequence, attempt.CommitSequence);
				cmd.AddParameter(this.Dialect.CommitStamp, attempt.CommitStamp);
				cmd.AddParameter(this.Dialect.Headers, this.Serializer.Serialize(attempt.Headers));
				cmd.AddParameter(this.Dialect.Payload, this.Serializer.Serialize(attempt.Events));

                cmd.Execute(this.Dialect.PersistCommit);
            });
		}

		public virtual IEnumerable<Commit> GetUndispatchedCommits()
		{
			return this.ExecuteQuery(Guid.Empty, query =>
				query.ExecuteWithQuery(this.Dialect.GetUndispatchedCommits, x => x.GetCommit(this.Serializer)));
		}
		public virtual void MarkCommitAsDispatched(Commit commit)
		{
			this.ExecuteCommand(commit.StreamId, cmd =>
			{
				cmd.AddParameter(this.Dialect.StreamId, commit.StreamId);
				cmd.AddParameter(this.Dialect.CommitSequence, commit.CommitSequence);
				cmd.ExecuteWithSuppression(this.Dialect.MarkCommitAsDispatched);
			});
		}

		public virtual IEnumerable<StreamHead> GetStreamsToSnapshot(int maxThreshold)
		{
			return this.ExecuteQuery(Guid.Empty, query =>
			{
				var statement = this.Dialect.GetStreamsRequiringSnapshots;
				query.AddParameter(this.Dialect.Threshold, maxThreshold);
				return query.ExecuteWithQuery(statement, record => record.GetStreamToSnapshot());
			});
		}
		public virtual Snapshot GetSnapshot(Guid streamId, int maxRevision)
		{
			return this.ExecuteQuery(streamId, query =>
			{
				var queryText = this.Dialect.GetSnapshot;
				query.AddParameter(this.Dialect.StreamId, streamId);
				query.AddParameter(this.Dialect.StreamRevision, maxRevision);
				return query.ExecuteWithQuery(queryText, x => x.GetSnapshot(this.Serializer)).FirstOrDefault();
			});
		}
		public virtual bool AddSnapshot(Snapshot snapshot)
		{
			var rowsAffected = 0;
			this.ExecuteCommand(snapshot.StreamId, cmd =>
			{
				cmd.AddParameter(this.Dialect.StreamId, snapshot.StreamId);
				cmd.AddParameter(this.Dialect.StreamRevision, snapshot.StreamRevision);
				cmd.AddParameter(this.Dialect.Payload, this.Serializer.Serialize(snapshot.Payload));
				rowsAffected = cmd.ExecuteWithSuppression(this.Dialect.AppendSnapshotToCommit);
			});
			return rowsAffected > 0;
		}

		protected virtual T ExecuteQuery<T>(Guid streamId, Func<IDbStatement, T> query)
		{
			var scope = this.OpenQueryScope();
			IDbConnection connection = null;
			IDbTransaction transaction = null;
			IDbStatement statement = null;

			try
			{
				connection = this.ConnectionFactory.OpenSlave(streamId);
				transaction = this.Dialect.OpenTransaction(connection);
				statement = this.Dialect.BuildStatement(connection, transaction, scope);
				return query(statement);
			}
			catch (Exception e)
			{
				if (statement != null)
					statement.Dispose();
				if (transaction != null)
					transaction.Dispose();
				if (connection != null)
					connection.Dispose();
				scope.Dispose();

				if (e is StorageUnavailableException)
					throw;

				throw new StorageException(e.Message, e);
			}
		}
		protected virtual void ExecuteCommand(Guid streamId, Action<IDbStatement> command)
		{
			using (var scope = this.OpenCommandScope())
			using (var connection = this.ConnectionFactory.OpenMaster(streamId))
			using (var transaction = this.Dialect.OpenTransaction(connection))
			using (var statement = this.Dialect.BuildStatement(connection, transaction, scope))
			{
				try
				{
					command(statement);
					if (transaction != null)
						transaction.Commit();

					scope.Complete();
				}
				catch (Exception e)
				{
					if (e is ConcurrencyException || e is DuplicateCommitException || e is StorageUnavailableException)
						throw;

					throw new StorageException(e.Message, e);
				}
			}
		}
		protected virtual TransactionScope OpenQueryScope()
		{
			return this.OpenCommandScope();
		}
		protected virtual TransactionScope OpenCommandScope()
		{
			return new TransactionScope(this.scopeOption);
		}
	}
}