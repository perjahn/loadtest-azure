using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

class Program
{
    static async Task<int> Main(string[] args)
    {
        int stopParse = Array.FindIndex(args, a => a == "--");
        List<string> parsedArgs = stopParse > 0 ? args.Take(stopParse).ToList() : args.ToList();

        if (parsedArgs.Contains("-unittest"))
        {
            return UnitTest() ? 0 : 1;
        }

        var extraFields = ExtractExtraFields(parsedArgs);

        if (parsedArgs.Count != 5)
        {
            Log(
@"Usage: ArtilleryToElastic.exe [-f <name> <value>] <filename> <serverurl> <username> <password> <rebasetime>

This tool imports an Artillery result file into Elasticsearch, and optionally copies other indices.
All imported/copied documents will have an added field that contains a rebased timestamp,
this is useful to make nighly loadtests appear to start at exactly the same time each day,
this makes everything easy to visualize, which is the real purpose of this tool.

-f:          Optional extra fields that will be added to every json document.
             May be specified multiple times to add multiple name/value pairs.
filename:    Artillery result input file (json).
serverurl:   Target elasticsearch base url.
username:    Target elasticsearch username.
password:    Target elasticsearch password.
rebasetime:  Start time (HH:mm:ss) that timestamps should be rebased on.

Environment variables, to copy extra documents with an applied Rebased[Timestamp] field. To copy multiple
indices, prefix and/or suffixed the variable names.
ElasticSourceServerurl:  Elasticsearch base url.
ElasticSourceUsername:   Elasticsearch username.
ElasticSourcePassword:   Elasticsearch password.
ElasticSourceIndex:      Elasticsearch source index.
ElasticTargetIndex:      Elasticsearch target index. Optional. May end with date pattern, yyyy.mm.dd, yyyy.mm or yyyy.
ElasticTimestampField:   Elasticsearch timestamp field. A Rebased[Timestamp] field will be added.
ElasticFilterField:      Filter to reduce number of copied documents, field name. Optional.
ElasticFilterValue:      Filter to reduce number of copied documents, field value. Optional.");

            return 1;
        }

        string filename = parsedArgs[0];
        string serverurl = parsedArgs[1];
        string username = parsedArgs[2];
        string password = parsedArgs[3];

        var elasticCopySources = GetElasticCopySources();

        if (!TryParseTime(parsedArgs[4], out long rebasestarttime))
        {
            Log("Invalid rebasetime format, use HH:mm:ss pattern.");
            return 1;
        }

        ArtilleryResult result = ArtilleryResult.ParseFile(filename, rebasestarttime);

        await ArtilleryToElastic.UploadResult(result, serverurl, username, password, extraFields);

        foreach (var elasticSource in elasticCopySources)
        {
            await CopyElasticLogs.CopyDocuments(elasticSource,
                serverurl, username, password, elasticSource.TargetIndex, result.EarliestStartTime.AddMinutes(-5), result.LastEndTime.AddMinutes(5), result.Diff_ms,
                extraFields);
        }

        return 0;
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

    static ElasticCopySettings[] GetElasticCopySources()
    {
        string[] validVariables = {
            "ElasticSourceServerurl",
            "ElasticSourceUsername",
            "ElasticSourcePassword",
            "ElasticSourceIndex",
            "ElasticTargetIndex",
            "ElasticTimestampField",
            "ElasticFilterField",
            "ElasticFilterValue" };

        var creds =
            Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value)
            .Where(e => validVariables.Any(v => e.Key.Contains(v)))
            .GroupBy(e => new
            {
                prefix = e.Key.Split(validVariables, StringSplitOptions.None).First(),
                postfix = e.Key.Split(validVariables, StringSplitOptions.None).Last()
            })
            .OrderBy(c => c.Key.prefix)
            .ThenBy(c => c.Key.postfix);

        var elasticCopySettings = new List<ElasticCopySettings>();
        foreach (var cred in creds)
        {
            List<string> missingVariables = new List<string>();

            string elasticSourceServerurl = cred.SingleOrDefault(c => c.Key.Contains("ElasticSourceServerurl")).Value;
            string elasticSourceUsername = cred.SingleOrDefault(c => c.Key.Contains("ElasticSourceUsername")).Value;
            string elasticSourcePassword = cred.SingleOrDefault(c => c.Key.Contains("ElasticSourcePassword")).Value;
            string elasticSourceIndex = cred.SingleOrDefault(c => c.Key.Contains("ElasticSourceIndex")).Value;
            string elasticTargetIndex = cred.SingleOrDefault(c => c.Key.Contains("ElasticTargetIndex")).Value;
            string elasticTimestampField = cred.SingleOrDefault(c => c.Key.Contains("ElasticTimestampField")).Value;
            string elasticFilterField = cred.SingleOrDefault(c => c.Key.Contains("ElasticFilterField")).Value;
            string elasticFilterValue = cred.SingleOrDefault(c => c.Key.Contains("ElasticFilterValue")).Value;
            if (elasticSourceServerurl == null)
            {
                missingVariables.Add("ElasticSourceServerurl");
            }
            if (elasticSourceUsername == null)
            {
                missingVariables.Add("ElasticSourceUsername");
            }
            if (elasticSourcePassword == null)
            {
                missingVariables.Add("ElasticSourcePassword");
            }
            if (elasticSourceIndex == null)
            {
                missingVariables.Add("ElasticSourceIndex");
            }
            if (elasticTimestampField == null)
            {
                missingVariables.Add("ElasticTimestampField");
            }

            if (missingVariables.Count > 0)
            {
                Log($"Missing environment variables: Prefix: '{cred.Key.prefix}', Postfix: '{cred.Key.postfix}': '{string.Join("', '", missingVariables)}'");
            }
            else
            {
                elasticCopySettings.Add(new ElasticCopySettings
                {
                    SourceServerurl = elasticSourceServerurl,
                    SourceUsername = elasticSourceUsername,
                    SourcePassword = elasticSourcePassword,
                    SourceIndex = elasticSourceIndex,
                    TargetIndex = elasticTargetIndex,
                    TimestampField = elasticTimestampField,
                    ElasticFilterField = elasticFilterField,
                    ElasticFilterValue = elasticFilterValue
                });
            }
        }

        return elasticCopySettings.ToArray();
    }

    static bool UnitTest()
    {
        bool result1 = UnitTest1();
        bool result2 = UnitTest2();

        return result1 || result2;
    }

    static bool UnitTest1()
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
            long resultms = ArtilleryResult.GetDiff(timeSinceMidnightMs * 3600 * 1000, desiredTimeOfDayMs * 3600 * 1000);

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

    static bool UnitTest2()
    {
        var dic = new Dictionary<int, int>
        {
            [11] = 2,
            [3] = 3
        };

        var results = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            results.Add(ArtilleryResult.PopSmallestKey(dic));
        }
        var compare = new[] { 3, 3, 3, 11, 11, 0 };

        bool error = false;

        for (int i = 0; i < 6; i++)
        {
            if (results[i] != compare[i])
            {
                error = true;
                Log($"ERROR: Expected {compare[i]}, was {results[i]}");
            }
            else
            {
                Log($"OK: Expected {compare[i]}, was {results[i]}");
            }
        }

        return error;
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

    static void Log(string message)
    {
        Console.WriteLine(message);
    }
}
