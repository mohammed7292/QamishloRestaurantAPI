using MySqlConnector;

namespace QamishloRestaurantAPI.Database
{
    public static class DatabaseManager
    {
        private static readonly string connectionString = BuildOptimizedConnectionString();

        private static string BuildOptimizedConnectionString()
        {
            string raw = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING")
                ?? throw new InvalidOperationException("MYSQL_CONNECTION_STRING is not configured.");
            try
            {
                // Tuned specifically for TiDB Cloud Serverless (prevents stale/closed idle socket errors)
                var builder = new MySqlConnectionStringBuilder(raw)
                {
                    Pooling = true,
                    MinimumPoolSize = 0,
                    MaximumPoolSize = 40,
                    ConnectionIdleTimeout = 45,
                    ConnectionLifeTime = 240,
                    Keepalive = 20,
                    ConnectionReset = true
                };
                return builder.ConnectionString;
            }
            catch
            {
                return raw;
            }
        }

        public static MySqlConnection GetConnection()
        {
            return new MySqlConnection(connectionString);
        }
    }
}
