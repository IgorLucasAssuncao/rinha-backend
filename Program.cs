using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using StackExchange.Redis;
using System;
using System.Text.Json;
using static rinha_backend.Requests;
using static rinha_backend.Responses;

namespace rinha_backend
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateSlimBuilder(args);

            builder.Services.AddControllers();

            #region Redis

            builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var options = ConfigurationOptions.Parse("localhost:6379");
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

            var mySqlconn = builder.Configuration.GetConnectionString("postgres");
 
            builder.Services.AddHttpClient("default", o =>
                o.BaseAddress = new Uri(builder.Configuration.GetConnectionString("default")!));

            builder.Services.AddHttpClient("fallback", o =>
                o.BaseAddress = new Uri(builder.Configuration.GetConnectionString("fallback")!));
            

            var app = builder.Build();

            app.UseAuthorization();

            app.MapPost("/payments", async ([FromBody]PaymentsRequest request, [FromServices] IConnectionMultiplexer _redis) =>
            {
                var db = _redis.GetDatabase();
                string json = JsonSerializer.Serialize(request);
                await db.ListLeftPushAsync("payments-queue", json);

                return Results.Ok();
            });

            app.MapGet("/payments-summary", async (DateTimeOffset? from, DateTimeOffset? to) =>
            {

                await using var conn = new MySqlConnection(mySqlconn);
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
                    new (defaultResult.TotalRequests, defaultResult.TotalAmount),
                    new (fallbackResult.TotalRequests, fallbackResult.TotalAmount)
                );
                return Results.Ok(summary);
            });

            app.MapPost("/purge-payments", async () =>
            {
                await using var conn = new MySqlConnection(mySqlconn);
                await conn.OpenAsync();
                const string sql = "TRUNCATE TABLE payments";
                await conn.ExecuteAsync(sql);
            });

            app.MapControllers();

            app.Run();
        }
    }
}
