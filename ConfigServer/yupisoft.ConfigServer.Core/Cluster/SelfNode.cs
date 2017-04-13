﻿

using System;
using System.Collections.Generic;
using System.Linq;

namespace yupisoft.ConfigServer.Core.Cluster
{

    public class SelfNode : Node
    {
        public List<LogMessage> LogMessages { get; set; }

        public SelfNode() : base()
        {
            LogMessages = new List<LogMessage>();
            Self = true;
            Life = 2;
        }

        public long LastLogId {
            get
            {
                if (LogMessages.Count > 0)
                    return LogMessages.Last().LogId;
                return 0;
            }            
        }

        public DateTime LastLogDate
        {
            get
            {
                if (LogMessages.Count > 0)
                    return LogMessages.Last().Created;
                return DateTime.MaxValue;
            }
        }
    }
}
