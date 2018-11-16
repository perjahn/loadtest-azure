using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

class ElasticCopySettings
{
    public string SourceServerurl { get; set; }
    public string SourceUsername { get; set; }
    public string SourcePassword { get; set; }
    public string SourceIndex { get; set; }
    public string TargetIndex { get; set; }
    public string TimestampField { get; set; }
    public string ElasticFilterField { get; set; }
    public string ElasticFilterValue { get; set; }
}

class CopyElasticLogs
{
    public static async Task CopyDocuments(
        ElasticCopySettings source,
        string targetServerurl, string targetUsername, string targetPassword, string targetIndex,
        DateTime starttime, DateTime endtime, long diff_ms,
        Dictionary<string, string> extraFields)
    {
        Log($"Copying: {targetServerurl}/{targetUsername}/{new string('*', targetPassword.Length)}/{targetIndex ?? "<null>"}, " +
            $"starttime: {starttime:yyyy-MM-dd HH:mm:ss.fff}, endtime: {endtime:yyyy-MM-dd HH:mm:ss.fff}, diffms: {diff_ms}");

        string timestampfieldname = source.TimestampField;

        List<ElasticBulkDocument> newDocuments = new List<ElasticBulkDocument>();

        for (DateTime spanStart = starttime; spanStart < endtime; spanStart = spanStart.AddMinutes(2))
        {
            DateTime spanEnd = spanStart.AddMinutes(2) > endtime ? endtime : spanStart.AddMinutes(2);

            Log($"time span: {spanStart:yyyy-MM-dd HH:mm:ss.fff} - {spanEnd:yyyy-MM-dd HH:mm:ss.fff}");

            dynamic sourceDocuments = await Elastic.GetRowsAsync(source.SourceServerurl, source.SourceUsername, source.SourcePassword, source.SourceIndex,
                source.ElasticFilterField, source.ElasticFilterValue,
                timestampfieldname, spanStart, spanEnd);
            if (sourceDocuments == null || sourceDocuments.Count == 0)
            {
                Log("Got no documents.");
                continue;
            }
            Log($"Source document count: {sourceDocuments.Count}");

            foreach (var sourceDocument in sourceDocuments)
            {
                dynamic jobject = new JObject(sourceDocument._source);

                DateTime timestamp = jobject[timestampfieldname];
                jobject[$"Rebased{timestampfieldname}"] = timestamp.AddMilliseconds(diff_ms).ToString("o");

                foreach (var field in extraFields)
                {
                    jobject[field.Key] = field.Value;
                }

                var bulkDocument = new ElasticBulkDocument
                {
                    Index = GetIndexWithDate(targetIndex, timestamp) ?? sourceDocument._index,
                    Id = sourceDocument._id,
                    Type = sourceDocument._type,
                    Document = jobject
                };

                newDocuments.Add(bulkDocument);
            }
        }

        if (newDocuments.Count == 0)
        {
            Log("No documents to copy.");
            return;
        }

        MakeSureDoublesAreDoubles(newDocuments.Select(d => d.Document).ToArray());

        Log($"New document count: {newDocuments.Count}");

        await Elastic.PutIntoIndex(targetServerurl, targetUsername, targetPassword, newDocuments.ToArray());
    }

    static string GetIndexWithDate(string index, DateTime datetime)
    {
        if (index.ToLower().EndsWith("-yyyy.mm.dd"))
        {
            return $"{index.Substring(0, index.Length - 11)}-{datetime:yyyy}.{datetime:MM}.{datetime:dd}";
        }
        else if (index.ToLower().EndsWith("-yyyy.mm"))
        {
            return $"{index.Substring(0, index.Length - 8)}-{datetime:yyyy}.{datetime:MM}";
        }
        else if (index.ToLower().EndsWith("-yyyy"))
        {
            return $"{index.Substring(0, index.Length - 5)}-{datetime:yyyy}";
        }
        else
        {
            return index;
        }
    }

    static void MakeSureDoublesAreDoubles(JObject[] documents)
    {
        var propertyPaths = new List<string>(documents.SelectMany(d =>
            d.DescendantsAndSelf()
            .Where(j => j is JProperty)
            .Select(p => p.Path))
            .Distinct())
            .OrderBy(p => p)
            .ToArray();

        Log($"Got {propertyPaths.Length} distinct properties: '{string.Join("', '", propertyPaths)}'");

        foreach (var propertyPath in propertyPaths)
        {
            if (ShouldRewrite(documents, propertyPath))
            {
                Log($"Rewriting property: '{propertyPath}'", ConsoleColor.Yellow);

                foreach (var doc in documents)
                {
                    var property = doc.DescendantsAndSelf().Where(p => p is JProperty && p.Path == propertyPath).Cast<JProperty>().SingleOrDefault();
                    if (property != null)
                    {
                        string value = property.Value.Value<string>();
                        if (!value.Contains('.'))
                        {
                            try
                            {
                                property.Parent[property.Name] = double.Parse($"{value}.0", CultureInfo.InvariantCulture);
                            }
                            catch (Exception ex)
                            {
                                Log($">>>{value}<<<");
                                Log(ex.ToString());
                                throw;
                            }
                        }
                    }
                }
            }
        }
    }

    static bool ShouldRewrite(JObject[] documents, string propertyPath)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        bool isDouble = false;
        bool isNumber = true;

        foreach (var doc in documents)
        {
            var property = doc.DescendantsAndSelf().Where(p => p is JProperty && p.Path == propertyPath).Cast<JProperty>().SingleOrDefault();
            if (property != null && property.Value is JValue)
            {
                var value = property.Value.Value<string>();
                if (value != null)
                {
                    if (!Regex.IsMatch(value, "^[0-9]+$") && !Regex.IsMatch(value, @"^[0-9]+\.[0-9]+$"))
                    {
                        isNumber = false;
                    }
                    if (Regex.IsMatch(value, @"^[0-9]+\.[0-9]+$"))
                    {
                        isDouble = true;
                    }
                }
            }
        }

        return isDouble && isNumber;
    }

    static void Log(string message)
    {
        Console.WriteLine(message);
    }

    static void Log(string message, ConsoleColor color)
    {
        ConsoleColor oldColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Log(message);
        }
        finally
        {
            Console.ForegroundColor = oldColor;
        }
    }
}
