using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace ICSharpCore.Protocols
{
    /// <summary>
    /// https://jupyter-client.readthedocs.io/en/stable/messaging.html#execution-results
    /// </summary>
    public class ExecuteReplyError : ExecuteReply
    {
        public ExecuteReplyError()
        {
            Status = Protocols.StatusType.Error;
        }
        
        [JsonProperty("ename")]
        public string EName { get; set; }

        [JsonProperty("evalue")]
        public string EValue { get; set; }

        [JsonProperty("traceback")]
        public List<string> Traceback { get; set; }
    }
}
