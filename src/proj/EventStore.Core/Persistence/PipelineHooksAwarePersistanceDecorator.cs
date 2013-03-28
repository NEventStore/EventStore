﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EventStore.Logging;

namespace EventStore.Persistence
{
    public class PipelineHooksAwarePersistanceDecorator : IPersistStreams
    {
        private static readonly ILog Logger = LogFactory.BuildLogger(typeof(PipelineHooksAwarePersistanceDecorator));
        private readonly IPersistStreams original;
        private readonly IEnumerable<IPipelineHook> pipelineHooks;

        public PipelineHooksAwarePersistanceDecorator(IPersistStreams original, IEnumerable<IPipelineHook> pipelineHooks)
        {
            if (original == null) throw new ArgumentNullException("original");
            if (pipelineHooks == null) throw new ArgumentNullException("pipelineHooks");
            this.original = original;
            this.pipelineHooks = pipelineHooks;
        }

        #region Implementation of IDisposable

        public void Dispose()
        {
            original.Dispose();
        }

        #endregion

        #region Implementation of ICommitEvents

        public IEnumerable<Commit> GetFrom(Guid streamId, int minRevision, int maxRevision)
        {
            return original.GetFrom(streamId, minRevision, maxRevision);
        }

        public void Commit(Commit attempt)
        {
            original.Commit(attempt);
        }

        #endregion

        #region Implementation of IAccessSnapshots

        public Snapshot GetSnapshot(Guid streamId, int maxRevision)
        {
            return original.GetSnapshot(streamId, maxRevision);
        }

        public bool AddSnapshot(Snapshot snapshot)
        {
            return original.AddSnapshot(snapshot);
        }

        public IEnumerable<StreamHead> GetStreamsToSnapshot(int maxThreshold)
        {
            return original.GetStreamsToSnapshot(maxThreshold);
        }

        #endregion

        #region Implementation of IPersistStreams

        public void Initialize()
        {
            original.Initialize();
        }

        public IEnumerable<Commit> GetFrom(DateTime start)
        {
            return ExecuteHooks(this.original.GetFrom(start));
        }

        public IEnumerable<Commit> GetFromTo(DateTime start, DateTime end)
        {
            return ExecuteHooks(this.original.GetFromTo(start, end));
        }

        public IEnumerable<Commit> GetUndispatchedCommits()
        {
            return original.GetUndispatchedCommits();
        }

        public void MarkCommitAsDispatched(Commit commit)
        {
            original.MarkCommitAsDispatched(commit);
        }

        public void Purge()
        {
            original.Purge();
        }

        #endregion

        private IEnumerable<Commit> ExecuteHooks(IEnumerable<Commit> commits)
        {
            foreach (var commit in commits)
            {
                var filtered = commit;
                foreach (var hook in this.pipelineHooks.Where(x => (filtered = x.Select(filtered)) == null))
                {
                    Logger.Info(Resources.PipelineHookSkippedCommit, hook.GetType(), commit.CommitId);
                    break;
                }

                if (filtered == null)
                    Logger.Info(Resources.PipelineHookFilteredCommit);
                else
                    yield return filtered;
            }
        }
    }
}
