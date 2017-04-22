﻿using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using yupisoft.ConfigServer.Core.Cluster;

namespace yupisoft.ConfigServer.Core
{
    public delegate void DataChangedEventHandler(string tenantId, string entity, JToken jsonDiff);

    public class ConfigServerManager
    {
        private Timer _monkeyTimer;
        private object _lock = new object();
        private object _lockMonkey = new object();
        private ILogger _logger;
        public ConfigServerTenants TenantManager { get; private set; }
        public ConfigServerServices ServiceManager { get; private set; }
        public ConfigServerHooks HooksManager { get; private set; }
        public ClusterManager ClusterMan { get; private set; }

        public DateTime AliveSince { get; private set; }

        public event DataChangedEventHandler DataChanged;

        public ConfigServerManager(ConfigServerServices serviceManager, ConfigServerHooks hooksManager, ClusterManager clusterMan, ILogger<ConfigServerManager> logger)
        {
            TenantManager = clusterMan.TenantManager;
            ServiceManager = serviceManager;
            HooksManager = hooksManager;
            ClusterMan = clusterMan;
            _logger = logger;
            _monkeyTimer = new Timer(new TimerCallback(MonkeyTimer_Elapsed), null, Timeout.Infinite, 2000);
        }

        private void MonkeyTimer_Elapsed(object state)
        {
            _monkeyTimer.Change(Timeout.Infinite, 2000);
            lock (_lockMonkey)
            {
                string[] lines = File.ReadAllLines(Path.Combine(Directory.GetCurrentDirectory(), "monkey.txt"));
                foreach(string line in lines)
                {
                    if (line.StartsWith("CHG.NODE.URI"))
                    {
                        //CHG.NODE.URI:1:http://127.0.0.1:8003
                        string[] parts = line.Split(':');
                        string nodeId = parts[1];
                        string uri = parts[2] + (parts.Length > 3 ? ":" + parts[3] : "") + (parts.Length > 4 ? ":" + parts[4] : "");
                        Node node = ClusterMan.Nodes.FirstOrDefault(n => n.Id == nodeId);
                        if ((node != null) && (node.NodeConfig.Uri != uri))
                        {
                            node.NodeConfig.Uri = uri;
                            _logger.LogTrace("Monkey: Applying Command(" + line + ")");
                        }
                    }
                    if (line.StartsWith("CHG.NODE.WURI"))
                    {
                        //CHG.NODE.WURI:1:http://127.0.0.1:8003
                        string[] parts = line.Split(':');
                        string nodeId = parts[1];
                        string uri = parts[2] + (parts.Length > 3 ? ":" + parts[3] : "") + (parts.Length > 4 ? ":" + parts[4] : "");
                        Node node = ClusterMan.Nodes.FirstOrDefault(n => n.Id == nodeId);
                        if ((node != null) && (node.NodeConfig.WANUri != uri))
                        {
                            node.NodeConfig.WANUri = uri;
                            _logger.LogTrace("Monkey: Applying Command(" + line + ")");
                        }
                    }
                }
            }
            _monkeyTimer.Change(2000, 2000);
        }

        public void StartServer()
        {
            foreach (var tenant in TenantManager.Tenants)
            {
                tenant.Store.Change += Store_Change;
                tenant.StartLoadTenantData += Tenant_StartLoadTenantData;
                tenant.EndLoadTenantData += Tenant_EndLoadTenantData;
                tenant.Load(true);
                _logger.LogTrace("Loaded Data for Tenant: " + tenant.Id);
            }
            ClusterMan.StartManaging();
        }

        private void Tenant_EndLoadTenantData(ConfigServerTenant tenant, JToken dataToken, bool startingUp)
        {   
            ServiceManager.StartMonitoring();
            HooksManager.StartMonitoring();
            ServiceManager.StartServiceDiscovery();
        }

        private void Tenant_StartLoadTenantData(ConfigServerTenant tenant, JToken dataToken, bool startingUp)
        {
            ServiceManager.StopServiceDiscovery();
            ServiceManager.StopMonitoring();
            HooksManager.StopMonitoring();            
        }

        private void Store_Change(ConfigServerTenant tenant, IStoreProvider sender, string entityName)
        {
            var loadResult = tenant.Load(false);
            if (loadResult.Changes.Length > 0)
                foreach (var e in loadResult.Changes)
                    DataChanged?.Invoke(tenant.TenantConfig.Id, e.entity, e.diffToken);
        }

        private ConfigServerTenant GetTenant(string tenantId)
        {
            foreach (var tenant in TenantManager.Tenants)
            {
                if (tenant.TenantConfig.Id == tenantId)
                    return tenant;
            }
            return null;
        }

        public JNode GetRaw(string path, string entityName, string tenantId)
        {
            var tenant = GetTenant(tenantId);
            if (tenant == null) throw new Exception("Tenant: " + tenantId + " not found.");
            return tenant.GetRaw(path, entityName);
        }

        public JToken Get(string path, string tenantId)
        {
            return Get<JToken>(path, tenantId);
        }

        public T Get<T>(string path, string tenantId)
        {
            var tenant = GetTenant(tenantId);
            if (tenant == null)
            {
                _logger.LogError("Get: Tenant: " + tenantId + " not found.");
                new Exception("Get: Tenant: " + tenantId + " not found.");
            }
            return tenant.Get<T>(path);
        }

        public bool Set(JNode newToken, string tenantId)
        {
            var tenant = GetTenant(tenantId);

            if (tenant == null)
            {
                _logger.LogError("Set: Tenant: " + tenantId + " not found.");
                new Exception("Set: Tenant: " + tenantId + " not found.");
            }
            return tenant.Set(newToken, tenantId, false);
        }
        
    }
}

