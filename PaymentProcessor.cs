using Dapper;
using Npgsql;
using System.Text;
using System.Text.Json;
using static rinha_backend.Models;
using static rinha_backend.Requests;

namespace rinha_backend
{
    internal class PaymentProcessor
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly PaymentDecider _paymentDecider;
        private readonly NpgsqlDataSource _dataSource;
        private readonly ILogger<PaymentProcessor> _logger;

        const string insertSql = @"
        INSERT INTO payments (CorrelationId, Amount, IsDefault, RequestedAt) 
        VALUES (@CorrelationId, @Amount, @IsDefault, @RequestedAt)
        ON CONFLICT (CorrelationId) DO NOTHING";

        public PaymentProcessor(IHttpClientFactory factory, PaymentDecider paymentDecider, NpgsqlDataSource dataSource, ILogger<PaymentProcessor> logger)
        {
            _httpClientFactory = factory;
            _paymentDecider = paymentDecider;
            _dataSource = dataSource;
            _logger = logger;
        }

        public async Task<bool> SendPayment(PaymentsRequest payment)  
        {
            var bestService = _paymentDecider.GetBestClient();

            if (string.IsNullOrEmpty(bestService))
                return false;

            var json = JsonSerializer.Serialize(payment);

            if (await TrySendPayment(bestService, json))
            {
                using var conn = _dataSource.CreateConnection();
                await conn.OpenAsync();

                var paymentDb = new Payments
                {
                    CorrelationId = payment.CorrelationId,
                    Amount = payment.Amount,
                    IsDefault = bestService == "default",  
                    RequestedAt = payment.RequestedAt
                };

                await conn.ExecuteAsync(insertSql, paymentDb);
                return true;
            }

            return false;
        }

        private async Task<bool> TrySendPayment(string serviceName, string jsonPayload)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(serviceName);
                var timeout = _paymentDecider.GetRecommendedTimeout(serviceName);

                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(timeout);

                var response = await client.PostAsync("/payments", content, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }
}