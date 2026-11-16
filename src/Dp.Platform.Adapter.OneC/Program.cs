using Dp.Platform.Adapter.OneC;
using Dp.Platform.Framework;

var builder = WebApplication.CreateBuilder(args);

builder.AddDpAdapter<OneCProfile>();

var app = builder.Build();
app.MapAdapterEndpoints();
app.UseDpService();
app.Run();
