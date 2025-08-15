using Dapper;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using static rinha_backend.Models;
using static rinha_backend.Requests;

namespace rinha_backend
{
    internal class HighThroughputRedisConsumer : BackgroundService
    {
        private readonly ILogger<HighThroughputRedisConsumer> _logger;
        private readonly PaymentProcessor _processor;
        private readonly int _consumerCount;
        private readonly ChannelWriter<PaymentsRequest> _writer;
        private readonly ChannelReader<PaymentsRequest> _reader;

        public HighThroughputRedisConsumer(
            ILogger<HighThroughputRedisConsumer> logger,
            PaymentProcessor processor,
            IOptions<RedisConsumerOptions> options,
            ChannelWriter<PaymentsRequest> writer,
            ChannelReader<PaymentsRequest> reader
            )
        {
            _writer = writer;
            _reader = reader;

            _logger = logger;
            _processor = processor;

             var config = options.Value;
            _consumerCount = config.ConsumerCount > 0 ? config.ConsumerCount : Environment.ProcessorCount * 2;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var consumerTasks = new Task[_consumerCount];
            for (int i = 0; i < _consumerCount; i++)
                consumerTasks[i] = ConsumeAsync(i, stoppingToken);

            await Task.WhenAll(consumerTasks.ToArray());
        }

        private async Task ConsumeAsync(int consumerId, CancellationToken stoppingToken)
        {
            await foreach (var message in _reader.ReadAllAsync(stoppingToken))
            {

                var paymentResult = await _processor.SendPayment(message);

                if (!paymentResult)
                {
                    await Task.Yield();
                    await _writer.WriteAsync(message, stoppingToken);
                }
            }
        }
    }

    public class RedisConsumerOptions
    {
        public int ConsumerCount { get; set; } = 1; 
    }
}


