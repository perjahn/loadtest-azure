#r "Newtonsoft.Json.dll"

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Log(
@"Usage: csi.exe ArtilleryToElastic.csx <filename> <serverurl> <username> <password>
               
filename:   Artillery result file (json).
serverurl:  Elasticsearch base url.
username:   Elasticsearch username.
password:   Elasticsearch password.");
            return 1;
        }

        string filename = args[0];
        string serverurl = args[1];
        string username = args[2];
        string password = args[3];

        byte[] binarycontent = File.ReadAllBytes(filename);
        string loadtestid = GetHashString(binarycontent);

        string content = File.ReadAllText(filename);
        JObject jobject = JObject.Parse(content);

        JArray intermediates = (JArray)jobject["intermediate"];

        List<JObject> allrequests = new List<JObject>();

        foreach (var intermediate in intermediates)
        {
            JArray latencies = (JArray)intermediate["latencies"];
            foreach (JArray latency in latencies)
            {
                string timestamp = (latency[0].Value<double>() / 1000).ToString(CultureInfo.InvariantCulture);
                string id = latency[1].Value<string>();
                long latencyns = latency[2].Value<long>();
                long httpresult = latency[3].Value<long>();

                allrequests.Add(new JObject
                {
                    ["loadtestid"] = loadtestid,
                    ["@timestamp"] = timestamp,
                    ["_id"] = id,
                    ["latencyns"] = latencyns,
                    ["httpresult"] = httpresult
                });
            }
        }

        Log($"Request count: {allrequests.Count}");

        PutIntoIndex(serverurl, username, password, "artillery", "doc", "@timestamp", "_id", allrequests.ToArray());

        return 0;
    }

    static void PutIntoIndex(string serverurl, string username, string password, string indexname,
        string typename, string timestampfield, string idfield, JObject[] jsonrows)
    {
        using (WebClient client = new WebClient())
        {
            StringBuilder sb = new StringBuilder();

            foreach (JObject jsonrow in jsonrows)
            {
                double seconds = double.Parse(jsonrow[timestampfield].Value<string>(), CultureInfo.InvariantCulture);
                DateTime timestamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
                jsonrow[timestampfield] = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff");

                string dateindexname = $"{indexname}-{timestamp:yyyy.MM}";

                string id = $"{jsonrow[idfield].Value<string>()}";
                jsonrow.Remove("_id");

                string metadata = "{ \"index\": { \"_index\": \"" + dateindexname + "\", \"_type\": \"" + typename + "\", \"_id\": \"" + id + "\" } }";
                sb.AppendLine(metadata);

                string rowdata = jsonrow.ToString().Replace("\r", string.Empty).Replace("\n", string.Empty);
                sb.AppendLine(rowdata);
            }

            string address = $"{serverurl}/_bulk";
            string bulkdata = sb.ToString();

            Log("Beginning of the data...");
            Log($">>>{bulkdata.Substring(0, 300)}<<<");

            Log($"Importing documents...");
            ImportRows(client, address, username, password, bulkdata);

            Log("Done!");
        }
    }

    static void ImportRows(WebClient client, string address, string username, string password, string bulkdata)
    {
        if (username != null && password != null)
        {
            string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            client.Headers[HttpRequestHeader.Authorization] = $"Basic {credentials}";
        }
        client.Headers["Content-Type"] = "application/x-ndjson";
        client.Headers["Accept"] = "application/json";
        client.Encoding = Encoding.UTF8;

        string result = string.Empty;
        try
        {
            result = client.UploadString(address, bulkdata);
        }
        catch (WebException ex)
        {
            Log($"Put '{address}': >>>{bulkdata}<<<");
            Log($"Result: >>>{result}<<<");
            Log($"Exception: >>>{ex.ToString()}<<<");
        }
    }

    static string GetHashString(byte[] value)
    {
        using (var crypto = new SHA256Managed())
        {
            return string.Concat(crypto.ComputeHash(value).Select(b => b.ToString("x2")));
        }
    }

    static void Log(string message)
    {
        Console.WriteLine($"{message}");
    }
}

return Program.Main(Environment.GetCommandLineArgs().Skip(2).ToArray());
