using System;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    static class Program
    {
        /// <summary>NetSpikeHarness [path to LlmMock.exe]</summary>
        static void Main(string[] args)
        {
            var config = new JObject();
            if (args.Length > 0) config["mockExe"] = args[0];
            Console.WriteLine(NetChecks.RunAll(config).GetAwaiter().GetResult().ToString());
        }
    }
}
