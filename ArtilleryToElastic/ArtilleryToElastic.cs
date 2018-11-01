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
        if (args.Contains("-unittest"))
        {
            return UnitTest() ? 0 : 1;
        }

        List<string> parsedArgs = args.ToList();

        var extraFields = ExtractExtraFields(parsedArgs);

        if (parsedArgs.Count < 4 || parsedArgs.Count > 5)
        {
            Log(
@"Usage: ArtilleryToElastic.exe [-f name value] <filename> <serverurl> <username> <password> [rebasetime]

-f:          Optional extra fields that will be added to every json document.
filename:    Artillery result file (json).
serverurl:   Elasticsearch base url.
username:    Elasticsearch username.
password:    Elasticsearch password.
rebasetime:  Optional start time (HH:mm:ss) that time stamps should be rebased on.");
            return 1;
        }

        string filename = parsedArgs[0];
        string serverurl = parsedArgs[1];
        string username = parsedArgs[2];
        string password = parsedArgs[3];

        bool addrebasestarttime = false;
        long rebasestartime = 0;
        if (parsedArgs.Count == 5)
        {
            if (!TryParseTime(parsedArgs[4], out rebasestartime))
            {
                Log("Invalid rebasetime format, use HH:mm:ss pattern.");
                return 1;
            }
            addrebasestarttime = true;
        }

        UploadFile(filename, serverurl, username, password, addrebasestarttime, rebasestartime, extraFields);

        return 0;
    }

    static void UploadFile(string filename, string serverurl, string username, string password, bool addrebasestarttime, long rebasestartime,
        Dictionary<string, string> extraFields)
    {
        byte[] binarycontent = File.ReadAllBytes(filename);
        string loadtestid = GetHashString(binarycontent);

        string content = File.ReadAllText(filename);
        JObject document = JObject.Parse(content);

        JArray intermediates = (JArray)document["intermediate"];

        List<JObject> allrequests = new List<JObject>();

        long diffms = 0;
        if (addrebasestarttime)
        {
            DateTime earliestStartTime = GetEarliestStartTime(intermediates);
            double ms = earliestStartTime.TimeOfDay.TotalMilliseconds;

            diffms = GetDiff((long)earliestStartTime.TimeOfDay.TotalMilliseconds, rebasestartime * 1000);

            Log($"earliestStartTime: {earliestStartTime.ToString("YYYY-MM-dd HH:mm:ss.fff")}, diffms: {diffms}");
        }

        foreach (var intermediate in intermediates)
        {
            JArray latencies = (JArray)intermediate["latencies"];
            foreach (JArray latency in latencies)
            {
                string timestamp = (latency[0].Value<double>() / 1000).ToString(CultureInfo.InvariantCulture);
                string id = latency[1].Value<string>();
                long latencyns = latency[2].Value<long>();
                long httpresult = latency[3].Value<long>();

                JObject jobject = new JObject
                {
                    ["LoadtestID"] = loadtestid,
                    ["@timestamp"] = timestamp,
                    ["_id"] = id,
                    ["LatencyNS"] = latencyns,
                    ["HttpResult"] = httpresult
                };
                foreach (var field in extraFields)
                {
                    jobject[field.Key] = field.Value;
                }
                if (addrebasestarttime)
                {
                    long starttime = latency[0].Value<long>() + diffms;
                    string rebasetime = (((double)starttime) / 1000).ToString(CultureInfo.InvariantCulture);
                    jobject["RebaseTimestamp"] = rebasetime;
                }

                allrequests.Add(jobject);
            }
        }

        Log($"Request count: {allrequests.Count}");

        string[] reformattimestampfields = addrebasestarttime ? new[] { "@timestamp", "RebaseTimestamp" } : new[] { "@timestamp" };

        PutIntoIndex(serverurl, username, password, "artillery", "doc", "@timestamp", "_id", allrequests.ToArray(), reformattimestampfields);
    }

    static Dictionary<string, string> ExtractExtraFields(List<string> parsedArgs)
    {
        var dic = new Dictionary<string, string>();

        int index = parsedArgs.IndexOf("-f");
        while (index >= 0 && index < parsedArgs.Count - 2)
        {
            string name = parsedArgs[index + 1];
            string value = parsedArgs[index + 2];
            Log($"Got extra field: '{name}' '{value}'");
            dic[name] = value;

            parsedArgs.RemoveAt(index);
            parsedArgs.RemoveAt(index);
            parsedArgs.RemoveAt(index);

            index = parsedArgs.IndexOf("-f", index);
        }

        return dic;
    }

    static bool UnitTest()
    {
        long[,] difftestshours = new long[,]
        {
             { 0,  0,  0 },
             { 0,  1,  1 },
             { 0,  11, 11 },
             { 0,  12, 12 },
             { 0,  13, -11 },
             { 0,  23, -1 },
             { 23, 0,  1 },
             { 23, 1,  2 },
             { 23, 11, 12 },
             { 23, 12, -11 },
             { 23, 13, -10 },
             { 23, 23, 0 },
             { 3,  4,  1 },
             { 3,  2,  -1 },
             { 3,  23, -4 },
             { 3,  14, 11 },
             { 3,  15, 12 },
             { 3,  16, -11 }
        };


        Log($"{difftestshours.Length}");

        for (int i = 0; i < difftestshours.Length / 3; i++)
        {
            long timeSinceMidnightMs = difftestshours[i, 0];
            long desiredTimeOfDayMs = difftestshours[i, 1];
            long timediffms = difftestshours[i, 2];
            long resultms = GetDiff(timeSinceMidnightMs * 3600 * 1000, desiredTimeOfDayMs * 3600 * 1000);

            if (resultms != timediffms * 3600 * 1000)
            {
                Log($"ERROR: {timeSinceMidnightMs} {desiredTimeOfDayMs} was: {resultms / 3600 / 1000} should be: {timediffms}");
            }
            else
            {
                Log($"OK:    {timeSinceMidnightMs} {desiredTimeOfDayMs} was {resultms / 3600 / 1000}");
            }
        }

        return true;
    }

    static bool TryParseTime(string rebasetime, out long totalseconds)
    {
        string errorMessage = "Invalid time format for rebasetime, must follow HH:mm:ss.";
        if (rebasetime.Length != 8 || rebasetime[2] != ':' || rebasetime[5] != ':')
        {
            Log(errorMessage);
            totalseconds = 0;
            return false;
        }
        int hours = int.Parse(rebasetime.Substring(0, 2));
        int minutes = int.Parse(rebasetime.Substring(3, 2));
        int seconds = int.Parse(rebasetime.Substring(6, 2));
        if (hours < 0 || hours > 23 || minutes < 0 || minutes > 59 || seconds < 0 || seconds > 59)
        {
            Log(errorMessage);
            totalseconds = 0;
            return false;
        }

        totalseconds = hours * 3600 + minutes * 60 + seconds;
        return true;
    }

    static DateTime GetEarliestStartTime(JArray intermediates)
    {
        long earlieststarttime = long.MaxValue;
        foreach (var intermediate in intermediates)
        {
            JArray latencies = (JArray)intermediate["latencies"];
            foreach (JArray latency in latencies)
            {
                long starttime = latency[0].Value<long>();
                if (starttime < earlieststarttime)
                {
                    earlieststarttime = starttime;
                }
            }
        }
        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(earlieststarttime);
    }

    static long GetDiff(long timeSinceMidnightMs, long desiredTimeOfDayMs)
    {
        long timediffms;

        if (desiredTimeOfDayMs - timeSinceMidnightMs > 12 * 3600 * 1000)
        {
            timediffms = desiredTimeOfDayMs - timeSinceMidnightMs - 24 * 3600 * 1000;
        }
        else
        {
            if (timeSinceMidnightMs - desiredTimeOfDayMs >= 12 * 3600 * 1000)
            {
                timediffms = 24 * 3600 * 1000 + desiredTimeOfDayMs - timeSinceMidnightMs;
            }
            else
            {
                timediffms = desiredTimeOfDayMs - timeSinceMidnightMs;
            }
        }
        Log($"timeSinceMidnightMs: {timeSinceMidnightMs}, desiredTimeOfDayMs: {desiredTimeOfDayMs}, timediffms: {timediffms}");
        return timediffms;
    }

    static void PutIntoIndex(string serverurl, string username, string password, string indexname,
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
        ImportRows(address, username, password, bulkdata);

        Log("Done!");
    }

    static void ImportRows(string address, string username, string password, string bulkdata)
    {
        using (WebClient client = new WebClient())
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
