using Dp.Platform.Cdc;
using Dp.Platform.Framework;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddDpService("dp-cdc");

var connectionString = builder.Configuration.GetConnectionString("Cdc")!;
builder.Services.AddHealthChecks().AddNpgsqlCheck("cdc-db", connectionString);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

builder.Services.AddOptions<CdcOptions>().BindConfiguration(CdcOptions.SectionName).ValidateOnStart();
builder.Services.AddOptions<RabbitOptions>().BindConfiguration(RabbitOptions.SectionName).ValidateOnStart();

builder.Services.AddSingleton<IOutboxProcessor, PgOutboxProcessor>();
builder.Services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
builder.Services.AddHttpClient<VersionResolver>();
builder.Services.AddHostedService<OutboxPoller>();

var app = builder.Build();
app.UseDpService();
app.Run();
