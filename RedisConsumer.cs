using Dapper;
using Microsoft.Extensions.Options;
using Npgsql.Internal;
using StackExchange.Redis;
using System;
using System.Linq;
using System.Reflection.Metadata;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using static rinha_backend.Models;
using static rinha_backend.Requests;

namespace rinha_backend
{
    internal class RedisConsumer : BackgroundService
    {
        private readonly ILogger<RedisConsumer> _logger;
        private readonly PaymentProcessor _processor;
        private readonly IDatabase _redisConn;

        public RedisConsumer(
            ILogger<RedisConsumer> logger,
            PaymentProcessor processor,
            IConnectionMultiplexer redisCon
            )
        {
            _logger = logger;
            _processor = processor;
            _redisConn = redisCon.GetDatabase();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var consumerTasks = new Task[10];
            for (int i = 0; i < consumerTasks.Length; i++)
                consumerTasks[i] = ConsumeAsync(i, stoppingToken);

            await Task.WhenAll(consumerTasks.ToArray());
        }

        private async Task ConsumeAsync(int consumerId, CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    RedisValue msg;

                    while ((msg = await _redisConn.ListLeftPopAsync("payments-queue").ConfigureAwait(false)).HasValue)
                    {
                        var paymentResult = await _processor.SendPayment(JsonSerializer.Deserialize<PaymentsRequest>(msg!));

                        if (!paymentResult)
                        {
                            await _redisConn.ListRightPushAsync("payments-queue", msg, flags: StackExchange.Redis.CommandFlags.FireAndForget).ConfigureAwait(false);
                        }
                    }
                    await Task.Delay(10, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"ConsumerId: {consumerId} - Consumer error: {ex}");

                }
            }
        }
    }
}


