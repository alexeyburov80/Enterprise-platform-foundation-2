using Dp.Platform.Framework;
using Dp.Platform.Gateway;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddDpService("dp-gateway");
builder.Services.AddOptions<GatewayOptions>()
    .BindConfiguration(GatewayOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddHttpClient("dp-gateway");
builder.Services.AddHttpClient("dp-diagnostics");

var app = builder.Build();

app.UseGatewayAuth();
app.MapGatewayDiagnostics();
app.MapGatewayProxy();
app.UseDpService();

app.Run();
