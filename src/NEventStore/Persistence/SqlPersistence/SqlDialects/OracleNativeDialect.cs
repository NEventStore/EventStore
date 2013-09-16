namespace NEventStore.Persistence.SqlPersistence.SqlDialects
{
    using System;
    using System.Data;
    using System.Transactions;

    public class OracleNativeDialect : CommonSqlDialect
    {
        private const int UniqueKeyViolation = -2146232008;

        public override string AppendSnapshotToCommit
        {
            get { return OracleNativeStatements.AppendSnapshotToCommit; }
        }

        public override string CommitId
        {
            get { return MakeOracleParameter(base.CommitId); }
        }

        public override string CommitSequence
        {
            get { return MakeOracleParameter(base.CommitSequence); }
        }

        public override string CommitStamp
        {
            get { return MakeOracleParameter(base.CommitStamp); }
        }

        public override string DuplicateCommit
        {
            get { return OracleNativeStatements.DuplicateCommit; }
        }

        public override string GetSnapshot
        {
            get { return OracleNativeStatements.GetSnapshot; }
        }

        public override string GetCommitsFromStartingRevision
        {
            get { return AddOuterTrailingCommitSequence(LimitedQuery(OracleNativeStatements.GetCommitsFromStartingRevision)); }
        }

        public override string GetCommitsFromInstant
        {
            get { return OraclePaging(OracleNativeStatements.GetCommitsFromInstant); }
        }

        public override string GetCommitsFromToInstant
        {
            get { return OraclePaging(OracleNativeStatements.GetCommitsFromToInstant); }
        }

        public override string GetUndispatchedCommits
        {
            get { return OraclePaging(base.GetUndispatchedCommits); }
        }

        public override string GetStreamsRequiringSnapshots
        {
            get { return LimitedQuery(OracleNativeStatements.GetStreamsRequiringSnapshots); }
        }

        public override string InitializeStorage
        {
            get { return OracleNativeStatements.InitializeStorage; }
        }

        public override string Limit
        {
            get { return MakeOracleParameter(base.Limit); }
        }

        public override string MarkCommitAsDispatched
        {
            get { return OracleNativeStatements.MarkCommitAsDispatched; }
        }

        public override string PersistCommit
        {
            get { return OracleNativeStatements.PersistCommit; }
        }

        public override string PurgeStorage
        {
            get { return OracleNativeStatements.PurgeStorage; }
        }

        public override string Drop
        {
            get { return OracleNativeStatements.DropTables; }
        }

        public override string Skip
        {
            get { return MakeOracleParameter(base.Skip); }
        }

        public override string StreamId
        {
            get { return MakeOracleParameter(base.StreamId); }
        }

        public override string Threshold
        {
            get { return MakeOracleParameter(base.Threshold); }
        }

        private string AddOuterTrailingCommitSequence(string query)
        {
            return (query.TrimEnd(new[] {';'}) + "\r\n" + OracleNativeStatements.AddCommitSequence);
        }

        public override IDbStatement BuildStatement(TransactionScope scope, IDbConnection connection, IDbTransaction transaction)
        {
            return new OracleDbStatement(this, scope, connection, transaction);
        }

        public override object CoalesceParameterValue(object value)
        {
            if (value is Guid)
            {
                value = ((Guid) value).ToByteArray();
            }

            return value;
        }

        private static string ExtractOrderBy(ref string query)
        {
            int orderByIndex = query.IndexOf("ORDER BY", StringComparison.Ordinal);
            string result = query.Substring(orderByIndex).Replace(";", String.Empty);
            query = query.Substring(0, orderByIndex);

            return result;
        }

        public override bool IsDuplicate(Exception exception)
        {
            return exception.Message.Contains("ORA-00001");
        }

        private static string LimitedQuery(string query)
        {
            query = RemovePaging(query);
            if (query.EndsWith(";"))
            {
                query = query.TrimEnd(new[] {';'});
            }
            string value = OracleNativeStatements.LimitedQueryFormat.FormatWith(query);
            return value;
        }

        private static string MakeOracleParameter(string parameterName)
        {
            return parameterName.Replace('@', ':');
        }

        private static string OraclePaging(string query)
        {
            query = RemovePaging(query);

            string orderBy = ExtractOrderBy(ref query);

            int fromIndex = query.IndexOf("FROM ", StringComparison.Ordinal);
            string from = query.Substring(fromIndex);

            string select = query.Substring(0, fromIndex);

            string value = OracleNativeStatements.PagedQueryFormat.FormatWith(select, orderBy, from);

            return value;
        }

        private static string RemovePaging(string query)
        {
            return query
                .Replace("\n LIMIT @Limit OFFSET @Skip;", ";")
                .Replace("\n LIMIT @Limit;", ";")
                .Replace("WHERE ROWNUM <= :Limit;", ";")
                .Replace("\r\nWHERE ROWNUM <= (:Skip + 1) AND ROWNUM  > :Skip", ";");
        }
    }
}