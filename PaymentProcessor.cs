using System.Text;
using System.Text.Json;
using static rinha_backend.Models;

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

        public async Task<bool> SendPayment(Payments payment)
        {
            var bestService = _paymentDecider.GetBestClient();

            if (string.IsNullOrEmpty(bestService))
            {
                return false; 
            }

            if (await TrySendPayment(bestService, payment))
                return true;

            var fallbackService = bestService == "default" ? "fallback" : "default";
            var fallbackStatus = _paymentDecider.GetServiceStatus(fallbackService);

            if (fallbackStatus?.IsFailing == false)
            {
                return await TrySendPayment(fallbackService, payment);
            }

            return false; 
        }

        private async Task<bool> TrySendPayment(string serviceName, Payments payment)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(serviceName);
                var timeout = _paymentDecider.GetRecommendedTimeout(serviceName);

                var payload = new PaymentsRequestDTO(
                
                    correlationId: payment.CorrelationId,
                    amount: payment.Amount,
                    requestedAt: payment.RequestedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                );

                var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.Payments);
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