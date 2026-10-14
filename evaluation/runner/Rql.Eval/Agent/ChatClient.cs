using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Rql.Eval.Agent
{
    /// <summary>Provider-reported usage of one request (DeepSeek/OpenAI-compatible fields).</summary>
    public sealed class Usage
    {
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public long CacheHitTokens { get; set; }
        public long CacheMissTokens { get; set; }
        public long ReasoningTokens { get; set; }
        public double CostUsd { get; set; }
        public bool Peak { get; set; }

        public void Add(Usage u)
        {
            PromptTokens += u.PromptTokens;
            CompletionTokens += u.CompletionTokens;
            CacheHitTokens += u.CacheHitTokens;
            CacheMissTokens += u.CacheMissTokens;
            ReasoningTokens += u.ReasoningTokens;
            CostUsd += u.CostUsd;
        }
    }

    public sealed class ChatResponse
    {
        public JsonObject Message { get; set; } = new JsonObject();
        public string? FinishReason { get; set; }
        public Usage Usage { get; set; } = new Usage();
        public double LatencyMs { get; set; }
        public int Retries { get; set; }
    }

    /// <summary>Versioned price snapshot (USD per 1M tokens). Peak/off-peak per provider schedule.</summary>
    public sealed class PriceTable
    {
        public string Source { get; set; } = "";
        public string RetrievedAt { get; set; } = "";
        public Dictionary<string, ModelPrice> Models { get; set; } = new Dictionary<string, ModelPrice>();

        public sealed class ModelPrice
        {
            public double PeakHit { get; set; }
            public double PeakMiss { get; set; }
            public double PeakOutput { get; set; }
            public double OffPeakFactor { get; set; } = 0.5;
        }

        /// <summary>Peak: 01:00–04:00 and 06:00–10:00 UTC, Monday–Friday (Chinese public holidays not modelled → conservative).</summary>
        public static bool IsPeak(DateTime utc)
        {
            if (utc.DayOfWeek == DayOfWeek.Saturday || utc.DayOfWeek == DayOfWeek.Sunday) return false;
            int h = utc.Hour;
            return (h >= 1 && h < 4) || (h >= 6 && h < 10);
        }

        public double Cost(string model, Usage u, DateTime utc, out bool peak)
        {
            peak = IsPeak(utc);
            if (!Models.TryGetValue(model, out var p)) throw new InvalidOperationException($"no price for model '{model}' in price table");
            double f = peak ? 1 : p.OffPeakFactor;
            return f * (u.CacheHitTokens * p.PeakHit + u.CacheMissTokens * p.PeakMiss + u.CompletionTokens * p.PeakOutput) / 1_000_000.0;
        }
    }

    // A copy of the request / pricing / redaction logic lives in unity/RqlTestProject/Assets/Editor/Eval/DeepSeekClient.cs
    // (Unity cannot reference this net10.0 project): keep both in sync.
    public sealed class ChatClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly PriceTable _prices;

        public ChatClient(string baseUrl, string apiKey, PriceTable prices, TimeSpan timeout)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _prices = prices;
            _http = new HttpClient { Timeout = timeout };
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        public static string LoadApiKey()
        {
            var k = Environment.GetEnvironmentVariable("RQL_EVAL_API_KEY");
            if (string.IsNullOrWhiteSpace(k))
            {
                var file = Environment.GetEnvironmentVariable("RQL_EVAL_API_KEY_FILE");
                if (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) k = File.ReadAllText(file).Trim();
            }
            if (string.IsNullOrWhiteSpace(k)) throw new InvalidOperationException("RQL_EVAL_API_KEY / RQL_EVAL_API_KEY_FILE not set — Tier B evaluation cannot run");
            return k!;
        }

        public async Task<ChatResponse> CompleteAsync(JsonObject request, string model, CancellationToken ct)
        {
            var body = request.ToJsonString();
            int retries = 0;
            var sw = Stopwatch.StartNew();
            while (true)
            {
                HttpResponseMessage? resp = null;
                string? text = null;
                Exception? error = null;
                try
                {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await _http.PostAsync(_baseUrl + "/chat/completions", content, ct).ConfigureAwait(false);
                    text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    error = ex;
                }
                bool transient = error != null || resp!.StatusCode == (HttpStatusCode)429 || (int)resp.StatusCode >= 500;
                if (transient && retries < 4)
                {
                    retries++;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retries)), ct).ConfigureAwait(false);
                    continue;
                }
                if (error != null) throw new InvalidOperationException("provider request failed: " + error.Message);
                if (!resp!.IsSuccessStatusCode)
                    throw new InvalidOperationException($"provider returned {(int)resp.StatusCode}: {Redact(text ?? "")}");
                var json = JsonNode.Parse(text!)!.AsObject();
                var choice = json["choices"]![0]!.AsObject();
                var usage = json["usage"]?.AsObject();
                var u = new Usage();
                if (usage != null)
                {
                    u.PromptTokens = usage["prompt_tokens"]?.GetValue<long>() ?? 0;
                    u.CompletionTokens = usage["completion_tokens"]?.GetValue<long>() ?? 0;
                    u.CacheHitTokens = usage["prompt_cache_hit_tokens"]?.GetValue<long>() ?? 0;
                    u.CacheMissTokens = usage["prompt_cache_miss_tokens"]?.GetValue<long>() ?? (u.PromptTokens - u.CacheHitTokens);
                    u.ReasoningTokens = usage["completion_tokens_details"]?["reasoning_tokens"]?.GetValue<long>() ?? 0;
                }
                u.CostUsd = _prices.Cost(model, u, DateTime.UtcNow, out var peak);
                u.Peak = peak;
                return new ChatResponse
                {
                    Message = (JsonObject)choice["message"]!.DeepClone(),
                    FinishReason = choice["finish_reason"]?.GetValue<string>(),
                    Usage = u,
                    LatencyMs = sw.Elapsed.TotalMilliseconds,
                    Retries = retries,
                };
            }
        }

        /// <summary>Defensive: strip anything that looks like a bearer token or sk- key from provider text.</summary>
        public static string Redact(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, @"(sk-[A-Za-z0-9]{6})[A-Za-z0-9]+|(Bearer\s+)\S+", m => m.Groups[1].Success ? m.Groups[1].Value + "…" : m.Groups[2].Value + "…");

        public void Dispose() => _http.Dispose();
    }
}
