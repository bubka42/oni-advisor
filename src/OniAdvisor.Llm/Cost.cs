using System;
using System.Collections.Generic;

namespace OniAdvisor.Llm
{
    /// <summary>US dollars per million tokens.</summary>
    public sealed class Price
    {
        public double Input, Output, CacheWrite, CacheRead;
        public Price(double input, double output, double? cacheWrite = null, double? cacheRead = null)
        {
            Input = input; Output = output;
            CacheWrite = cacheWrite ?? input * 1.25; // Anthropic 5-minute cache writes; providers with free caching pass 0
            CacheRead = cacheRead ?? input * 0.1;
        }

        public double Cost(Usage u) =>
            (u.InputTokens * Input + u.OutputTokens * Output + u.CacheWriteTokens * CacheWrite + u.CacheReadTokens * CacheRead) / 1_000_000.0;
    }

    /// <summary>
    /// Known prices, overridable from settings. Only Anthropic's are built in (Anthropic first-party API rates,
    /// October 2026; Claude Haiku 5.5's rate is for prompts up to 100K tokens). Other providers change prices
    /// often and differ by account, so the player enters them; unknown models report tokens but no cost.
    /// </summary>
    public static class Prices
    {
        static readonly Dictionary<string, Price> known = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-fable-5-1"] = new Price(10, 50, cacheRead: 0.25),
            ["claude-opus-5-5"] = new Price(4, 20, cacheRead: 0.20),
            ["claude-sonnet-5-5"] = new Price(2, 10, cacheRead: 0.20),
            ["claude-haiku-5-5"] = new Price(0.10, 0.50),
        };

        public static void Set(string model, Price price) => known[model] = price;

        public static Price For(string model) => model != null && known.TryGetValue(model, out var p) ? p : null;
    }

    public sealed class BudgetExceededException : Exception
    {
        public BudgetExceededException(double spent, double limit)
            : base($"The advisor's spending limit for this session is reached (${spent:0.00} of ${limit:0.00}).") { }
    }

    /// <summary>What the session has used and cost so far, with an optional limit that stops further requests.</summary>
    public sealed class CostTracker
    {
        readonly object gate = new object();
        public readonly Usage Total = new Usage();
        public double Dollars { get; private set; }
        /// <summary>Requests to models without a known price: their cost is missing from Dollars.</summary>
        public int Unpriced { get; private set; }
        /// <summary>0 = no limit.</summary>
        public double LimitDollars;

        public void Add(string model, Usage usage)
        {
            lock (gate)
            {
                Total.Add(usage);
                var price = Prices.For(model);
                if (price != null) Dollars += price.Cost(usage);
                else Unpriced++;
            }
        }

        public void CheckBudget()
        {
            if (LimitDollars > 0 && Dollars >= LimitDollars) throw new BudgetExceededException(Dollars, LimitDollars);
        }

        public override string ToString() => Unpriced > 0 && Dollars == 0 ? Total.ToString() : $"${Dollars:0.000} ({Total})" + (Unpriced > 0 ? " + unpriced requests" : "");
    }
}
