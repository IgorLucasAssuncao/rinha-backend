using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using Npgsql;
using Npgsql.Internal;
using Org.BouncyCastle.Asn1.Ocsp;
using Polly;
using Polly.Extensions.Http;
using StackExchange.Redis;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using static rinha_backend.Models;
using static rinha_backend.Requests;
using static rinha_backend.Responses;

namespace rinha_backend
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateSlimBuilder(args);
            builder.Configuration.AddEnvironmentVariables();

            builder.WebHost.ConfigureKestrel(options =>
            {
                var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH") ?? "/tmp/backend.sock";

                options.ListenUnixSocket(socketPath);

                if (builder.Environment.IsDevelopment())
                {
                    options.ListenLocalhost(5000);
                }
            });

            builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "redis:6379";

                var options = ConfigurationOptions.Parse(redisConnection);

                options.AbortOnConnectFail = false;
                options.ConnectRetry = 3;
                options.SyncTimeout = 5000;
                options.ConnectTimeout = 5000;
                options.KeepAlive = 60;

                options.SocketManager = new SocketManager(workerCount: Environment.ProcessorCount);

                return ConnectionMultiplexer.Connect(options);
            });

            #region Redis

            builder.Services.AddHostedService<RedisConsumer>();

            builder.Services.AddHostedService<PaymentVerifier>();

            builder.Services.AddSingleton<PaymentDecider>();

            builder.Services.AddSingleton<PaymentProcessor>();

            #endregion

            var postgresConn = builder.Configuration.GetConnectionString("postgres")!;

            builder.Services.AddSingleton(provider =>
            {
                var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConn);
                return dataSourceBuilder.Build();
            });

            builder.Services.AddHttpClient("default", o =>
                o.BaseAddress = new Uri(builder.Configuration.GetConnectionString("default")!));

            builder.Services.AddHttpClient("fallback", o =>
                o.BaseAddress = new Uri(builder.Configuration.GetConnectionString("fallback")!));

            var app = builder.Build();

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH") ?? "/tmp/backend.sock";

                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                                   UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                                   UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            });

            app.MapPost("/payments", async (HttpContext context, [FromServices] IConnectionMultiplexer _redis) =>
            {
                using var ms = new MemoryStream();
                await context.Request.Body.CopyToAsync(ms);
                var rawBody = ms.ToArray();

                _ = Task.Run(async () =>
                {
                    var db = _redis.GetDatabase();
                    await db.ListRightPushAsync("payments-queue", rawBody, flags: StackExchange.Redis.CommandFlags.FireAndForget).ConfigureAwait(false);
                });


                return Results.Accepted();
            });

            app.MapGet("/payments-summary", async (DateTimeOffset? from, DateTimeOffset? to) =>
            {

                await using var conn = new NpgsqlConnection(postgresConn);
                await conn.OpenAsync();

                var query = @"
                    SELECT IsDefault,
                          COUNT(*) AS TotalRequests,
                          SUM(Amount) AS TotalAmount
                    FROM payments
                    WHERE(@from IS NULL OR RequestedAt >= @from)
                      AND(@to IS NULL OR RequestedAt <= @to)
                    GROUP BY IsDefault";

                var results = await conn.QueryAsync<PaymentSummary>(query, new
                {
                    from,
                    to
                });

                var defaultResult = results.FirstOrDefault(r => r.IsDefault);
                var fallbackResult = results.FirstOrDefault(r => !r.IsDefault);

                var summary = new PaymentsSummaryResponse(
                    new(defaultResult.TotalRequests, defaultResult.TotalAmount),
                    new(fallbackResult.TotalRequests, fallbackResult.TotalAmount)
                );
                return Results.Ok(summary);
            });

            app.MapPost("/purge-payments", async () =>
            {
                await using var conn = new NpgsqlConnection(postgresConn);
                await conn.OpenAsync();
                const string sql = "TRUNCATE TABLE payments";
                await conn.ExecuteAsync(sql);
            });

            app.Run();
        }
    }
}
