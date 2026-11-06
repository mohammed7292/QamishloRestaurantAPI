using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Security;

public static class ManagerCredentialValidator
{
    public static async Task<bool> IsManagerAsync(HttpRequest request)
        => await GetManagerUserIdAsync(request) is > 0;

    public static async Task<int?> GetManagerUserIdAsync(HttpRequest request)
    {
        var username = request.Headers["X-Manager-Username"].ToString();
        var password = request.Headers["X-Manager-Password"].ToString();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return null;

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        const string sql = "SELECT UserID FROM Users WHERE Username=@username AND Password=@password AND IsActive=1 AND Role IN ('Admin','Manager') LIMIT 1";
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@username", username.Trim());
        cmd.Parameters.AddWithValue("@password", password);
        var result = await cmd.ExecuteScalarAsync();
        return result is null || result == DBNull.Value ? null : Convert.ToInt32(result);
    }
}
