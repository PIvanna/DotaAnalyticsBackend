using Npgsql;

public static class DatabaseErrorHelper
{
    public static string MapDatabaseError(Exception ex)
    {
        if (ex is HttpRequestException)
            return "❌ Network error: Failed to connect to OpenDota API. Check the internet or try it later.";

        if (ex is Newtonsoft.Json.JsonException || ex is System.Text.Json.JsonException)
            return "❌ Data error: An incorrect response from the API was received.";

        if (ex is TimeoutException)
            return "❌ Timeout error: The operation took too long. The server might be overloaded.";
            
        if (ex is OperationCanceledException)
            return "❌ Cancelled: The operation was stopped by the user or system.";

        var pgEx = FindPostgresException(ex);
        
        if (pgEx != null)
        {
            return pgEx.SqlState switch
            {
                "23503" => "❌ Integrity error: The related record (Role or Player) was not found. Please ensure all base data is loaded.",                "23505" => "❌ Duplicate error: Such a record already exists.",
                "23502" => "❌ Data error: One of the required fields is not filled.",
                "42P01" => "❌ Schema error: Table not found in database. Check migrations.",
                "08001" => "❌ Connection error: Failed to connect to database server.",
                
                "57014" => "❌ Timeout: The database query was cancelled because it took too long.",
                "53300" => "❌ Server busy: Too many connections to the database. Try again in a few seconds.",
                "40001" => "❌ Conflict: A deadlock occurred. Please retry the operation.",
                "42501" => "❌ Access denied: Insufficient privileges for this database operation.",

                _ => $"❌ PostgreSQL error ({pgEx.SqlState}): {pgEx.MessageText}"
            };
        }

        return $"❌ System error: {ex.Message}";
    }

    private static PostgresException? FindPostgresException(Exception? ex)
    {
        while (ex != null)
        {
            if (ex is PostgresException pg) return pg;
            ex = ex.InnerException;
        }
        return null;
    }
}