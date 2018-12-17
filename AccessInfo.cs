using System;
using System.Collections.Generic;
using System.Text;

using Newtonsoft.Json;

namespace PerformanceTest
{
    public class AccessInfo
    {
        /// <summary>
        /// 
        /// </summary>
        [JsonProperty("token")]
        public string SignalRToken { set; get; } = "";

        /// <summary>
        /// 
        /// </summary>
        [JsonProperty("url")]
        public string SignalRUrl { set; get; } = "";

        /// <summary>
        /// 
        /// </summary>
        [JsonProperty("expire")]
        public int TokenExpire { set; get; } = 0;

        /// <summary>
        /// 
        /// </summary>
        [JsonProperty("error")]
        public string Error { set; get; } = "";
    }
}
