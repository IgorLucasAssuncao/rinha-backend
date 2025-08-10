using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using Npgsql;
using StackExchange.Redis;
using System.Text.Json;
using System.Text.Json.Serialization;
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

            #region Redis

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

            builder.Services.AddOptions<IOptions<RedisConsumerOptions>>();

            builder.Services.AddHostedService<HighThroughputRedisConsumer>();

            builder.Services.AddHostedService<PaymentVerifier>();

            builder.Services.AddSingleton<PaymentDecider>();

            builder.Services.AddSingleton<PaymentProcessor>();

            #endregion

            var postgresConn = builder.Configuration.GetConnectionString("postgres")!;

            builder.Services.AddSingleton(sp => postgresConn);

            builder.Services.AddHttpClient("default", o =>
                o.BaseAddress = new Uri(builder.Configuration.GetConnectionString("default")!));

            builder.Services.AddHttpClient("fallback", o =>
                o.BaseAddress = new Uri(builder.Configuration.GetConnectionString("fallback")!));


            var app = builder.Build();

            app.MapPost("/payments", async ([FromBody] PaymentsRequest request, [FromServices] IConnectionMultiplexer _redis) =>
            {
                var db = _redis.GetDatabase();
                string json = JsonSerializer.Serialize(request);
                await db.ListLeftPushAsync("payments-queue", json);

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

                var defaultResult = results.FirstOrDefault(r => r.IsDefault) ?? new PaymentSummary(true, 0, 0);
                var fallbackResult = results.FirstOrDefault(r => !r.IsDefault) ?? new PaymentSummary(false, 0, 0);

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

    [JsonSerializable(typeof(Payments))]
    [JsonSerializable(typeof(PaymentsRequest))]
    [JsonSerializable(typeof(PaymentSummary))]
    [JsonSerializable(typeof(PaymentItem))]
    [JsonSerializable(typeof(PaymentsSummaryResponse))]
    [JsonSerializable(typeof(PaymentServiceHealth))]
    [JsonSerializable(typeof(List<Payments>))]
    [JsonSerializable(typeof(List<PaymentsRequest>))]
    [JsonSerializable(typeof(List<PaymentSummary>))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    [JsonSerializable(typeof(object))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(PaymentsRequestDTO))]
    internal partial class AppJsonContext : JsonSerializerContext
    {
    }
}
