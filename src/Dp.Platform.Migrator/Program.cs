using Dp.Platform.Migrator;

var connection = Environment.GetEnvironmentVariable("DP_MIGRATOR__CONNECTION")
                 ?? Environment.GetEnvironmentVariable("ConnectionStrings__Migrator")
                 ?? "Host=localhost;Port=5432;Database=dp;Username=dp;Password=dp";
var migrationsDir = args.Length > 0 ? args[0] : "/app/migrations";

if (!Directory.Exists(migrationsDir))
{
    Console.Error.WriteLine($"[migrator] migrations directory not found: {migrationsDir}");
    return 2;
}

var runner = new MigrationRunner(connection, migrationsDir, Console.WriteLine);
try
{
    await runner.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[migrator] FAILED: {ex.Message}");
    return 1;
}
