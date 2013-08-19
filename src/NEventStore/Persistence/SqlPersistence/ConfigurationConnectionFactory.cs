namespace NEventStore.Persistence.SqlPersistence
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Data;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using NEventStore.Logging;

    public class ConfigurationConnectionFactory : IConnectionFactory
    {
        private const int DefaultShards = 0; // TODO remove sharding functionality
        private const string DefaultConnectionName = "EventStore";

        private static readonly ILog Logger = LogFactory.BuildLogger(typeof (ConfigurationConnectionFactory));

        private static readonly IDictionary<string, ConnectionStringSettings> CachedSettings =
            new Dictionary<string, ConnectionStringSettings>();

        private static readonly IDictionary<string, DbProviderFactory> CachedFactories =
            new Dictionary<string, DbProviderFactory>();

        private readonly string _masterConnectionName;
        private readonly string _replicaConnectionName;
        private readonly int _shards;

        public ConfigurationConnectionFactory(string connectionName)
            : this(connectionName, connectionName, DefaultShards)
        {}

        public ConfigurationConnectionFactory(
            string masterConnectionName, string replicaConnectionName, int shards)
        {
            _masterConnectionName = masterConnectionName ?? DefaultConnectionName;
            _replicaConnectionName = replicaConnectionName ?? _masterConnectionName;
            _shards = shards >= 0 ? shards : DefaultShards;

            Logger.Debug(Messages.ConfiguringConnections,
                _masterConnectionName, _replicaConnectionName, _shards);
        }

        public virtual ConnectionStringSettings Settings
        {
            get { return GetConnectionStringSettings(_masterConnectionName); }
        }

        public virtual IDbConnection OpenMaster(string streamId)
        {
            Logger.Verbose(Messages.OpeningMasterConnection, _masterConnectionName);
            return Open(streamId, _masterConnectionName);
        }

        public virtual IDbConnection OpenReplica(string streamId)
        {
            Logger.Verbose(Messages.OpeningReplicaConnection, _replicaConnectionName);
            return Open(streamId, _replicaConnectionName);
        }

        public static IDisposable OpenScope()
        {
            KeyValuePair<string, ConnectionStringSettings> settings = CachedSettings.FirstOrDefault();
            if (string.IsNullOrEmpty(settings.Key))
            {
                throw new ConfigurationErrorsException(Messages.NotConnectionsAvailable);
            }

            return OpenScope(string.Empty, settings.Key);
        }

        public static IDisposable OpenScope(string connectionName)
        {
            return OpenScope(string.Empty, connectionName);
        }

        public static IDisposable OpenScope(string streamId, string connectionName)
        {
            var factory = new ConfigurationConnectionFactory(connectionName);
            return factory.Open(streamId, connectionName);
        }

        protected virtual IDbConnection Open(string streamId, string connectionName)
        {
            ConnectionStringSettings setting = GetSetting(connectionName);
            string connectionString = BuildConnectionString(streamId, setting);
            return new ConnectionScope(connectionString, () => Open(connectionString, setting));
        }

        protected virtual IDbConnection Open(string connectionString, ConnectionStringSettings setting)
        {
            DbProviderFactory factory = GetFactory(setting);
            DbConnection connection = factory.CreateConnection();
            if (connection == null)
            {
                throw new ConfigurationErrorsException(Messages.BadConnectionName);
            }

            connection.ConnectionString = connectionString;

            try
            {
                Logger.Verbose(Messages.OpeningConnection, setting.Name);
                connection.Open();
            }
            catch (Exception e)
            {
                Logger.Warn(Messages.OpenFailed, setting.Name);
                throw new StorageUnavailableException(e.Message, e);
            }

            return connection;
        }

        protected virtual ConnectionStringSettings GetSetting(string connectionName)
        {
            lock (CachedSettings)
            {
                ConnectionStringSettings setting;
                if (CachedSettings.TryGetValue(connectionName, out setting))
                {
                    return setting;
                }

                setting = GetConnectionStringSettings(connectionName);
                return CachedSettings[connectionName] = setting;
            }
        }

        protected virtual DbProviderFactory GetFactory(ConnectionStringSettings setting)
        {
            lock (CachedFactories)
            {
                DbProviderFactory factory;
                if (CachedFactories.TryGetValue(setting.Name, out factory))
                {
                    return factory;
                }

                factory = DbProviderFactories.GetFactory(setting.ProviderName);
                Logger.Debug(Messages.DiscoveredConnectionProvider, setting.Name, factory.GetType());
                return CachedFactories[setting.Name] = factory;
            }
        }

        protected virtual ConnectionStringSettings GetConnectionStringSettings(string connectionName)
        {
            Logger.Debug(Messages.DiscoveringConnectionSettings, connectionName);

            ConnectionStringSettings settings = ConfigurationManager.ConnectionStrings
                                                                    .Cast<ConnectionStringSettings>()
                                                                    .FirstOrDefault(x => x.Name == connectionName);

            if (settings == null)
            {
                throw new ConfigurationErrorsException(
                    Messages.ConnectionNotFound.FormatWith(connectionName));
            }

            if ((settings.ConnectionString ?? string.Empty).Trim().Length == 0)
            {
                throw new ConfigurationErrorsException(
                    Messages.MissingConnectionString.FormatWith(connectionName));
            }

            if ((settings.ProviderName ?? string.Empty).Trim().Length == 0)
            {
                throw new ConfigurationErrorsException(
                    Messages.MissingProviderName.FormatWith(connectionName));
            }

            return settings;
        }

        protected virtual string BuildConnectionString(string streamId, ConnectionStringSettings setting)
        {
            if (_shards == 0)
            {
                return setting.ConnectionString;
            }

            Logger.Verbose(Messages.EmbeddingShardKey, setting.Name);
            return setting.ConnectionString.FormatWith(ComputeHashKey(streamId));
        }

        protected virtual string ComputeHashKey(string streamId)
        {
            // simple sharding scheme which could easily be improved through such techniques
            // as consistent hashing (Amazon Dynamo) or other kinds of sharding.
            return (_shards == 0 ? 0 : Encoding.UTF8.GetBytes(streamId)[0] % _shards).ToString(CultureInfo.InvariantCulture);
        }
    }
}