using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public class Request
{
    public DateTime StartTime;
    public long Latency;
    public int HttpResult;
}

class ArtilleryResult
{
    public string LoadtestID { get; set; }
    public Request[] Requests { get; set; }
    public long Diff_ms { get; set; }
    public DateTime EarliestStartTime { get; set; }
    public DateTime LastEndTime { get; set; }

    public static ArtilleryResult ParseFile(string filename, long rebasestarttime)
    {
        string content = File.ReadAllText(filename);
        dynamic document = JObject.Parse(content);

        string loadtestID = GetHashString(document.ToString());

        var result = new ArtilleryResult
        {
            LoadtestID = loadtestID
        };

        result.EarliestStartTime = GetEarliestStartTime(document);
        result.LastEndTime = GetLastEndTime(document);

        if (result.EarliestStartTime > result.LastEndTime)
        {
            Log("Couldn't find any latencies.");
            return null;
        }

        result.Diff_ms = GetDiff((long)result.EarliestStartTime.TimeOfDay.TotalMilliseconds, rebasestarttime * 1000);
        Log($"EarliestStartTime: {result.EarliestStartTime:yyyy-MM-dd HH:mm:ss.fff}, LastEndTime: {result.LastEndTime:yyyy-MM-dd HH:mm:ss.fff}, Diff_ms: {result.Diff_ms}");

        result.Requests = GetRequests(document);
        Log($"Got {result.Requests.Length} requests/latencies.");

        return result;
    }

    static DateTime GetEarliestStartTime(JObject document)
    {
        DateTime earliestStartTime = DateTime.MaxValue;

        foreach (dynamic intermediate in document["intermediate"])
        {
            if (intermediate.latencies is JArray && intermediate.latencies.Count > 0)
            {
                DateTime startTime = intermediate.timestamp;

                if (startTime < earliestStartTime)
                {
                    earliestStartTime = startTime;
                }
            }
        }

        return earliestStartTime;
    }

    static DateTime GetLastEndTime(JObject document)
    {
        DateTime lastEndTime = DateTime.MinValue;

        foreach (dynamic intermediate in document["intermediate"])
        {
            if (intermediate.latencies is JArray && intermediate.latencies.Count > 0)
            {
                DateTime startTime = intermediate.timestamp;

                foreach (long latency_ns in intermediate.latencies)
                {
                    DateTime endTime = startTime.AddTicks(latency_ns / 100);
                    if (endTime > lastEndTime)
                    {
                        lastEndTime = endTime;
                    }
                }
            }
        }

        return lastEndTime;
    }

    public static long GetDiff(long timeSinceMidnight_ms, long desiredTimeOfDay_ms)
    {
        long timediff_ms;

        if (desiredTimeOfDay_ms - timeSinceMidnight_ms > 12 * 3600 * 1000)
        {
            timediff_ms = desiredTimeOfDay_ms - timeSinceMidnight_ms - 24 * 3600 * 1000;
        }
        else
        {
            if (timeSinceMidnight_ms - desiredTimeOfDay_ms >= 12 * 3600 * 1000)
            {
                timediff_ms = 24 * 3600 * 1000 + desiredTimeOfDay_ms - timeSinceMidnight_ms;
            }
            else
            {
                timediff_ms = desiredTimeOfDay_ms - timeSinceMidnight_ms;
            }
        }
        Log($"timeSinceMidnightMs: {timeSinceMidnight_ms}, desiredTimeOfDayMs: {desiredTimeOfDay_ms}, timediffms: {timediff_ms}");
        return timediff_ms;
    }

    static Request[] GetRequests(JObject document)
    {
        var requests = new List<Request>();

        foreach (dynamic intermediate in document["intermediate"])
        {
            if (intermediate.latencies is JArray && intermediate.latencies.Count > 0)
            {
                DateTime startTime = intermediate.timestamp;

                var httpResultCodes = new Dictionary<int, int>();

                httpResultCodes = intermediate.codes.ToObject<Dictionary<int, int>>();

                foreach (long latency_ns in intermediate.latencies)
                {
                    int fakeHttpResultCode = PopSmallestKey(httpResultCodes);

                    requests.Add(new Request { StartTime = startTime, Latency = latency_ns, HttpResult = fakeHttpResultCode });
                }
            }
        }

        return requests.ToArray();
    }

    public static int PopSmallestKey(Dictionary<int, int> dic)
    {
        int[] keys = dic.Keys.Select(k => k).OrderBy(k => k).ToArray();

        foreach (var key in keys)
        {
            if (dic[key] > 0)
            {
                dic[key]--;
                if (dic[key] == 0)
                {
                    dic.Remove(key);
                }
                return key;
            }
        }

        return 0;
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
