using System.Data.SqlTypes;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Serialization;

namespace rinha_backend
{
    public class Models
    {
        public struct Payments
        {
            public Payments()
            {
            }

            //Modelo do banco
            public Guid CorrelationId { get; set; }
            public decimal Amount { get; set; }
            public bool IsDefault { get; set; } = true;
            public DateTimeOffset RequestedAt { get; set; }
        }
    }

    public class Requests
    {
        //Requests do cliente
        public struct PaymentsRequest
        {
            [JsonPropertyName("correlationId")]
            public Guid CorrelationId { get; set; }

            [JsonPropertyName("amount")]
            public decimal Amount { get; set; }
        }
    }

    public class Responses
    {
        //Responses da API e do PaymentsService
        public sealed record PaymentSummary
        {
            public bool IsDefault { get; set; }

            [JsonPropertyName("totalRequests")]
            public int TotalRequests { get; set; }

            [JsonPropertyName("totalAmount")]
            public decimal TotalAmount { get; set; }

            public PaymentSummary(bool isDefault, int totalRequests, decimal totalAmount)
            {
                IsDefault = isDefault;
                TotalRequests = totalRequests;
                TotalAmount = totalAmount;
            }
        }

        public sealed record PaymentItem
        {
            [JsonPropertyName("totalRequests")]
            public int TotalRequests { get; set; }

            [JsonPropertyName("totalAmount")]
            public decimal TotalAmount { get; set; }

            public PaymentItem(int totalRequests, decimal totalAmount)
            {
                TotalRequests = totalRequests;
                TotalAmount = totalAmount;
            }
        }
        public record PaymentsSummaryResponse
        {
            [JsonPropertyName("default")]
            public PaymentItem Default { get; set; }

            [JsonPropertyName("fallback")]
            public PaymentItem Fallback { get; set; }

            public PaymentsSummaryResponse(PaymentItem @default, PaymentItem fallback)
            {
                Default = @default;
                Fallback = fallback;
            }
        }

        public record PaymentServiceHealth
        {
            [JsonPropertyName("failing")]
            public bool IsFailing { get; set; }

            [JsonPropertyName("minResponseTime")]
            public decimal MinResponseTime { get; set; }

            public PaymentServiceHealth(bool isFailing, decimal minResponseTime)
            {
                IsFailing = isFailing;
                MinResponseTime = minResponseTime;
            }
        }
    }
}
