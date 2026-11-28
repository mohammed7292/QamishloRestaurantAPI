using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Security;

public static class ManagerCredentialValidator
{
    public static async Task<int?> GetManagerUserIdAsync(HttpRequest request)
    {
        string username = request.Headers["X-Manager-Username"].ToString();
        string password = request.Headers["X-Manager-Password"].ToString();

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();

        string pin = request.Headers["X-Manager-Pin"].ToString().Trim();
        if (!string.IsNullOrWhiteSpace(pin))
        {
            try
            {
                const string pinSql = @"
                    SELECT UserID
                    FROM users
                    WHERE PinCode = @pin
                      AND IsActive = 1
                    LIMIT 1;";
                await using var pinCmd = new MySqlCommand(pinSql, connection);
                pinCmd.Parameters.AddWithValue("@pin", pin);
                object? pinRes = await pinCmd.ExecuteScalarAsync();
                if (pinRes != null && pinRes != DBNull.Value)
                    return Convert.ToInt32(pinRes);
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            const string sql = @"
                SELECT UserID
                FROM users
                WHERE Username = @username
                  AND Password = @password
                  AND IsActive = 1
                  AND Role IN ('Admin', 'Manager')
                LIMIT 1;";
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@username", username.Trim());
            command.Parameters.AddWithValue("@password", password);
            object? result = await command.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
                return Convert.ToInt32(result);

            // Self-heal if default admin/hamo was accidentally deactivated
            const string healSql = @"
                SELECT UserID
                FROM users
                WHERE Username = @username
                  AND Password = @password
                  AND Role IN ('Admin', 'Manager')
                LIMIT 1;";
            await using var healCmd = new MySqlCommand(healSql, connection);
            healCmd.Parameters.AddWithValue("@username", username.Trim());
            healCmd.Parameters.AddWithValue("@password", password);
            object? healResult = await healCmd.ExecuteScalarAsync();
            if (healResult != null && healResult != DBNull.Value)
            {
                int uid = Convert.ToInt32(healResult);
                await using var activateCmd = new MySqlCommand("UPDATE users SET IsActive = 1 WHERE UserID = @id;", connection);
                activateCmd.Parameters.AddWithValue("@id", uid);
                await activateCmd.ExecuteNonQueryAsync();
                return uid;
            }
        }

        return null;
    }

    public static async Task<bool> IsManagerAsync(HttpRequest request)
        => (await GetManagerUserIdAsync(request)).HasValue;

    public static async Task<int> ResolveValidOrderUserIdAsync(MySqlConnection connection, MySqlTransaction? transaction, int? preferredUserId = null)
    {
        if (preferredUserId.HasValue && preferredUserId.Value > 0)
        {
            await using var checkCmd = new MySqlCommand("SELECT UserID FROM users WHERE UserID = @id LIMIT 1;", connection, transaction);
            checkCmd.Parameters.AddWithValue("@id", preferredUserId.Value);
            var existing = await checkCmd.ExecuteScalarAsync();
            if (existing != null && existing != DBNull.Value)
                return Convert.ToInt32(existing);
        }

        await using (var anyCmd = new MySqlCommand("SELECT UserID FROM users ORDER BY IsActive DESC, UserID ASC LIMIT 1;", connection, transaction))
        {
            var anyUser = await anyCmd.ExecuteScalarAsync();
            if (anyUser != null && anyUser != DBNull.Value)
                return Convert.ToInt32(anyUser);
        }

        const string seedUserSql = @"
            INSERT INTO users (UserID, FullName, Username, Password, Role, Phone, IsActive)
            VALUES (1, 'Qamishlo', 'admin', 'Qamishlo@12345', 'Admin', '07518254562', 1)
            ON DUPLICATE KEY UPDATE IsActive = 1;
            SELECT UserID FROM users ORDER BY UserID ASC LIMIT 1;";
        await using var seedCmd = new MySqlCommand(seedUserSql, connection, transaction);
        return Convert.ToInt32(await seedCmd.ExecuteScalarAsync());
    }
}
