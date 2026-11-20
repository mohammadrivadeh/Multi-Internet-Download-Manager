using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
const int sampleBytes = 65536;
const int defaultFixtureBytes = 262144;
const int maximumFixtureBytes = 256 * 1024 * 1024;
var fixtureBytes = builder.Configuration.GetValue("fixture-bytes", defaultFixtureBytes);
if (fixtureBytes is < sampleBytes or > maximumFixtureBytes)
    throw new ArgumentOutOfRangeException(nameof(fixtureBytes), $"Choose a fixture size from {sampleBytes} to {maximumFixtureBytes} bytes.");

var app = builder.Build();
var fixture = new byte[fixtureBytes];
for (var index = 0; index < fixture.Length; index++)
    fixture[index] = (byte)(index % 251);
var fixtureInfo = new
{
    Length = fixture.Length,
    SampleBytes = sampleBytes,
    SampleSha256 = Convert.ToHexString(SHA256.HashData(fixture.AsSpan(0, sampleBytes))),
    FullSha256 = Convert.ToHexString(SHA256.HashData(fixture))
};
var fixtureTag = fixtureBytes == defaultFixtureBytes ? "\"fixture-v1\"" : $"\"fixture-v1-{fixtureBytes}\"";
app.MapGet("/fixture", () => Results.File(fixture, "application/octet-stream",
    entityTag: new EntityTagHeaderValue(fixtureTag), enableRangeProcessing: true));
app.MapGet("/fixture-info", () => fixtureInfo);
app.MapGet("/redirect", () => Results.Redirect("/fixture"));
app.MapGet("/redirect-loop", () => Results.Redirect("/redirect-loop"));
app.MapGet("/redirect-chain/{remaining:int}", (int remaining) => Results.Redirect(remaining > 0 ? $"/redirect-chain/{remaining - 1}" : "/fixture"));
app.MapGet("/stall", async (HttpContext context) =>
{
    await Task.Delay(Timeout.Infinite, context.RequestAborted);
});
app.Run();
