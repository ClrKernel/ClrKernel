using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace ClrKernel.Protocols
{
    public class Status
    {
        [JsonProperty("execution_state")]
        public string ExecutionState { get; set; }
    }
}
