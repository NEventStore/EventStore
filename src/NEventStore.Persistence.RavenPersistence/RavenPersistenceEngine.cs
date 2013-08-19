﻿namespace NEventStore.Persistence.RavenPersistence
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Net;
    using System.Threading;
    using System.Transactions;
    using NEventStore.Logging;
    using NEventStore.Persistence.RavenPersistence.Indexes;
    using NEventStore.Serialization;
    using Raven.Abstractions.Commands;
    using Raven.Abstractions.Data;
    using Raven.Client;
    using Raven.Client.Exceptions;
    using Raven.Client.Indexes;
    using Raven.Json.Linq;
    using ConcurrencyException = NEventStore.ConcurrencyException;

    public class RavenPersistenceEngine : IPersistStreams
    {
        private const int MinPageSize = 10;
        private static readonly ILog Logger = LogFactory.BuildLogger(typeof (RavenPersistenceEngine));
        private readonly bool _consistentQueries;
        private readonly int _pageSize;
        private readonly string _partition;
        private readonly TransactionScopeOption _scopeOption;
        private readonly IDocumentSerializer _serializer;
        private readonly IDocumentStore _store;
        private int _initialized;

        public RavenPersistenceEngine(IDocumentStore store, RavenConfiguration config)
        {
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }

            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            if (config.Serializer == null)
            {
                throw new ArgumentException(Messages.SerializerCannotBeNull, "config");
            }

            if (config.PageSize < MinPageSize)
            {
                throw new ArgumentException(Messages.PagingSizeTooSmall, "config");
            }

            _store = store;
            _serializer = config.Serializer;
            _scopeOption = config.ScopeOption;
            _consistentQueries = config.ConsistentQueries;
            _pageSize = config.PageSize;
            _partition = config.Partition;
        }

        public IDocumentStore Store
        {
            get { return _store; }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public virtual void Initialize()
        {
            if (Interlocked.Increment(ref _initialized) > 1)
            {
                return;
            }

            Logger.Debug(Messages.InitializingStorage);

            TryRaven(() =>
            {
                using (TransactionScope scope = OpenCommandScope())
                {
                    new RavenCommitByDate().Execute(_store);
                    new RavenCommitByRevisionRange().Execute(_store);
                    new RavenCommitsByDispatched().Execute(_store);
                    new RavenSnapshotByStreamIdAndRevision().Execute(_store);
                    new RavenStreamHeadBySnapshotAge().Execute(_store);
                    new EventStoreDocumentsByEntityName().Execute(_store);
                    scope.Complete();
                }

                return true;
            });
        }

        public virtual IEnumerable<Commit> GetFrom(string streamId, int minRevision, int maxRevision)
        {
            Logger.Debug(Messages.GettingAllCommitsBetween, streamId, minRevision, maxRevision);

            return
                QueryCommits<RavenCommitByRevisionRange>(
                                                         x =>
                                                             x.StreamId == streamId && x.StreamRevision >= minRevision &&
                                                                 x.StartingStreamRevision <= maxRevision).OrderBy(x => x.CommitSequence);
        }

        public virtual IEnumerable<Commit> GetFrom(DateTime start)
        {
            Logger.Debug(Messages.GettingAllCommitsFrom, start);

            return QueryCommits<RavenCommitByDate>(x => x.CommitStamp >= start).OrderBy(x => x.CommitStamp);
        }

        public virtual IEnumerable<Commit> GetFromTo(DateTime start, DateTime end)
        {
            Logger.Debug(Messages.GettingAllCommitsFromTo, start, end);

            return QueryCommits<RavenCommitByDate>(x => x.CommitStamp >= start && x.CommitStamp < end).OrderBy(x => x.CommitStamp);
        }

        public virtual void Commit(Commit attempt)
        {
            Logger.Debug(Messages.AttemptingToCommit, attempt.Events.Count, attempt.StreamId, attempt.CommitSequence);

            try
            {
                TryRaven(() =>
                {
                    using (TransactionScope scope = OpenCommandScope())
                    using (IDocumentSession session = _store.OpenSession())
                    {
                        session.Advanced.UseOptimisticConcurrency = true;
                        session.Store(attempt.ToRavenCommit(_partition, _serializer));
                        session.SaveChanges();
                        scope.Complete();
                    }

                    Logger.Debug(Messages.CommitPersisted, attempt.CommitId);
                    SaveStreamHead(attempt.ToRavenStreamHead(_partition));
                    return true;
                });
            }
            catch (Raven.Abstractions.Exceptions.ConcurrencyException)
            {
                RavenCommit savedCommit = LoadSavedCommit(attempt);
                if (savedCommit.CommitId == attempt.CommitId)
                {
                    throw new DuplicateCommitException();
                }

                Logger.Debug(Messages.ConcurrentWriteDetected);
                throw new ConcurrencyException();
            }
        }

        public virtual IEnumerable<Commit> GetUndispatchedCommits()
        {
            Logger.Debug(Messages.GettingUndispatchedCommits);
            return QueryCommits<RavenCommitsByDispatched>(c => c.Dispatched == false).OrderBy(x => x.CommitSequence);
        }

        public virtual void MarkCommitAsDispatched(Commit commit)
        {
            if (commit == null)
            {
                throw new ArgumentNullException("commit");
            }

            var patch = new PatchRequest {Type = PatchCommandType.Set, Name = "Dispatched", Value = RavenJToken.Parse("true")};
            var data = new PatchCommandData {Key = commit.ToRavenCommitId(_partition), Patches = new[] {patch}};

            Logger.Debug(Messages.MarkingCommitAsDispatched, commit.CommitId);

            TryRaven(() =>
            {
                using (TransactionScope scope = OpenCommandScope())
                using (IDocumentSession session = _store.OpenSession())
                {
                    session.Advanced.DocumentStore.DatabaseCommands.Batch(new[] {data});
                    session.SaveChanges();
                    scope.Complete();
                    return true;
                }
            });
        }

        public virtual IEnumerable<StreamHead> GetStreamsToSnapshot(int maxThreshold)
        {
            Logger.Debug(Messages.GettingStreamsToSnapshot);

            return
                Query<RavenStreamHead, RavenStreamHeadBySnapshotAge>(s => s.SnapshotAge >= maxThreshold && s.Partition == _partition)
                    .Select(s => s.ToStreamHead());
        }

        public virtual Snapshot GetSnapshot(string streamId, int maxRevision)
        {
            Logger.Debug(Messages.GettingRevision, streamId, maxRevision);

            return
                Query<RavenSnapshot, RavenSnapshotByStreamIdAndRevision>(
                                                                         x =>
                                                                             x.StreamId == streamId && x.StreamRevision <= maxRevision &&
                                                                                 x.Partition == _partition)
                    .OrderByDescending(x => x.StreamRevision)
                    .FirstOrDefault()
                    .ToSnapshot(_serializer);
        }

        public virtual bool AddSnapshot(Snapshot snapshot)
        {
            if (snapshot == null)
            {
                return false;
            }

            Logger.Debug(Messages.AddingSnapshot, snapshot.StreamId, snapshot.StreamRevision);

            try
            {
                return TryRaven(() =>
                {
                    using (TransactionScope scope = OpenCommandScope())
                    using (IDocumentSession session = _store.OpenSession())
                    {
                        RavenSnapshot ravenSnapshot = snapshot.ToRavenSnapshot(_partition, _serializer);
                        session.Store(ravenSnapshot);
                        session.SaveChanges();
                        scope.Complete();
                    }

                    SaveStreamHead(snapshot.ToRavenStreamHead(_partition));

                    return true;
                });
            }
            catch (Raven.Abstractions.Exceptions.ConcurrencyException)
            {
                return false;
            }
        }

        public virtual void Purge()
        {
            Logger.Warn(Messages.PurgingStorage);

            TryRaven(() =>
            {
                using (TransactionScope scope = OpenCommandScope())
                using (IDocumentSession session = _store.OpenSession())
                {
                    PurgeDocuments(session);

                    session.SaveChanges();
                    scope.Complete();
                    return true;
                }
            });
        }

        public bool IsDisposed
        {
            get { return _store.WasDisposed; }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            Logger.Debug(Messages.ShuttingDownPersistence);
            _store.Dispose();
        }

        private RavenCommit LoadSavedCommit(Commit attempt)
        {
            Logger.Debug(Messages.DetectingConcurrency);

            return TryRaven(() =>
            {
                using (TransactionScope scope = OpenQueryScope())
                using (IDocumentSession session = _store.OpenSession())
                {
                    var commit = session.Load<RavenCommit>(attempt.ToRavenCommitId(_partition));
                    scope.Complete();
                    return commit;
                }
            });
        }

        private void PurgeDocuments(IDocumentSession session)
        {
            Func<Type, string> getTagCondition = t => "Tag:" + session.Advanced.DocumentStore.Conventions.GetTypeTagName(t);

            string typeQuery = "(" + getTagCondition(typeof (RavenCommit)) + " OR " + getTagCondition(typeof (RavenSnapshot)) + " OR " +
                getTagCondition(typeof (RavenStreamHead)) + ")";
            string partitionQuery = "Partition:" + (_partition ?? "[[NULL_VALUE]]");
            string queryText = partitionQuery + " AND " + typeQuery;

            var query = new IndexQuery {Query = queryText};

            const string index = "EventStoreDocumentsByEntityName";

            while (HasDocs(index, query))
            {
                session.Advanced.DocumentStore.DatabaseCommands.DeleteByIndex(index, query, true);
            }
        }

        private bool HasDocs(string index, IndexQuery query)
        {
            while (_store.DatabaseCommands.GetStatistics().StaleIndexes.Contains(index))
            {
                Thread.Sleep(50);
            }

            return _store.DatabaseCommands.Query(index, query, null, true).TotalResults != 0;
        }

        private IEnumerable<Commit> QueryCommits<TIndex>(Expression<Func<RavenCommit, bool>> query)
            where TIndex : AbstractIndexCreationTask, new()
        {
            IEnumerable<RavenCommit> commits = Query<RavenCommit, TIndex>(query, c => c.Partition == _partition);

            return commits.Select(x => x.ToCommit(_serializer));
        }

        private IEnumerable<T> Query<T, TIndex>(params Expression<Func<T, bool>>[] conditions)
            where TIndex : AbstractIndexCreationTask, new()
        {
            return new ResetableEnumerable<T>(() => PagedQuery<T, TIndex>(conditions));
        }

        private IEnumerable<T> PagedQuery<T, TIndex>(Expression<Func<T, bool>>[] conditions) where TIndex : AbstractIndexCreationTask, new()
        {
            int total = 0;
            RavenQueryStatistics stats;

            do
            {
                using (IDocumentSession session = _store.OpenSession())
                {
                    int requestsForSession = 0;

                    do
                    {
                        T[] docs = PerformQuery<T, TIndex>(session, conditions, total, _pageSize, out stats);
                        total += docs.Length;
                        requestsForSession++;

                        foreach (var d in docs)
                        {
                            yield return d;
                        }
                    } while (total < stats.TotalResults && requestsForSession < session.Advanced.MaxNumberOfRequestsPerSession);
                }
            } while (total < stats.TotalResults);
        }

        private T[] PerformQuery<T, TIndex>(
            IDocumentSession session, Expression<Func<T, bool>>[] conditions, int skip, int take, out RavenQueryStatistics stats)
            where TIndex : AbstractIndexCreationTask, new()
        {
            TransactionScope scope = null;

            try
            {
                scope = OpenQueryScope();

                IQueryable<T> query = session.Query<T, TIndex>().Customize(x =>
                {
                    if (_consistentQueries)
                    {
                        x.WaitForNonStaleResults();
                    }
                }).Statistics(out stats);

                query = conditions.Aggregate(query, (current, condition) => current.Where(condition));

                return query.Skip(skip).Take(take).ToArray();
            }
            catch (WebException e)
            {
                Logger.Warn(Messages.StorageUnavailable);
                throw new StorageUnavailableException(e.Message, e);
            }
            catch (ObjectDisposedException)
            {
                Logger.Warn(Messages.StorageAlreadyDisposed);
                throw;
            }
            catch (Exception e)
            {
                Logger.Error(Messages.StorageThrewException, e.GetType());
                throw new StorageException(e.Message, e);
            }
            finally
            {
                if (scope != null)
                {
                    scope.Dispose();
                }
            }
        }

        private void SaveStreamHead(RavenStreamHead streamHead)
        {
            if (_consistentQueries)
            {
                SaveStreamHeadAsync(streamHead);
            }
            else
            {
                ThreadPool.QueueUserWorkItem(x => SaveStreamHeadAsync(streamHead), null);
            }
        }

        private void SaveStreamHeadAsync(RavenStreamHead updated)
        {
            TryRaven(() =>
            {
                using (TransactionScope scope = OpenCommandScope())
                using (IDocumentSession session = _store.OpenSession())
                {
                    RavenStreamHead current = session.Load<RavenStreamHead>(updated.StreamId.ToRavenStreamId(_partition)) ?? updated;
                    current.HeadRevision = updated.HeadRevision;

                    if (updated.SnapshotRevision > 0)
                    {
                        current.SnapshotRevision = updated.SnapshotRevision;
                    }

                    session.Advanced.UseOptimisticConcurrency = false;
                    session.Store(current);
                    session.SaveChanges();
                    scope.Complete(); // if this fails it's no big deal, stream heads can be updated whenever
                }
                return true;
            });
        }

        protected virtual T TryRaven<T>(Func<T> callback)
        {
            try
            {
                return callback();
            }
            catch (WebException e)
            {
                Logger.Warn(Messages.StorageUnavailable);
                throw new StorageUnavailableException(e.Message, e);
            }
            catch (NonUniqueObjectException e)
            {
                Logger.Warn(Messages.DuplicateCommitDetected);
                throw new DuplicateCommitException(e.Message, e);
            }
            catch (Raven.Abstractions.Exceptions.ConcurrencyException)
            {
                Logger.Warn(Messages.ConcurrentWriteDetected);
                throw;
            }
            catch (ObjectDisposedException)
            {
                Logger.Warn(Messages.StorageAlreadyDisposed);
                throw;
            }
            catch (Exception e)
            {
                Logger.Error(Messages.StorageThrewException, e.GetType());
                throw new StorageException(e.Message, e);
            }
        }

        protected virtual TransactionScope OpenQueryScope()
        {
            return OpenCommandScope() ?? new TransactionScope(TransactionScopeOption.Suppress);
        }

        protected virtual TransactionScope OpenCommandScope()
        {
            return new TransactionScope(_scopeOption);
        }
    }
}