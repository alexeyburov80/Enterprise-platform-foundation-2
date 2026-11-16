using Dp.Platform.Adapter.Bitrix;
using Dp.Platform.Framework;

var builder = WebApplication.CreateBuilder(args);

builder.AddDpAdapter<BitrixProfile>();

var app = builder.Build();
app.MapAdapterEndpoints();
app.UseDpService();
app.Run();
