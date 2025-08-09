using System.Data.SqlTypes;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Serialization;

namespace rinha_backend
{
    internal class Models
    {
        internal struct Payments
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

        internal struct PaymentsRequestDTO
        {
            public Guid correlationId { get; set; }
            public decimal amount { get; set; }
            public string requestedAt { get; set; }

            public PaymentsRequestDTO(Guid correlationId, decimal amount, string requestedAt)
            {
                this.correlationId = correlationId;
                this.amount = amount;
                this.requestedAt = requestedAt;
            }
        }
    }

    internal class Requests
    {
        //Requests do cliente
        internal struct PaymentsRequest
        {
            [JsonPropertyName("correlationId")]
            public Guid CorrelationId { get; set; }

            [JsonPropertyName("amount")]
            public decimal Amount { get; set; }
        }
    }

    internal class Responses
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

        internal sealed record PaymentItem
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
        internal record PaymentsSummaryResponse
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

        internal record PaymentServiceHealth
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
