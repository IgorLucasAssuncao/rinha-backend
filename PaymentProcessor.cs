using Dapper;
using Org.BouncyCastle.Asn1;
using System.Text;
using System.Text.Json;
using static Org.BouncyCastle.Math.EC.ECCurve;
using static rinha_backend.Models;
using static rinha_backend.Requests;

namespace rinha_backend
{
    internal class PaymentProcessor
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly PaymentDecider _paymentDecider;
        const string insertSql = @"
                        INSERT INTO payments (CorrelationId, Amount, IsDefault, RequestedAt) 
                        VALUES (@CorrelationId, @Amount, @IsDefault, @RequestedAt)
                        ON CONFLICT (CorrelationId) DO NOTHING";


        public PaymentProcessor(IHttpClientFactory factory, PaymentDecider paymentDecider)
        {
            _httpClientFactory = factory;
            _paymentDecider = paymentDecider;
        }

        public async Task<bool> SendPayment(PaymentsRequest payment, string connectionPgSql)
        {
            var bestService = _paymentDecider.GetBestClient();
            var json = JsonSerializer.Serialize(payment);

            if (string.IsNullOrEmpty(bestService))
            {
                return false;
            }

            if (await TrySendPayment(bestService, json))
            {
                await using var conn = new Npgsql.NpgsqlConnection(connectionPgSql!);
                await conn.OpenAsync();

                var paymentDb = new Payments
                {
                    CorrelationId = payment.CorrelationId,
                    Amount = payment.Amount,
                    IsDefault = bestService == "default" ? true : false,
                    RequestedAt = DateTimeOffset.Parse(payment.RequestedAt)
                };

                await conn.ExecuteAsync(insertSql, paymentDb);
                return true;
            }

            return false; 
        }

        private async Task<bool> TrySendPayment(string serviceName, string payment)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(serviceName);
                var timeout = _paymentDecider.GetRecommendedTimeout(serviceName);

                var content = new StringContent(payment, Encoding.UTF8, "application/json");

                using var cts = new CancellationTokenSource(timeout);
                var response = await client.PostAsync("/payments", content, cts.Token);

                return response.IsSuccessStatusCode;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch
            { 
                return false;
            }
        }
    }
}