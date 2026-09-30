using MySqlConnector;

namespace QamishloRestaurantAPI.Database
{
    public static class DatabaseManager
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "MYSQL_CONNECTION_STRING is not configured.");

        public static MySqlConnection GetConnection()
        {
            return new MySqlConnection(connectionString);
        }
    }
}