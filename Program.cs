using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using Npgsql;
using Npgsql.Internal;
using Polly;
using Polly.Extensions.Http;
using StackExchange.Redis;
using System.Diagnostics;
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

            #region Redis

            builder.Services.AddOptions<IOptions<RedisConsumerOptions>>();

            builder.Services.AddHostedService<HighThroughputRedisConsumer>();

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

            builder.Services.AddSingleton(provider =>
            {
                var options = new UnboundedChannelOptions()
                {
                    SingleReader = false,
                    SingleWriter = false,
                    AllowSynchronousContinuations = true
                };
                return Channel.CreateUnbounded<PaymentsRequest>(options);
            });

            builder.Services.AddSingleton(provider =>
            provider.GetRequiredService<Channel<PaymentsRequest>>().Writer);

            builder.Services.AddSingleton(provider =>
                provider.GetRequiredService<Channel<PaymentsRequest>>().Reader);

            var app = builder.Build();

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH") ?? "/tmp/backend.sock";

                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                                   UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                                   UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            });

            app.MapPost("/payments", async ([FromBody] PaymentsRequest payment, [FromServices] ChannelWriter<PaymentsRequest> writer, CancellationToken token/*[FromServices] ILogger<Program> logger*/) =>
            {
                await writer.WriteAsync(payment, token);
                return Results.Ok();
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
