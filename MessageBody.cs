using System;
using System.Collections.Generic;
using System.Text;

namespace PerformanceTest
{
    public class MessageBody
    {
        public DateTimeOffset CreatedTime { set; get; }

        public byte[] Contnet { set; get; }
    }
}
