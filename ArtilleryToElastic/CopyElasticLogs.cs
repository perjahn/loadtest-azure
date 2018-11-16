using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        for (DateTime spanStart = starttime; spanStart < endtime; spanStart = spanStart.AddMinutes(2))
        {
            DateTime spanEnd = spanStart.AddMinutes(2) > endtime ? endtime : spanStart.AddMinutes(2);

            Log($"time span: {spanStart:yyyy-MM-dd HH:mm:ss.fff} - {spanEnd:yyyy-MM-dd HH:mm:ss.fff}");

            dynamic sourceDocuments = await Elastic.GetRowsAsync(source.SourceServerurl, source.SourceUsername, source.SourcePassword, source.SourceIndex,
                source.ElasticFilterField, source.ElasticFilterValue,
                timestampfieldname, spanStart, spanEnd);
            if (sourceDocuments == null || sourceDocuments.Count == 0)
            {
                Log("No documents to copy.");
                continue;
            }
            Log($"Source document count: {sourceDocuments.Count}");

            List<JObject> newDocuments = new List<JObject>();

            string doctype = sourceDocuments[0]["_type"];

            foreach (var sourceDocument in sourceDocuments)
            {
                dynamic jobject = new JObject(sourceDocument);

                DateTime timestamp = sourceDocument._source[timestampfieldname];
                jobject._source[$"Rebased{timestampfieldname}"] = timestamp.AddMilliseconds(diff_ms).ToString("o");

                if (targetIndex != null)
                {
                    jobject._index = targetIndex;
                }

                foreach (var field in extraFields)
                {
                    jobject[field.Key] = field.Value;
                }

                newDocuments.Add(jobject);
            }

            Log($"New document count: {newDocuments.Count}");

            await PutIntoIndex(targetServerurl, targetUsername, targetPassword, "_index", doctype, "_id", newDocuments.ToArray());
        }
    }

    static async Task PutIntoIndex(string serverurl, string username, string password,
        string indexfield, string typename, string idfield, JObject[] jsonrows)
    {
        StringBuilder sb = new StringBuilder();

        foreach (JObject jsonrow in jsonrows)
        {
            string index = $"{jsonrow[indexfield].Value<string>()}";
            string id = $"{jsonrow[idfield].Value<string>()}";

            string metadata = "{ \"index\": { \"_index\": \"" + index + "\", \"_type\": \"" + typename + "\", \"_id\": \"" + id + "\" } }";
            sb.AppendLine(metadata);

            string rowdata = jsonrow["_source"].ToString().Replace("\r", string.Empty).Replace("\n", string.Empty);
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
