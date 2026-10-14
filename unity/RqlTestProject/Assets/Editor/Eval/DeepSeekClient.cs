#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace RqlTest.Eval
{
    /// <summary>Provider usage of one request (DeepSeek / OpenAI-compatible fields) and its cost.</summary>
    public sealed class Usage
    {
        public long PromptTokens, CompletionTokens, CacheHitTokens, CacheMissTokens;
        public double CostUsd;

        public void Add(Usage u)
        {
            PromptTokens += u.PromptTokens; CompletionTokens += u.CompletionTokens;
            CacheHitTokens += u.CacheHitTokens; CacheMissTokens += u.CacheMissTokens; CostUsd += u.CostUsd;
        }

        public JsonObject ToJson() => new JsonObject
        {
            ["promptTokens"] = PromptTokens, ["completionTokens"] = CompletionTokens,
            ["cacheHitTokens"] = CacheHitTokens, ["cacheMissTokens"] = CacheMissTokens, ["costUsd"] = CostUsd,
        };
    }

    /// <summary>
    /// Same request shape, price table and redaction as the RQL evaluation runner (evaluation/runner/Rql.Eval): the key comes
    /// from RQL_EVAL_API_KEY_FILE and is never logged.
    /// </summary>
    public sealed class DeepSeekClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly double _hit, _miss, _output, _offPeak;

        public DeepSeekClient(string baseUrl, string apiKey, JsonObject modelPrices, TimeSpan timeout)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { Timeout = timeout };
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            _hit = modelPrices["peakHit"]!.GetValue<double>();
            _miss = modelPrices["peakMiss"]!.GetValue<double>();
            _output = modelPrices["peakOutput"]!.GetValue<double>();
            _offPeak = modelPrices["offPeakFactor"]?.GetValue<double>() ?? 0.5;
        }

        public static string LoadApiKey()
        {
            var file = Environment.GetEnvironmentVariable("RQL_EVAL_API_KEY_FILE");
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) throw new InvalidOperationException("RQL_EVAL_API_KEY_FILE is not set or missing");
            var key = File.ReadAllText(file).Trim();
            if (key.Length == 0) throw new InvalidOperationException("API key file is empty");
            return key;
        }

        /// <summary>Peak: 01:00–04:00 and 06:00–10:00 UTC, Monday–Friday (as in the main runner).</summary>
        private static bool IsPeak(DateTime utc)
        {
            if (utc.DayOfWeek == DayOfWeek.Saturday || utc.DayOfWeek == DayOfWeek.Sunday) return false;
            int h = utc.Hour;
            return (h >= 1 && h < 4) || (h >= 6 && h < 10);
        }

        public async Task<(JsonObject message, string? finish, Usage usage, double latencyMs)> CompleteAsync(JsonObject request)
        {
            var body = request.ToJsonString();
            var sw = Stopwatch.StartNew();
            for (int attempt = 0; ; attempt++)
            {
                string? text = null;
                HttpResponseMessage? resp = null;
                Exception? error = null;
                try
                {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await _http.PostAsync(_baseUrl + "/chat/completions", content);
                    text = await resp.Content.ReadAsStringAsync();
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException) { error = ex; }
                bool transient = error != null || resp!.StatusCode == (HttpStatusCode)429 || (int)resp.StatusCode >= 500;
                if (transient && attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
                    continue;
                }
                if (error != null) throw new InvalidOperationException("provider request failed: " + error.Message);
                if (!resp!.IsSuccessStatusCode) throw new InvalidOperationException($"provider returned {(int)resp.StatusCode}: {Redact(text ?? "")}");
                var json = JsonNode.Parse(text!)!.AsObject();
                var choice = json["choices"]![0]!.AsObject();
                var u = new Usage();
                if (json["usage"] is JsonObject usage)
                {
                    u.PromptTokens = usage["prompt_tokens"]?.GetValue<long>() ?? 0;
                    u.CompletionTokens = usage["completion_tokens"]?.GetValue<long>() ?? 0;
                    u.CacheHitTokens = usage["prompt_cache_hit_tokens"]?.GetValue<long>() ?? 0;
                    u.CacheMissTokens = usage["prompt_cache_miss_tokens"]?.GetValue<long>() ?? (u.PromptTokens - u.CacheHitTokens);
                }
                double f = IsPeak(DateTime.UtcNow) ? 1 : _offPeak;
                u.CostUsd = f * (u.CacheHitTokens * _hit + u.CacheMissTokens * _miss + u.CompletionTokens * _output) / 1_000_000.0;
                return ((JsonObject)choice["message"]!.DeepClone(), choice["finish_reason"]?.GetValue<string>(), u, sw.Elapsed.TotalMilliseconds);
            }
        }

        public static string Redact(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, @"(sk-[A-Za-z0-9]{6})[A-Za-z0-9]+|(Bearer\s+)\S+", m => m.Groups[1].Success ? m.Groups[1].Value + "…" : m.Groups[2].Value + "…");

        public void Dispose() => _http.Dispose();
    }
}
