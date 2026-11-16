using Npgsql;

namespace Dp.Platform.Framework;

/// <summary>
/// Единый способ создать команду с текстом (DRY): NpgsqlConnection и
/// NpgsqlDataSource. SQL подставляется как есть; параметры добавляются отдельно.
/// </summary>
public static class DpDb
{
    public static NpgsqlCommand CreateCommand(this NpgsqlConnection connection, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    public static NpgsqlCommand CreateCommand(this NpgsqlDataSource source, string sql)
    {
        var cmd = source.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }
}
