﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace yupisoft.ConfigServer.Core
{
    public delegate void StoreChanged(IStoreProvider sender, string entityName);
    public interface IStoreProvider 
    {
        IConfigWatcher Watcher { get; }
        event StoreChanged Change;
        string StartEntityName { get; }
        JToken Get(string entityName);
        JToken GetRaw(string entityName);
        void Set(JToken node, string entityName);
    }
}
