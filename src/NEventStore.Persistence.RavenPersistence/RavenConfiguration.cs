namespace NEventStore.Persistence.RavenPersistence
{
    using System;
    using System.Transactions;
    using NEventStore.Serialization;

    public class RavenConfiguration
    {
        [Obsolete("This will be removed after 3.2")]
        public string ConnectionName { get; set; }

        [Obsolete("This will be removed after 3.2")]
        public Uri Url { get; set; }

        [Obsolete("This will be removed after 3.2")]
        public string DefaultDatabase { get; set; }

        [Obsolete("Raven partition support will be will be removed in 5.0")]
        public string Partition { get; set; }

        public IDocumentSerializer Serializer { get; set; }
        public TransactionScopeOption ScopeOption { get; set; }
        public bool ConsistentQueries { get; set; }
        public int RequestedPageSize { get; set; }
        public int MaxServerPageSize { get; set; }

        public int PageSize
        {
            get
            {
                if (RequestedPageSize > MaxServerPageSize)
                {
                    return MaxServerPageSize;
                }

                return RequestedPageSize;
            }
        }
    }
}