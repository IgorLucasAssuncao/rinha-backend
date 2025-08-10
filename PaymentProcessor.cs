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

        public PaymentProcessor(IHttpClientFactory factory, PaymentDecider paymentDecider)
        {
            _httpClientFactory = factory;
            _paymentDecider = paymentDecider;
        }

        public async Task<(bool, string)> SendPayment(PaymentsRequest payment)
        {
            var bestService = _paymentDecider.GetBestClient();

            if (string.IsNullOrEmpty(bestService))
            {
                return (false, ""); 
            }

            if (await TrySendPayment(bestService, payment))
                return (true, bestService);

            var fallbackService = bestService == "default" ? "fallback" : "default";
            var fallbackStatus = _paymentDecider.GetServiceStatus(fallbackService);

            if (fallbackStatus?.IsFailing == false)
            {
                var result = await TrySendPayment(fallbackService, payment);
                return (result, fallbackService);
            }

            return (false, ""); 
        }

        private async Task<bool> TrySendPayment(string serviceName, PaymentsRequest payment)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(serviceName);
                var timeout = _paymentDecider.GetRecommendedTimeout(serviceName);

                var payload = new PaymentsRequestDTO(
                
                    correlationId: payment.CorrelationId,
                    amount: payment.Amount,
                    requestedAt: payment.RequestedAt
                );

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

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