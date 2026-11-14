using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var fixture = Enumerable.Range(0, 262144).Select(i => (byte)(i % 251)).ToArray();
app.MapGet("/fixture", () => Results.File(fixture, "application/octet-stream",
    entityTag: new EntityTagHeaderValue("\"fixture-v1\""), enableRangeProcessing: true));
app.MapGet("/fixture-info", () => new { Length = fixture.Length,
    SampleBytes = 65536, SampleSha256 = Convert.ToHexString(SHA256.HashData(fixture.AsSpan(0, 65536))),
    FullSha256 = Convert.ToHexString(SHA256.HashData(fixture)) });
app.MapGet("/redirect", () => Results.Redirect("/fixture"));
app.MapGet("/redirect-loop", () => Results.Redirect("/redirect-loop"));
app.MapGet("/redirect-chain/{remaining:int}", (int remaining) => Results.Redirect(remaining > 0 ? $"/redirect-chain/{remaining - 1}" : "/fixture"));
app.MapGet("/stall", async (HttpContext context) =>
{
    await Task.Delay(Timeout.Infinite, context.RequestAborted);
});
app.Run();
