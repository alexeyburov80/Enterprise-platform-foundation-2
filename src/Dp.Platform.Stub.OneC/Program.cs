using Dp.Platform.Stub.OneC;
using Dp.Platform.Framework;
using Dp.Platform.Framework.Stubs;

var builder = WebApplication.CreateBuilder(args);

builder.AddDpService("dp-stub-1c");
builder.Services.AddSingleton<StubFailMode>();

var app = builder.Build();

// Тумблер отказа должен идти до всех маршрутов (имитация недоступности 1С).
app.MapStubFailMode();
app.MapOneCStub();
app.UseDpService();
app.Run();
