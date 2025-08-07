
using Org.BouncyCastle.Asn1.Ocsp;
using System.Text.Json;
using static rinha_backend.Responses;

namespace rinha_backend
{
    public class PaymentVerifier : BackgroundService
    {
        private readonly HttpClient _default;
        private readonly HttpClient _fallback;
        private readonly PaymentDecider _paymentDecider;

        public PaymentVerifier(IHttpClientFactory factory, PaymentDecider paymentDecider)
        {
            _default = factory.CreateClient("default");
            _fallback = factory.CreateClient("fallback");
            _paymentDecider = paymentDecider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var checkDefault = await CheckService(_default);

                _paymentDecider.DefineDefaultStatus(checkDefault);

                var checkFallback = await CheckService(_fallback);

                _paymentDecider.DefineFallbackStatus(checkFallback);

                await Task.Delay(5100, stoppingToken);
            }
        }

        private async Task<PaymentServiceHealth> CheckService(HttpClient client)
        {
            try
            {
                var response = await client.GetAsync("/payments/service-health");

                if (!response.IsSuccessStatusCode)
                    return new PaymentServiceHealth(true, 100000);

                var content = await response.Content.ReadAsStringAsync();

                return JsonSerializer.Deserialize<PaymentServiceHealth>(content)!;
            }
            catch
            {
                return new PaymentServiceHealth(false, 1000000);
            }
        }
    }
}
