﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace yupisoft.ConfigServer.Core.Cluster
{

    public class ClusterManager
    {
        private Timer _timer;

        private ILogger _logger;
        private ConfigurationChanger _cfgChanger;
        private ConfigServerManager _cfgServer;

        private int _NodesMonitoringHeartbeat = 2000; // Milliseconds
        private int _NodesMonitoringMaxAttempts = 3;
        private int _NodesMonitoringSkipAttemptsOnFail = 3;

        
        private List<Node> _nodes;    
        private SelfNode selfNode
        {
            get
            {
                lock (_nodes)
                {
                    if (_nodes == null) return null;
                    return _nodes.FirstOrDefault(n => n.Self) as SelfNode;
                }
            }
        }
        
        public Node[] GetNodes()
        {
            lock (_nodes)
            {
                return _nodes.ToArray();
            }
        }

        public ClusterManager(IOptions<ClusterConfigSection> clusterConfig, ILogger<ClusterManager> logger, ConfigurationChanger cfgChanger, ConfigServerManager cfgServer)
        {
            _cfgChanger = cfgChanger;
            _cfgServer = cfgServer;
            _cfgServer.DataChanged += _cfgServer_DataChanged;
            _logger = logger;
            _nodes = new List<Node>();
            _NodesMonitoringHeartbeat = clusterConfig.Value.NodesMonitoringInterval;
            _NodesMonitoringMaxAttempts = clusterConfig.Value.NodesMonitoringMaxAttempts;
            _NodesMonitoringSkipAttemptsOnFail = clusterConfig.Value.NodesMonitoringSkipAttemptsOnFail;

            var nodesConfig = clusterConfig.Value.Nodes;
            foreach(var node in nodesConfig)
            {
                if (node.Enabled)
                {
                    Node newNode = null;
                    if (clusterConfig.Value.OwnNodeName == (node.Id))
                        newNode = new SelfNode() { Id = node.Id, Active = true, Address = node.Address, NodeConfig = node };
                    else
                        newNode = new Node() { Id = node.Id, Active = true, Address = node.Address, NodeConfig = node };

                    _nodes.Add(newNode);
                }
            }

            if (selfNode == null)
            {
                var newNode = new SelfNode() { Id = clusterConfig.Value.OwnNodeName, Active = true, Address = clusterConfig.Value.OwnNodeUrl, NodeConfig = new NodeConfigSection(){ Id = clusterConfig.Value.OwnNodeName, Address= clusterConfig.Value.OwnNodeUrl, Enabled = true } };
                _nodes.Add(newNode);
            }

            if (selfNode == null)
            {
                _logger.LogCritical("The current node is not in the Nodes List.");
                Environment.Exit(1);
            }

            _timer = new Timer(new TimerCallback(Timer_Elapsed), _nodes, Timeout.Infinite, _NodesMonitoringHeartbeat);
            _logger.LogInformation("Created ClusterManager with " + _nodes.Count + " nodes.");
        }

        private void _cfgServer_DataChanged(int tenantId, string entity, JToken diffToken)
        {
            _logger.LogInformation("Data changed Tenant " + tenantId + " entity: " + entity + " diff:" + diffToken?.ToString());

            LogMessage lm = new LogMessage() {Created = DateTime.UtcNow, Entity = entity, TenantId = tenantId, JsonDiff = diffToken?.ToString(Formatting.None) };
            lock (selfNode)
            {                
                lm.LogId = (selfNode.LogMessages.Count > 0) ? (selfNode.LogMessages.Last().LogId + 1) : 1;
                selfNode.LogMessages.Add(lm);
            }
        }

        public List<int> TenantsToUpgrade(KeyValuePair<int,string>[] dataHash)
        {
            List<int> TenantsToUpgrade = new List<int>();
            var DataHash = _cfgServer.TenantManager.Tenants.Select(p => new KeyValuePair<int, string>(p.TenantConfig.Id, p.DataHash)).ToArray();
            // Check DataHash
            foreach (var dh in DataHash)
            {
                if (dataHash.FirstOrDefault(e => e.Key == dh.Key).Value != dh.Value)
                    TenantsToUpgrade.Add(dh.Key);
            }
            return TenantsToUpgrade;
        }

        public HeartBeatMessageResponse ProcessHeartBeat(HeartBeatMessageRequest request)
        {
            lock (selfNode)
            {
                HeartBeatMessageResponse response = new HeartBeatMessageResponse();
                response.Created = DateTime.UtcNow;
                response.NodeAliveSince = _cfgServer.AliveSince;
                response.LastLogId = selfNode.LastLogId;
                response.NodeId = selfNode.Id;
                UpdateNodes(request.Nodes);

                if (selfNode.InUse)
                {
                    _logger.LogTrace("Node in Use; Ignoring HeartBeat: " + request.Command);
                    response.Command = HeartBeartCommand.InUse;
                    response.LastLogId = selfNode.LastLogId;
                    return response;
                }

                if (request.Command == HeartBeartCommand.SyncRequest)
                {
                    _logger.LogTrace("SyncRequest received from Node: " + request.NodeId);
                    response.Command = HeartBeartCommand.SyncResponse;

                    response.Log = selfNode.LogMessages.Where(l => l.LogId > request.LastLogId).ToList();
                    return response;
                }

                if (request.Command == HeartBeartCommand.FullSyncRequest)
                {
                    _logger.LogTrace("FullSyncRequest received from Node: " + request.NodeId);
                    response.Command = HeartBeartCommand.FullSyncResponse;
                    if (request.Log.Count > 0)
                    {
                        List<LogMessage> logToUpdate = new List<LogMessage>();
                        foreach (LogMessage msg in request.Log)
                        {
                            ConfigServerTenant tenant = _cfgServer.TenantManager.Tenants.FirstOrDefault(t => t.TenantConfig.Id == msg.TenantId);
                            LogMessage lmsg = new LogMessage()
                            {
                                Created = DateTime.UtcNow,
                                LogId = selfNode.LastLogId,
                                Full = true,
                                Entity = tenant.StartEntityName,
                                TenantId = tenant.TenantConfig.Id,
                                JsonDiff = _cfgServer.GetRaw("", tenant.StartEntityName, tenant.TenantConfig.Id).Value.ToString()
                            };
                            logToUpdate.Add(lmsg);
                        }
                        response.Log = logToUpdate;
                    }
                    return response;
                }

                if (request.Command == HeartBeartCommand.HeartBeatRequest)
                {
                    _logger.LogTrace("HeartBeatRequest received from Node: " + request.NodeId);
                    response.Command = HeartBeartCommand.HeartBeatResponse;
                    // Si el nodo tiene Log 0 y fue creado despues que el nodo que hizo el request, deberemos comprobar el hash
                    if ((selfNode.LastLogId == 0) && (_cfgServer.AliveSince > request.NodeAliveSince))
                    {
                        List<int> upgrade = TenantsToUpgrade(request.DataHash);

                        if (upgrade.Count > 0)
                        {
                            selfNode.Status = SelfNodeStatus.Unsyncronized;
                            // Tienen diferente hash, pedire una sincronizacion completa
                            HeartBeatSyncRequest(request.NodeId, response, true, upgrade.ToArray());
                        }
                    }
                    else
                    if (selfNode.LastLogId > request.LastLogId) // My LogId is updated
                    {
                        _logger.LogTrace("Remote Node: " + request.NodeId + " needs upgrade, SelfLog: " + selfNode.LastLogId + " versus:" + request.LastLogId);
                    }
                    else
                    if (selfNode.LastLogId < request.LastLogId) // My LogId is not updated
                    {
                        try
                        {
                            _logger.LogTrace("Self Node needs upgrade from: " + request.NodeId + ", SelfLog: " + selfNode.LastLogId + " versus:" + request.LastLogId);
                            selfNode.Status = SelfNodeStatus.Unsyncronized;

                            HeartBeatSyncRequest(request.NodeId, response, false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogCritical("Exception procesing node: " + request.NodeId + ex.ToString());
                        }
                    }
                    else
                    if ((selfNode.LastLogId == request.LastLogId) && (selfNode.LastLogId > 0) && (selfNode.LastLogDate < request.LastLogDate))
                    {
                        try
                        {
                            List<int> upgrade = TenantsToUpgrade(request.DataHash);

                            if (upgrade.Count > 0)
                            {
                                selfNode.Status = SelfNodeStatus.Unsyncronized;
                                HeartBeatSyncRequest(request.NodeId, response, true, upgrade.ToArray());
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogCritical("Exception procesing node: " + request.NodeId + ex.ToString());
                        }
                    }
                    else
                    {
                        //_logger.LogCritical("Nothing to do to handle node: " + request.NodeId);
                        //if (_cfgServer.AliveSince > request.NodeAliveSince)
                        //{
                        //    // El nodo que esta haciendo el request fue creado antes, so, voy a verificar si el hash mio es igual que el de el.
                        //    List<int> upgrade = TenantsToUpgrade(request.DataHash);

                        //    if (upgrade.Count > 0)
                        //    {
                        //        selfNode.Status = SelfNodeStatus.Unsyncronized;
                        //        HeartBeatSyncRequest(request.NodeId, response, true, upgrade.ToArray());
                        //    }
                        //}
                    }
                    return response;
                }

                response.Command = HeartBeartCommand.Unknow;
                response.Result = HeartBeartCommandResult.Error;
                _logger.LogTrace("Unknow command: " + request.Command.ToString());
                return response;
            }
        }

        public void HeartBeatSyncRequest(string NodeId, HeartBeatMessageResponse response, bool fullSync, int[] tenants = null)
        {
            HeartBeatMessageRequest req = new HeartBeatMessageRequest();
            req.Command = (fullSync ? HeartBeartCommand.FullSyncRequest : HeartBeartCommand.SyncRequest);
            req.Created = DateTime.UtcNow;
            req.NodeId = selfNode.Id;
            req.NodeAliveSince = _cfgServer.AliveSince;
            req.LastLogId = selfNode.LastLogId;
            req.LastLogDate = selfNode.LastLogDate;
            req.DataHash = _cfgServer.TenantManager.Tenants.Select(p => new KeyValuePair<int, string>(p.TenantConfig.Id, p.DataHash)).ToArray();

            lock (_nodes) req.Nodes = _nodes.Select(e => e.NodeConfig).ToArray();
            if (tenants != null)
            {
                foreach (int t in tenants)
                {
                    req.Log.Add(new LogMessage() { Created = DateTime.UtcNow, Entity = "default", TenantId = t, JsonDiff = null, LogId = 0 });
                }
            }
            string msgData = JsonConvert.SerializeObject(req);
            HttpClient client = new HttpClient();
            Node requestNode = null;
            lock (_nodes) { requestNode = _nodes.FirstOrDefault(n => n.Id == NodeId); }
            if (requestNode == null)
            {
                _logger.LogError("Node: " + NodeId + " not found in node list. Aborting upgrade.");
                response.Result = HeartBeartCommandResult.Error;
            }
            else
            {
                _logger.LogTrace((fullSync? "FullSyncRequest": "SyncRequest") + " --> " + requestNode.Id);
                response.Result = HeartBeartCommandResult.Success;
                selfNode.InUse = true;
                requestNode.InUse = true;
                client.PostAsync(requestNode.Address + "/api/Cluster/HeartBeat", new StringContent(msgData, Encoding.UTF8, "application/json")).ContinueWith((a) =>
                {
                    lock (selfNode)
                    {
                        try
                        {
                            _logger.LogTrace("Received from " + requestNode.Id + " HeartBeat<Sync> ST:" + a.Status);
                            if ((a.Status == TaskStatus.RanToCompletion) && (a.Result.IsSuccessStatusCode))
                            {
                                ApiSingleResult<HeartBeatMessageResponse> rsMsg = JsonConvert.DeserializeObject<ApiSingleResult<HeartBeatMessageResponse>>(a.Result.Content.ReadAsStringAsync().Result);
                                if (rsMsg.Item == null)
                                {
                                    _logger.LogError("Node: " + requestNode.Id + " do not return valid response. <null>");
                                    return;
                                }

                                if ((rsMsg.Item.Command != HeartBeartCommand.SyncResponse) && (rsMsg.Item.Command != HeartBeartCommand.FullSyncResponse))
                                {
                                    _logger.LogError("Node: " + requestNode.Id + " do not return valid command: " + rsMsg.Item.Command.ToString());
                                    return;
                                }

                                if (rsMsg.Item.Log == null)
                                {
                                    _logger.LogError("Node: " + requestNode.Id + " do not return valid response <log null>. ");
                                    return;
                                }

                                if (rsMsg.Item.Log.Count == 0)
                                {
                                    _logger.LogError("Node: " + requestNode.Id + " do not return valid response <log empty>. ");
                                    return;
                                }

                                IEnumerable<LogMessage> logsToApply = null;

                                if (rsMsg.Item.Command == HeartBeartCommand.SyncResponse)
                                {
                                    logsToApply = rsMsg.Item.Log.Where(l => l.LogId > selfNode.LastLogId);
                                    foreach (var log in logsToApply)
                                    {
                                        bool applied = false;
                                        if (log.Full)
                                            applied = _cfgServer.Set(new TNode("", JToken.Parse(log.JsonDiff), log.Entity), log.TenantId, true);
                                         else 
                                            applied = _cfgServer.ApplyUpdate(log.TenantId, log.Entity, log.JsonDiff);
                                        if (applied) selfNode.LogMessages.Add(log);
                                    }
                                    _logger.LogInformation("Applied " + logsToApply.Count() + " logs successfully.");
                                }

                                if (rsMsg.Item.Command == HeartBeartCommand.FullSyncResponse)
                                {
                                    foreach (var log in rsMsg.Item.Log)
                                    {
                                        TNode node = new TNode("", JToken.Parse(log.JsonDiff), log.Entity);
                                        bool applied = _cfgServer.Set(node, log.TenantId, true);
                                        if (applied) selfNode.LogMessages.Add(log);
                                    }
                                    _logger.LogInformation("FullSync Applied successfully.");
                                }
                                
                            }
                            else
                            {
                                _logger.LogError("Unable to contact: " + requestNode.Id + " for sync. ");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogCritical("Exception procesing response from node: " + requestNode.Id + "\r\n" + ex.ToString());
                        }
                        finally
                        {
                            requestNode.InUse = false;
                            selfNode.InUse = false;
                        }
                    }
                });
            }
        }

        public void UpdateNodes(NodeConfigSection[] nodesConfig)
        {
            if (nodesConfig == null) return;
            Node[] nodes = GetNodes();
            foreach (var node in nodesConfig)
            {
                if (node.Enabled)
                {
                    var foundNode = nodes.FirstOrDefault(e => e.Id == node.Id);
                    if (foundNode != null)
                    {
                        foundNode.Active = true;
                    }
                    else
                    {
                        var newNode = new Node() { Id = node.Id, Active = true, Address = node.Address, Self = false, NodeConfig = node };
                        lock (_nodes)
                        {
                            _logger.LogInformation("Adding New Node Id: " + newNode.Id + ".");
                            _nodes.Add(newNode);
                            _cfgChanger.AddClusterNode(node);
                        }
                    }
                }
            }
        }

        public void HeartBeat(Node node)
        {
            if ((node.Self) || (node.InUse)) return;
            if (node.SkipAttempts > 0)
            {
                node.SkipAttempts--;
                return;
            }
            try
            {
                // ==========================================================================
                //  Create Request Message here.
                // ==========================================================================

                lock (selfNode)
                {
                    HeartBeatMessageRequest request = new HeartBeatMessageRequest();
                    request.Created = DateTime.UtcNow;
                    request.Command = HeartBeartCommand.HeartBeatRequest;
                    request.NodeId = selfNode.Id;
                    request.NodeAliveSince = _cfgServer.AliveSince;
                    request.LastLogDate = selfNode.LastLogDate;
                    request.LastLogId = selfNode.LastLogId;
                    request.DataHash = _cfgServer.TenantManager.Tenants.Select(p => new KeyValuePair<int, string>(p.TenantConfig.Id, p.DataHash)).ToArray();
                    lock (_nodes) request.Nodes = _nodes.Select(e => e.NodeConfig).ToArray();

                    string msgData = JsonConvert.SerializeObject(request);
                    HttpClient client = new HttpClient();
                    _logger.LogTrace("HeartBeat --> " + node.Id + " Hash:" + request.DataHash[0].Value + " Log:" + selfNode.LastLogId);
                    node.InUse = true;

                    client.PostAsync(node.Address + "/api/Cluster/Heartbeat", new StringContent(msgData, Encoding.UTF8, "application/json")).ContinueWith((a) =>
                    {
                        lock (node)
                        {
                            _logger.LogTrace("Received from: " + node.Id + " HeartBeat ST: " + a.Status);
                            try
                            {
                                if ((a.Status == TaskStatus.RanToCompletion) && (a.Result.IsSuccessStatusCode))
                                {
                                    ApiSingleResult<HeartBeatMessageResponse> rsMsg = JsonConvert.DeserializeObject<ApiSingleResult<HeartBeatMessageResponse>>(a.Result.Content.ReadAsStringAsync().Result);
                                    if (rsMsg.Item == null)
                                    {
                                        _logger.LogError("Node: " + node.Id + " return Invalid response. attempt: " + node.Attempts);
                                        node.Attempts++;
                                    }
                                    _logger.LogInformation("Heartbeat from " + node.Id + " successfully.");
                                    node.Attempts = 0;
                                    node.SkipAttempts = 0;
                                    node.ResetLife();
                                }
                                else
                                {
                                    node.Attempts++;
                                    _logger.LogError("Unable to contact: " + node.Id + " attempt: " + node.Attempts);
                                }

                                if (node.Attempts >= _NodesMonitoringMaxAttempts)
                                {
                                    if (node.Life == 0)
                                    {
                                        lock (_cfgChanger)
                                        {
                                            _cfgChanger.DisableClusterNode(node.Id);
                                            node.Active = false;
                                        }
                                        node.Attempts = 0;
                                        _logger.LogError("Node " + node.Id + " life out; Disabled. ");
                                    }
                                    else
                                    {
                                        node.SkipAttempts = _NodesMonitoringSkipAttemptsOnFail;
                                        _logger.LogError("Heartbeat failed to " + node.Id + " " + node.Attempts + " attempts; Skip: " + node.SkipAttempts);
                                        node.Attempts = 0;
                                        node.Life--;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogCritical("Exception heartbeating node: " + node.Id + ex.ToString());
                            }
                            finally
                            {
                                node.InUse = false;
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical("Exception processing node: " + node.Id + ex.ToString());
            }           

        }

        private void Timer_Elapsed(object state)
        {
            _timer.Change(Timeout.Infinite, _NodesMonitoringHeartbeat); // Disable the timer;
            Node[] nodes = null;
            lock (_nodes)
            {
                nodes = _nodes.ToArray();
            }
            foreach (var w in nodes)
            {
                lock (w)
                {
                    if (w.Active)
                    {
                        HeartBeat(w);
                    }
                }
            }
            _timer.Change(_NodesMonitoringHeartbeat, _NodesMonitoringHeartbeat); // Reenable the timer;
        }

        public void StartManaging()
        {
            _timer.Change(Timeout.Infinite, _NodesMonitoringHeartbeat);
            foreach (var w in _nodes)
                w.Active = true;
            _timer.Change(_NodesMonitoringHeartbeat, _NodesMonitoringHeartbeat);
        }

        public void StopManaging()
        {
            _timer.Change(Timeout.Infinite, _NodesMonitoringHeartbeat);
            foreach (var w in _nodes)
                w.Active = false;
            _timer.Change(_NodesMonitoringHeartbeat, _NodesMonitoringHeartbeat);
        }

        public void ClearNodes()
        {
            _nodes.Clear();
        }

    }
}