using Dp.Platform.Data;
using Dp.Platform.Data.Operations;
using Dp.Platform.Framework;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddDpService("dp-data");

var connectionString = builder.Configuration.GetConnectionString("Data")!;
builder.Services.AddHealthChecks().AddNpgsqlCheck("data-db", connectionString);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

builder.Services.AddOptions<RegistryOptions>()
    .BindConfiguration(RegistryOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddHttpClient<IStoreSchemaProvider, RegistryClient>();

builder.Services.AddSingleton<SqlFilterTranslator>();
builder.Services.AddSingleton<TableProvisioner>();
builder.Services.AddSingleton<IRecordRepository, PgRecordRepository>();
builder.Services.AddSingleton<DataOperations>();

// Реестр операций: OCP — новая операция = один класс + одна строка здесь.
builder.Services.AddKeyedSingleton<IDataOperation, InsertOperation>("insert");
builder.Services.AddKeyedSingleton<IDataOperation, BulkInsertOperation>("bulk-insert");
builder.Services.AddKeyedSingleton<IDataOperation, GetOperation>("get");
builder.Services.AddKeyedSingleton<IDataOperation, QueryOperation>("query");
builder.Services.AddKeyedSingleton<IDataOperation, PatchOperation>("patch");
builder.Services.AddKeyedSingleton<IDataOperation, ReplaceOperation>("replace");
builder.Services.AddKeyedSingleton<IDataOperation, DeleteOperation>("delete");
builder.Services.AddKeyedSingleton<IDataOperation, CountOperation>("count");
builder.Services.AddKeyedSingleton<IDataOperation, PatchAllOperation>("patch-all");

var app = builder.Build();
app.MapDataEndpoints();
app.UseDpService();
app.Run();
