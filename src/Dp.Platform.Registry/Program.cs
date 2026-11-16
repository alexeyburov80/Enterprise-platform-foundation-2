using Dp.Platform.Framework;
using Dp.Platform.Registry;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddDpService("dp-registry");

// NpgsqlDataSource регистрируется вручную (вендоренный Npgsql без DI-пакета).
var connectionString = builder.Configuration.GetConnectionString("Registry")!;
builder.Services.AddHealthChecks().AddNpgsqlCheck("registry-db", connectionString);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

builder.Services.AddSingleton<IStoreRepository, PgStoreRepository>();
builder.Services.AddSingleton<SchemaService>();

var app = builder.Build();
app.MapSchemaEndpoints();
app.UseDpService();
app.Run();
