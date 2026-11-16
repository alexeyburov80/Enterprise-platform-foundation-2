using Dp.Platform.Stub.Bitrix;
using Dp.Platform.Framework;
using Dp.Platform.Framework.Stubs;

var builder = WebApplication.CreateBuilder(args);

builder.AddDpService("dp-stub-bitrix");
builder.Services.AddSingleton<StubFailMode>();
builder.Services.AddOptions<StubBitrixOptions>()
    .BindConfiguration("StubBitrix")
    .ValidateOnStart();

var app = builder.Build();

app.MapStubFailMode();
app.MapBitrixStub();
app.UseDpService();
app.Run();

public sealed class StubBitrixOptions
{
    public string WebhookKey { get; set; } = "demo";
}
