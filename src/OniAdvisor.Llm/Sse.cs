using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OniAdvisor.Llm
{
    /// <summary>Reads a server-sent-event stream: calls back with each event's "data:" payload.</summary>
    internal static class Sse
    {
        public static async Task Read(HttpResponseMessage res, Action<string, string> onEvent, CancellationToken ct)
        {
            // A pending read doesn't watch the token: closing the response on cancel ends it at once.
            using (ct.Register(() => res.Dispose()))
            using (var s = await res.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(s, Encoding.UTF8))
            {
                string evt = null;
                var data = new StringBuilder();
                string line;
                while (true)
                {
                    try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
                    catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                    if (line == null) break;
                    if (line.Length == 0)
                    {
                        if (data.Length > 0) onEvent(evt, data.ToString());
                        evt = null; data.Clear();
                        continue;
                    }
                    if (line.StartsWith(":")) continue; // comment / keep-alive
                    if (line.StartsWith("event:")) evt = line.Substring(6).Trim();
                    else if (line.StartsWith("data:"))
                    {
                        if (data.Length > 0) data.Append('\n');
                        data.Append(line.Substring(5).TrimStart());
                    }
                }
                if (data.Length > 0) onEvent(evt, data.ToString());
            }
            ct.ThrowIfCancellationRequested();
        }
    }
}
