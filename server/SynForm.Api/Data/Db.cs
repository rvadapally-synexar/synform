using Npgsql;

namespace SynForm.Api.Data;

/// <summary>Connection factory + tiny SQL-file migration runner (runs at startup).</summary>
public sealed class Db(IConfiguration config, ILogger<Db> logger)
{
    private readonly string _connString = config.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default missing");

    public NpgsqlConnection Open()
    {
        var conn = new NpgsqlConnection(_connString);
        conn.Open();
        return conn;
    }

    public void Migrate(string sqlDir)
    {
        using var conn = Open();
        // schema_migration table is created by 001 itself; bootstrap check first.
        using (var cmd = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migration (filename TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now());", conn))
            cmd.ExecuteNonQuery();

        foreach (var file in Directory.GetFiles(sqlDir, "*.sql").OrderBy(f => f))
        {
            var name = Path.GetFileName(file);
            using (var check = new NpgsqlCommand("SELECT 1 FROM schema_migration WHERE filename = @f", conn))
            {
                check.Parameters.AddWithValue("f", name);
                if (check.ExecuteScalar() != null) continue;
            }
            logger.LogInformation("Applying migration {File}", name);
            using var tx = conn.BeginTransaction();
            using (var apply = new NpgsqlCommand(File.ReadAllText(file), conn, tx))
                apply.ExecuteNonQuery();
            using (var record = new NpgsqlCommand("INSERT INTO schema_migration (filename) VALUES (@f)", conn, tx))
            {
                record.Parameters.AddWithValue("f", name);
                record.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
