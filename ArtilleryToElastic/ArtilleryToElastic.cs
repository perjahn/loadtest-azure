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
        List<JObject> allrequests = new List<JObject>();

        foreach (JArray latency in artilleryResult.Latencies)
        {
            string timestamp = (latency[0].Value<double>() / 1000).ToString(CultureInfo.InvariantCulture);
            string id = latency[1].Value<string>();
            long latency_ns = latency[2].Value<long>();
            long httpresult = latency[3].Value<long>();

            JObject jobject = new JObject
            {
                ["LoadtestID"] = artilleryResult.LoadtestID,
                ["@timestamp"] = timestamp,
                ["_id"] = id,
                ["LatencyNS"] = latency_ns,
                ["HttpResult"] = httpresult
            };
            foreach (var field in extraFields)
            {
                jobject[field.Key] = field.Value;
            }
            long starttime = latency[0].Value<long>() + artilleryResult.Diff_ms;
            string rebasetime = (((double)starttime) / 1000).ToString(CultureInfo.InvariantCulture);
            jobject["RebasedTimestamp"] = rebasetime;

            allrequests.Add(jobject);
        }

        Log($"Request count: {allrequests.Count}");

        string[] reformattimestampfields = new[] { "@timestamp", "RebasedTimestamp" };

        await PutIntoIndex(serverurl, username, password, "artillery", "doc", "@timestamp", "_id", allrequests.ToArray(), reformattimestampfields);
    }

    static async Task PutIntoIndex(string serverurl, string username, string password, string indexname,
        string typename, string timestampfield, string idfield, JObject[] jsonrows, string[] reformattimestampfields)
    {
        StringBuilder sb = new StringBuilder();

        foreach (JObject jsonrow in jsonrows)
        {
            double seconds = double.Parse(jsonrow[timestampfield].Value<string>(), CultureInfo.InvariantCulture);
            DateTime timestamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

            foreach (string reformattimestampfield in reformattimestampfields)
            {
                double reformatseconds = double.Parse(jsonrow[reformattimestampfield].Value<string>(), CultureInfo.InvariantCulture);
                DateTime reformattimestamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(reformatseconds);
                jsonrow[reformattimestampfield] = reformattimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff");
            }

            string dateindexname = $"{indexname}-{timestamp:yyyy.MM}";

            string id = $"{jsonrow[idfield].Value<string>()}";
            jsonrow.Remove(idfield);

            string metadata = "{ \"index\": { \"_index\": \"" + dateindexname + "\", \"_type\": \"" + typename + "\", \"_id\": \"" + id + "\" } }";
            sb.AppendLine(metadata);

            string rowdata = jsonrow.ToString().Replace("\r", string.Empty).Replace("\n", string.Empty);
            sb.AppendLine(rowdata);
        }

        string address = $"{serverurl}/_bulk";
        string bulkdata = sb.ToString();

        Log($"Importing {jsonrows.Length} documents...");
        await Elastic.ImportRows(address, username, password, bulkdata);

        Log("Done!");
    }

    static void Log(string message)
    {
        Console.WriteLine(message);
    }
}
