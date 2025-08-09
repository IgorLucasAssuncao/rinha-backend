using Dapper;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;
using System.Threading.Channels;
using static rinha_backend.Models;

namespace rinha_backend
{
    internal class HighThroughputRedisConsumer : BackgroundService
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<HighThroughputRedisConsumer> _logger;
        private readonly PaymentProcessor _processor;
        private readonly Channel<Payments> _channel;
        private readonly string _queueName;
        private readonly int _consumerCount;
        private readonly int _producerCount;
        private readonly int _batchSize;
        private readonly AdaptivePollingStrategy _pollingStrategy;
        private readonly IConfiguration _config;

        public HighThroughputRedisConsumer(
            IConnectionMultiplexer redis,
            ILogger<HighThroughputRedisConsumer> logger,
            PaymentProcessor processor,
            IOptions<RedisConsumerOptions> options,
            IConfiguration appConfig)
        {
            _redis = redis;
            _logger = logger;
            _processor = processor;

            _config = appConfig;

            var config = options.Value;
            _queueName = config.QueueName;
            _batchSize = config.BatchSize;
            _consumerCount = config.ConsumerCount > 0 ? config.ConsumerCount : Environment.ProcessorCount * 2;
            _producerCount = config.ProducerCount > 0 ? config.ProducerCount : Math.Max(2, Environment.ProcessorCount / 2);

            _channel = Channel.CreateBounded<Payments>(new BoundedChannelOptions(config.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

            _pollingStrategy = new AdaptivePollingStrategy(
                minDelay: TimeSpan.FromMilliseconds(1),
                maxDelay: TimeSpan.FromMilliseconds(100),
                initialDelay: TimeSpan.FromMilliseconds(10));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var producerTasks = new Task[_producerCount];
            for (int i = 0; i < _producerCount; i++)
                producerTasks[i] = ProduceAsync(i, stoppingToken);
            

            var consumerTasks = new Task[_consumerCount];
            for (int i = 0; i < _consumerCount; i++)
                consumerTasks[i] = ConsumeAsync(i, stoppingToken);
            

            var monitorTask = MonitorQueueAsync(stoppingToken);

            await Task.WhenAll(producerTasks.Concat(consumerTasks).Append(monitorTask).ToArray());
        }

        private async Task ProduceAsync(int producerId, CancellationToken stoppingToken)
        {
            var db = _redis.GetDatabase();
            var writer = _channel.Writer;
            int emptyBatchCount = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    int currentBatchSize = CalculateDynamicBatchSize(emptyBatchCount);

                    var luaScript = @"
                      local result = {}
                      for i = 1, @batchSize do
                          local item = redis.call('RPOP', @key)
                          if not item then break end
                          table.insert(result, item)
                      end
                      return result";

                    var prepared = LuaScript.Prepare(luaScript);
                    var values = (RedisValue[]?)await db.ScriptEvaluateAsync(
                        prepared,
                        new { key = (RedisKey)_queueName, batchSize = currentBatchSize }
                    );

                    if (values != null && values.Length > 0)
                    {
                        emptyBatchCount = 0; 

                        foreach (var value in values)
                        {
                            if (!value.IsNullOrEmpty)
                            {
                                var message = JsonSerializer.Deserialize<Payments>(value.ToString(), AppJsonContext.Default.Payments);
                                await writer.WriteAsync(message, stoppingToken);
                            }
                        }
                    }
                    else
                    {
                        emptyBatchCount++;
                        TimeSpan delay = _pollingStrategy.GetNextDelay(emptyBatchCount);
                        await Task.Delay(delay, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro no produtor {ProducerId}", producerId);
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }

        private async Task ConsumeAsync(int consumerId, CancellationToken stoppingToken)
        {
            var reader = _channel.Reader;

            await foreach (var message in reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var db = _redis.GetDatabase();

                    var paymentResult = await _processor.SendPayment(message);

                    if (!paymentResult.Item1)
                    {
                        _logger.LogInformation("Reenfileirando pagamento: {CorrelationId}, Amount: {Amount}", message.CorrelationId, message.Amount);
                        await db.ListRightPushAsync((RedisKey)_queueName, (RedisValue)JsonSerializer.Serialize(message, AppJsonContext.Default.Payments));
                    }
                    else
                    {
                        await using var conn = new Npgsql.NpgsqlConnection(_config.GetConnectionString("postgres")!);
                        await conn.OpenAsync();

                        const string insertSql = @"
                        INSERT INTO payments (CorrelationId, Amount, IsDefault, RequestedAt) 
                        VALUES (@CorrelationId, @Amount, @IsDefault, @RequestedAt)
                        ON CONFLICT (CorrelationId) DO NOTHING";

                        var payment = new Payments
                        {
                            CorrelationId = message.CorrelationId,
                            Amount = message.Amount,
                            IsDefault = paymentResult.Item2 == "default"? true : false, 
                            RequestedAt = DateTimeOffset.Now
                        };

                        await conn.ExecuteAsync(insertSql, payment);

                        _logger.LogInformation("Payment inserido: {CorrelationId}, Amount: {Amount}",
                            payment.CorrelationId, payment.Amount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao processar mensagem no consumidor {ConsumerId}", consumerId);
                }
            }
        }

        private async Task MonitorQueueAsync(CancellationToken stoppingToken)
        {
            var db = _redis.GetDatabase();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    long queueLength = await db.ListLengthAsync(_queueName);
                    int channelItems = _channel.Reader.Count;

                    _logger.LogInformation(
                        "Status da fila: {QueueLength} itens no Redis, {ChannelItems} itens no channel",
                        queueLength, channelItems);

                    await Task.Delay(10000, stoppingToken); 
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao monitorar fila");
                    await Task.Delay(30000, stoppingToken);
                }
            }
        }

        private int CalculateDynamicBatchSize(int emptyBatchCount)
        {
            if (emptyBatchCount > 10)
                return _batchSize / 4; 
            if (emptyBatchCount > 5)
                return _batchSize / 2;
            if (emptyBatchCount == 0)
                return _batchSize * 2; 

            return _batchSize;
        }
    }

    public class AdaptivePollingStrategy
    {
        private readonly TimeSpan _minDelay;
        private readonly TimeSpan _maxDelay;
        private TimeSpan _currentDelay;

        public AdaptivePollingStrategy(TimeSpan minDelay, TimeSpan maxDelay, TimeSpan initialDelay)
        {
            _minDelay = minDelay;
            _maxDelay = maxDelay;
            _currentDelay = initialDelay;
        }

        public TimeSpan GetNextDelay(int emptyBatchCount)
        {
            if (emptyBatchCount == 0)
            {
                _currentDelay = _minDelay;
            }
            else
            { 
                _currentDelay = TimeSpan.FromMilliseconds(
                    Math.Min(
                        _maxDelay.TotalMilliseconds,
                        _currentDelay.TotalMilliseconds * 1.5
                    )
                );
            }

            return _currentDelay;
        }
    }

    public class RedisConsumerOptions
    {
        public string QueueName { get; set; } = "payments-queue";

        //Define o número de pagamentos por Channel
        public int BatchSize { get; set; } = 50; 

        public int ChannelCapacity { get; set; } = 5000;
        public int ConsumerCount { get; set; } = 100; // 0 = automático
        public int ProducerCount { get; set; } = 10; // 0 = automático
    }
}


