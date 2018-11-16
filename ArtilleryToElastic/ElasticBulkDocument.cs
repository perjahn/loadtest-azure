using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

class ElasticBulkDocument
{
    public string Index { get; set; } = null;
    public string Type { get; set; } = null;
    public string Id { get; set; } = null;
    public JObject Document { get; set; } = null;
}
