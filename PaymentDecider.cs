using System.Collections.Concurrent;
using static rinha_backend.Responses;

public class PaymentDecider
{
    private readonly ConcurrentDictionary<string, PaymentServiceHealth> _services = new();
    private volatile string _bestService = "default";

    public void DefineDefaultStatus(PaymentServiceHealth health)
    {
        _services.AddOrUpdate("default", health, (key, old) => health);
        UpdateBestService();
    }

    public void DefineFallbackStatus(PaymentServiceHealth health)
    {
        _services.AddOrUpdate("fallback", health, (key, old) => health);
        UpdateBestService();
    }

    public string GetBestClient() => _bestService;

    private void UpdateBestService()
    {
        var defaultHealth = _services.GetValueOrDefault("default");
        var fallbackHealth = _services.GetValueOrDefault("fallback");

        var defaultOk = defaultHealth?.IsFailing == false;
        var fallbackOk = fallbackHealth?.IsFailing == false;

        if (!defaultOk && !fallbackOk)
        {
            _bestService = ""; 
            return;
        }

        if (defaultOk && !fallbackOk)
        {
            _bestService = "default";
            return;
        }

        if (!defaultOk && fallbackOk)
        {
            _bestService = "fallback";
            return;
        }

        _bestService = defaultHealth!.MinResponseTime <= fallbackHealth!.MinResponseTime
            ? "default"
            : "fallback";
    }

    public int GetRecommendedTimeout(string serviceName)
    {
        var health = _services.GetValueOrDefault(serviceName);
        return (int)(health?.MinResponseTime + 1000 ?? 5000);
    }

    public PaymentServiceHealth? GetServiceStatus(string serviceName)
    {
        return _services.GetValueOrDefault(serviceName);
    }
}