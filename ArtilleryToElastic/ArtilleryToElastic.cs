using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

class ArtilleryToElastic
{
    public static async Task UploadResult(ArtilleryResult artilleryResult, string serverurl, string username, string password, Dictionary<string, string> extraFields)
    {
        var allrequests = new List<ElasticBulkDocument>();

        foreach (JArray latency in artilleryResult.Latencies)
        {
            double secondsSince1970 = double.Parse(latency[0].Value<string>(), CultureInfo.InvariantCulture) / 1000;
            DateTime timestamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secondsSince1970);

            DateTime rebasedtime = timestamp.AddMilliseconds(artilleryResult.Diff_ms);

            string id = latency[1].Value<string>();
            long latency_ns = latency[2].Value<long>();
            long httpresult = latency[3].Value<long>();

            JObject jobject = new JObject
            {
                ["LoadtestID"] = artilleryResult.LoadtestID,
                ["@timestamp"] = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                ["LatencyNS"] = latency_ns,
                ["HttpResult"] = httpresult,
                ["RebasedTimestamp"] = rebasedtime.ToString("yyyy-MM-ddTHH:mm:ss.fff")
            };

            foreach (var field in extraFields)
            {
                jobject[field.Key] = field.Value;
            }

            var bulkDocument = new ElasticBulkDocument
            {
                Index = $"artillery-{timestamp:yyyy}.{timestamp:MM}",
                Id = id,
                Type = "doc",
                Document = jobject
            };

            allrequests.Add(bulkDocument);
        }

        Log($"Request count: {allrequests.Count}");

        await Elastic.PutIntoIndex(serverurl, username, password, allrequests.ToArray());
    }

    static void Log(string message)
    {
        Console.WriteLine(message);
    }
}
