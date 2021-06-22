using ColossalFramework;
using HarmonyLib;
using ModsCommon;
using ModsCommon.Utilities;
using NodeController.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;

namespace NodeController
{
    public class Manager : IManager
    {
        private NodeData[] Buffer { get; set; }

        public Manager()
        {
            SingletonMod<Mod>.Logger.Debug("Create manager");
            Buffer = new NodeData[NetManager.MAX_NODE_COUNT];
        }
        private void Clear()
        {
            SingletonMod<Mod>.Logger.Debug("Clear manager");
            Buffer = new NodeData[NetManager.MAX_NODE_COUNT];
        }
        public NodeData InsertNode(NetTool.ControlPoint controlPoint, NodeStyleType nodeType = NodeStyleType.Crossing)
        {
            if (NetTool.CreateNode(controlPoint.m_segment.GetSegment().Info, controlPoint, controlPoint, controlPoint, NetTool.m_nodePositionsSimulation, 0, false, false, true, false, false, false, 0, out var nodeId, out _, out _, out _) != ToolBase.ToolErrors.None)
                return null;
            else
                nodeId.GetNode().m_flags |= NetNode.Flags.Middle | NetNode.Flags.Moveable;

            var info = controlPoint.m_segment.GetSegment().Info;
            var newNodeType = nodeType == NodeStyleType.Crossing && (info.m_netAI is not RoadBaseAI || info.PedestrianLanes() < 2) ? null : (NodeStyleType?)nodeType;
            var data = Create(nodeId, Options.Default, newNodeType);
            return data;
        }
        public NodeData this[ushort nodeId, bool create = false] => this[nodeId, create ? Options.Default : Options.None];
        public NodeData this[ushort nodeId, Options options]
        {
            get
            {
                if (Buffer[nodeId] is not NodeData data)
                    data = options.IsSet(Options.CreateThis) ? Create(nodeId, options) : null;

                return data;
            }
        }
        public SegmentEndData this[ushort nodeId, ushort segmentId, bool create = false] => this[nodeId, create] is NodeData data ? data[segmentId] : null;
        private NodeData Create(ushort nodeId, Options options, NodeStyleType? nodeType = null)
        {
            try
            {
                var data = new NodeData(nodeId, nodeType);
                Buffer[nodeId] = data;
                Update(nodeId, options);
                return data;
            }
            catch (NodeNotCreatedException)
            {
                return null;
            }
            catch (NodeStyleNotImplementedException)
            {
                return null;
            }
            catch (Exception error)
            {
                SingletonMod<Mod>.Logger.Error($"Cant create Node data #{nodeId}", error);
                return null;
            }
        }
        public void GetSegmentData(ushort segmentId, out SegmentEndData start, out SegmentEndData end)
        {
            ref var segment = ref segmentId.GetSegment();
            start = Buffer[segment.m_startNode]?[segmentId];
            end = Buffer[segment.m_endNode]?[segmentId];
        }

        public bool ContainsNode(ushort nodeId) => Buffer[nodeId] != null;
        public bool ContainsSegment(ushort segmentId)
        {
            ref var segment = ref segmentId.GetSegment();
            return Buffer[segment.m_startNode] != null || Buffer[segment.m_endNode] != null;
        }
        public SegmentEndData GetSegmentData(ushort segmentId, bool isStart)
        {
            ref var segment = ref segmentId.GetSegment();
            return Buffer[segment.GetNode(isStart)]?[segmentId];
        }

        public void UpdateAll()
        {
            foreach (var data in Buffer)
            {
                if (data != null)
                    Update(data.Id);
            }
        }
        public void Update(ushort nodeId, bool now = false)
        {
            var option = Options.UpdateLater | (now ? Options.UpdateNow : Options.None);
            Update(nodeId, option);
        }
        private void Update(ushort nodeId, Options options)
        {
            if ((options & Options.UpdateAll) != 0)
            {
                if (options.IsSet(Options.UpdateThisNow))
                {
                    GetUpdateList(nodeId, options & ~Options.UpdateLater, out var nodeIds, out var segmentIds);
                    UpdateNow(nodeIds.ToArray(), segmentIds.ToArray(), false);
                }
                if (options.IsSet(Options.UpdateThisLater))
                {
                    GetUpdateList(nodeId, options & ~Options.UpdateNow, out var nodeIds, out _);
                    AddToUpdate(nodeIds);
                }
            }
        }
        private void AddToUpdate(HashSet<ushort> nodeIds)
        {
            foreach (var nodeId in nodeIds)
                NetManager.instance.UpdateNode(nodeId);
        }

        private void GetUpdateList(ushort nodeId, Options nearbyOptions, out HashSet<ushort> nodeIds, out HashSet<ushort> segmentIds)
        {
            nodeIds = new HashSet<ushort>();
            segmentIds = new HashSet<ushort>();

            if (Buffer[nodeId] == null)
                return;

            nodeIds.Add(nodeId);
            var nodeSegmentIds = nodeId.GetNode().SegmentIds().ToArray();
            segmentIds.AddRange(nodeSegmentIds);

            if ((nearbyOptions & Options.UpdateNearby) != 0)
            {
                foreach (var segmentIs in nodeSegmentIds)
                {
                    var otherNodeId = segmentIs.GetSegment().GetOtherNode(nodeId);
                    if (this[otherNodeId, nearbyOptions & Options.CreateAll & ~Options.Nearby | Options.This] != null)
                        nodeIds.Add(otherNodeId);
                }
            }
        }

        public static void SimulationStep()
        {
            var manager = SingletonManager<Manager>.Instance;
            var nodeIds = NetManager.instance.GetUpdateNodes().Where(s => manager.ContainsNode(s)).ToArray();
            var segmentIds = NetManager.instance.GetUpdateSegments().Where(s => manager.ContainsSegment(s)).ToArray();

            UpdateNow(nodeIds, segmentIds, true);
        }
        private static void UpdateNow(ushort[] nodeIds, ushort[] segmentIds, bool updateFlags)
        {
            if (nodeIds.Length == 0)
                return;
#if DEBUG
            SingletonMod<Mod>.Logger.Debug($"Update now\nNodes:{string.Join(", ", nodeIds.Select(i => i.ToString()).ToArray())}\nSegments:{string.Join(", ", segmentIds.Select(i => i.ToString()).ToArray())}");
#endif
            var manager = SingletonManager<Manager>.Instance;

            foreach (var nodeId in nodeIds)
                manager.Buffer[nodeId].Update(updateFlags);

            foreach (var segmentId in segmentIds)
                SegmentEndData.UpdateBeziers(segmentId);

            foreach (var nodeId in nodeIds)
                SegmentEndData.UpdateMinLimits(manager.Buffer[nodeId]);

            foreach (var segmentId in segmentIds)
                SegmentEndData.UpdateMaxLimits(segmentId);

            foreach (var nodeId in nodeIds)
                manager.Buffer[nodeId].LateUpdate();
        }

        public static void ReleaseNodeImplementationPrefix(ushort node) => SingletonManager<Manager>.Instance.Buffer[node] = null;

        public XElement ToXml()
        {
            var config = new XElement(nameof(NodeController));

            config.AddAttr("V", SingletonMod<Mod>.Version);

            foreach (var data in Buffer)
            {
                if (data != null)
                    config.Add(data.ToXml());
            }

            return config;
        }
        public void FromXml(XElement config, NetObjectsMap map, bool update = false)
        {
            foreach (var nodeConfig in config.Elements(NodeData.XmlName))
            {
                var id = nodeConfig.GetAttrValue(nameof(NodeData.Id), (ushort)0);

                if (map.TryGetNode(id, out var targetId))
                    id = targetId;

                if (id != 0 && id <= NetManager.MAX_NODE_COUNT)
                {
                    try
                    {
                        var type = (NodeStyleType)nodeConfig.GetAttrValue("T", (int)NodeStyleType.Custom);
                        var data = new NodeData(id, type);
                        data.FromXml(nodeConfig, map);
                        Buffer[data.Id] = data;

                        if (update)
                            NetManager.instance.UpdateNode(data.Id);
                    }
                    catch (NodeNotCreatedException error)
                    {
                        SingletonMod<Mod>.Logger.Error($"Can't load Node data #{id}: {error.Message}");
                    }
                    catch (NodeStyleNotImplementedException error)
                    {
                        SingletonMod<Mod>.Logger.Error($"Can't load Node data #{id}: {error.Message}");
                    }
                    catch (Exception error)
                    {
                        SingletonMod<Mod>.Logger.Error($"Can't load Node data #{id}", error);
                    }
                }
            }
        }

        [Flags]
        public enum Options
        {
            None = 0,

            This = 1,
            Nearby = 2,

            Create = 4,
            CreateThis = This | Create,
            CreateNearby = Nearby | Create,
            CreateAll = CreateThis | CreateNearby,

            UpdateThisNow = 8,
            UpdateThisLater = 16,
            UpdateThis = UpdateThisNow | UpdateThisLater,

            UpdateNearbyNow = 32,
            UpdateNearbyLater = 64,
            UpdateNearby = UpdateNearbyNow | UpdateNearbyLater,

            UpdateNow = UpdateThisNow | UpdateNearbyNow,
            UpdateLater = UpdateThisLater | UpdateNearbyLater,
            UpdateAll = UpdateNow | UpdateLater,

            Default = CreateThis | CreateNearby | UpdateThis | UpdateNearbyLater,
        }
    }

    public class NodeNotCreatedException : Exception
    {
        public ushort Id { get; }

        public NodeNotCreatedException(ushort id) : base($"Node #{id} not created")
        {
            Id = id;
        }
    }
    public class NodeStyleNotImplementedException : NotImplementedException
    {
        public ushort Id { get; }
        public NetNode.Flags Flags { get; }

        public NodeStyleNotImplementedException(ushort id, NetNode.Flags flags) : base($"Node #{id} style {flags} not implemented")
        {
            Id = id;
            Flags = flags;
        }
    }
}
