using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

class ArtilleryToElastic
{
    public static async Task UploadResult(ArtilleryResult artilleryResult, string serverurl, string username, string password, Dictionary<string, string> extraFields)
    {
        var allrequests = new List<ElasticBulkDocument>();

        foreach (var request in artilleryResult.Requests)
        {
            dynamic jobject = new JObject
            {
                ["LoadtestID"] = artilleryResult.LoadtestID,
                ["@timestamp"] = request.StartTime.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                ["LatencyNS"] = request.Latency,
                ["HttpResult"] = request.HttpResult,
                ["RebasedTimestamp"] = request.StartTime.AddMilliseconds(artilleryResult.Diff_ms).ToString("yyyy-MM-ddTHH:mm:ss.fff")
            };

            foreach (var field in extraFields)
            {
                jobject[field.Key] = field.Value;
            }

            var bulkDocument = new ElasticBulkDocument
            {
                Index = $"artillery-{request.StartTime:yyyy}.{request.StartTime:MM}",
                Id = GetHashString(jobject.ToString()),
                Type = "doc",
                Document = jobject
            };

            allrequests.Add(bulkDocument);
        }

        Log($"Request count: {allrequests.Count}");

        await Elastic.PutIntoIndex(serverurl, username, password, allrequests.ToArray());
    }

    static string GetHashString(string value)
    {
        using (var crypto = new SHA256Managed())
        {
            return string.Concat(crypto.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
        }
    }

    static void Log(string message)
    {
        Console.WriteLine(message);
    }
}
