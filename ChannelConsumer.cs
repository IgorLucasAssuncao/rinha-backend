using Dapper;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System;
using System.Text.Json;
using System.Threading.Channels;
using static rinha_backend.Models;
using static rinha_backend.Requests;

namespace rinha_backend
{
    internal class HighThroughputRedisConsumer : BackgroundService
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<HighThroughputRedisConsumer> _logger;
        private readonly PaymentProcessor _processor;
        private readonly Channel<PaymentsRequest> _channel;
        private readonly string _queueName;
        private readonly int _consumerCount;
        private readonly int _producerCount;
        private readonly int _batchSize;
        private readonly AdaptivePollingStrategy _pollingStrategy;
        private readonly IConfiguration _config;

        const string insertSql = @"
                        INSERT INTO payments (CorrelationId, Amount, IsDefault, RequestedAt) 
                        VALUES (@CorrelationId, @Amount, @IsDefault, @RequestedAt)
                        ON CONFLICT (CorrelationId) DO NOTHING";

        const string luaScript = @"
                      local result = {}
                      for i = 1, @batchSize do
                          local item = redis.call('RPOP', @key)
                          if not item then break end
                          table.insert(result, item)
                      end
                      return result";

        private readonly LuaScript _preparedScript;

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
            _preparedScript = LuaScript.Prepare(luaScript);

            var config = options.Value;
            _queueName = config.QueueName;
            _batchSize = config.BatchSize;
            _consumerCount = config.ConsumerCount > 0 ? config.ConsumerCount : Environment.ProcessorCount * 2;
            _producerCount = config.ProducerCount > 0 ? config.ProducerCount : Math.Max(2, Environment.ProcessorCount / 2);

            _channel = Channel.CreateBounded<PaymentsRequest>(new BoundedChannelOptions(config.ChannelCapacity)
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
            

            //var monitorTask = MonitorQueueAsync(stoppingToken);

            await Task.WhenAll(producerTasks.Concat(consumerTasks)/*.Append(monitorTask)*/.ToArray());
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

                    var values = (RedisValue[]?)await db.ScriptEvaluateAsync(
                        _preparedScript,
                        new { key = (RedisKey)_queueName, batchSize = currentBatchSize }
                    );

                    if (values != null && values.Length > 0)
                    {
                        emptyBatchCount = 0; 

                        foreach (var value in values)
                        {
                            if (!value.IsNullOrEmpty)
                            {
                                var message = JsonSerializer.Deserialize<PaymentsRequest>(value.ToString())!;
                                _logger.LogInformation($"{message.Amount} - {message.CorrelationId} - {message.RequestedAt}");
                                await writer.WriteAsync(message, stoppingToken);
                            }
                        }
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
            var db = _redis.GetDatabase();

            await foreach (var message in reader.ReadAllAsync(stoppingToken))
            {
                (bool, string) paymentResult = default;
                try
                {
                    var json = JsonSerializer.Serialize(message);
                    paymentResult = await _processor.SendPayment(json);

                    if (!paymentResult.Item1)
                    {
                        _ = db.ListRightPushAsync((RedisKey)_queueName, (RedisValue)JsonSerializer.Serialize(message));
                    }
                    else
                    {
                        await using var conn = new Npgsql.NpgsqlConnection(_config.GetConnectionString("postgres")!);
                        await conn.OpenAsync();

                        var payment = new Payments
                        {
                            CorrelationId = message.CorrelationId,
                            Amount = message.Amount,
                            IsDefault = paymentResult.Item2 == "default" ? true : false,
                            RequestedAt = DateTimeOffset.Parse(message.RequestedAt)
                        };

                        await conn.ExecuteAsync(insertSql, payment);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao processar mensagem no consumidor {ConsumerId}", consumerId);
                    _logger.LogError($"{paymentResult.Item1} - {paymentResult.Item2}");
                }
            }
        }

        private async Task MonitorQueueAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Monitor de fila e sistema iniciado");
            var db = _redis.GetDatabase();
            var startTime = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // ✅ Métricas da fila (original)
                    long queueLength = await db.ListLengthAsync(_queueName);
                    int channelItems = _channel.Reader.Count;

                    // ✅ Métricas de ThreadPool
                    ThreadPool.GetAvailableThreads(out int availableWorker, out int availableIO);
                    ThreadPool.GetMaxThreads(out int maxWorker, out int maxIO);
                    ThreadPool.GetMinThreads(out int minWorker, out int minIO);
                    var usedWorker = maxWorker - availableWorker;
                    var usedIO = maxIO - availableIO;

                    // ✅ Métricas do processo
                    var process = System.Diagnostics.Process.GetCurrentProcess();
                    var processThreads = process.Threads.Count;
                    var workingSet = process.WorkingSet64 / 1024 / 1024; // MB
                    var cpuTime = process.TotalProcessorTime.TotalSeconds;

                    // ✅ Métricas de GC
                    var gen0Collections = GC.CollectionCount(0);
                    var gen1Collections = GC.CollectionCount(1);
                    var gen2Collections = GC.CollectionCount(2);
                    var totalMemory = GC.GetTotalMemory(false) / 1024 / 1024; // MB

                    // ✅ Contagem aproximada de tasks ativas
                    var activeTasks = _producerCount + _consumerCount + 1; // Producers + Consumers + Monitor

                    // ✅ Uptime
                    var uptime = DateTime.UtcNow - startTime;

                    // ✅ Log expandido
                    _logger.LogInformation(
                        "📊 SYSTEM MONITOR | " +
                        "Uptime: {Uptime:hh\\:mm\\:ss} | " +
                        "Queue: Redis:{QueueLength} Channel:{ChannelItems}/{ChannelCapacity} | " +
                        "Tasks: {ActiveTasks} active | " +
                        "ThreadPool: Worker:{UsedWorker}/{MaxWorker} (min:{MinWorker}) IO:{UsedIO}/{MaxIO} (min:{MinIO}) | " +
                        "Process: {ProcessThreads} threads | " +
                        "Memory: Working:{WorkingSet}MB GC:{TotalMemory}MB | " +
                        "GC Collections: G0:{Gen0} G1:{Gen1} G2:{Gen2} | " +
                        "CPU Time: {CpuTime:F1}s",

                        uptime,
                        queueLength, channelItems, 15000, // assumindo capacidade padrão
                        activeTasks,
                        usedWorker, maxWorker, minWorker, usedIO, maxIO, minIO,
                        processThreads,
                        workingSet, totalMemory,
                        gen0Collections, gen1Collections, gen2Collections,
                        cpuTime);

                    // ✅ Warnings automáticos
                    if (queueLength > 1000)
                        _logger.LogWarning("⚠️ Fila Redis crescendo: {QueueLength} itens", queueLength);

                    if (channelItems > 12000) // 80% da capacidade padrão
                        _logger.LogWarning("⚠️ Channel próximo da capacidade: {ChannelItems} itens", channelItems);

                    if (usedWorker > maxWorker * 0.8)
                        _logger.LogWarning("⚠️ Alta utilização de worker threads: {UsedWorker}/{MaxWorker} ({Percentage:F1}%)",
                            usedWorker, maxWorker, (usedWorker * 100.0 / maxWorker));

                    if (workingSet > 150) // 150MB
                        _logger.LogWarning("⚠️ Alto consumo de memória: {WorkingSet}MB", workingSet);

                    if (gen2Collections > 0 && uptime.TotalMinutes > 1) // GC Gen2 após 1min de uptime
                        _logger.LogWarning("⚠️ GC Gen2 collections detectadas: {Gen2Collections}", gen2Collections);

                    // ✅ Log adicional de performance a cada 1 minuto
                    if (uptime.TotalSeconds % 60 < 10) // aproximadamente a cada minuto
                    {
                        var avgCpuPerSecond = cpuTime / uptime.TotalSeconds;
                        _logger.LogInformation(
                            "📈 Performance Summary | " +
                            "Avg CPU: {AvgCpu:F2}s/s | " +
                            "Throughput: Redis reads, Channel writes | " +
                            "Memory trend: {MemoryTrend}MB working set",
                            avgCpuPerSecond, workingSet);
                    }

                    await Task.Delay(1000, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao monitorar fila e sistema");
                    await Task.Delay(1000, stoppingToken);
                }
            }

            _logger.LogInformation("Monitor de fila e sistema finalizado");
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
        public int BatchSize { get; set; } = 15;
        public int ChannelCapacity { get; set; } = 5500;
        public int ConsumerCount { get; set; } = 3; 
        public int ProducerCount { get; set; } = 1; 
    }
}


