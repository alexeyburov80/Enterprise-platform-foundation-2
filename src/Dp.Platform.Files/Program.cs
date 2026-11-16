using Dp.Platform.Files;
using Dp.Platform.Framework;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddDpService("dp-files");

var connectionString = builder.Configuration.GetConnectionString("Files")!;
builder.Services.AddHealthChecks().AddNpgsqlCheck("files-db", connectionString);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

builder.Services.AddOptions<FilesOptions>()
    .BindConfiguration(FilesOptions.SectionName)
    .ValidateOnStart();

var filesRoot = builder.Configuration["Files:DataRoot"] ?? "/data/blobs";
builder.Services.AddSingleton<IBlobStore>(new ContentAddressedBlobStore(filesRoot));
builder.Services.AddSingleton<IFileMetaRepository, PgFileMetaRepository>();
builder.Services.AddSingleton<FileService>(
    sp => new FileService(
        sp.GetRequiredService<IBlobStore>(),
        sp.GetRequiredService<IFileMetaRepository>(),
        sp.GetRequiredService<IOptions<FilesOptions>>().Value));

var app = builder.Build();
app.MapFileEndpoints();
app.UseDpService();
app.Run();
