using ColossalFramework;
using HarmonyLib;
using KianCommons;
using KianCommons.Patches;
using ModsCommon.Utilities;
using NodeController.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace NodeController.Patches
{
    public static class NetNodePatches
    {
        public static void RefreshJunctionDataPostfix(ref NetNode __instance, ref RenderManager.Instance data)
        {
            var nodeId = __instance.GetID();
            if (Manager.Instance[nodeId] is not NodeData blendData)
                return;

            if (blendData.ShouldRenderCenteralCrossingTexture)
                data.m_dataVector1.w = 0.01f;
        }

        public static IEnumerable<CodeInstruction> RefreshJunctionDataTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var codes = instructions.ToList();

            var ldarg_nodeID = TranspilerUtilities.GetLDArg(original, "nodeID");
            var ldarg_segmentID = BuildSegmentLDLocFromSTLoc(codes);
            var call_GetMinCornerOffset = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NetNodePatches), nameof(GetMinCornerOffset)));

            var minCornerOffsetField = AccessTools.Field(typeof(NetInfo), nameof(NetInfo.m_minCornerOffset));
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Ldfld && instruction.operand == minCornerOffsetField)
                {
                    yield return ldarg_nodeID;
                    yield return ldarg_segmentID;
                    yield return call_GetMinCornerOffset;
                }
            }
        }

        private static CodeInstruction BuildSegmentLDLocFromSTLoc(List<CodeInstruction> codes, int startIndex = 0, int count = 1)
        {
            var GetSegmentMethod = AccessTools.Method(typeof(NetNode), nameof(NetNode.GetSegment));
            int index = codes.Search(c => c.Calls(GetSegmentMethod), startIndex, count);
            index = codes.Search(c => c.IsStloc(), index);
            return codes[index].BuildLdLocFromStLoc();
        }
        private static float GetMinCornerOffset(float cornerOffset, ushort nodeId, ushort segmentId) => Manager.Instance[nodeId, segmentId]?.Offset ?? cornerOffset;

        public static IEnumerable<CodeInstruction> RenderInstanceTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase method)
        {
            var codes = instructions.ToList();
            PatchCheckFlags(codes, 2, method);

            return codes;
        }
        public static void PatchCheckFlags(List<CodeInstruction> codes, int occurance, MethodBase method)
        {
            int index = 0;
            var drawMeshMethod = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh), new[] { typeof(Mesh), typeof(Vector3), typeof(Quaternion), typeof(Material), typeof(int), typeof(Camera), typeof(int), typeof(MaterialPropertyBlock) });
            index = TranspilerUtilities.SearchInstruction(codes, new CodeInstruction(OpCodes.Call, drawMeshMethod), index, counter: occurance);

            var nodeMaterialField = AccessTools.Field(typeof(NetInfo.Node), nameof(NetInfo.Node.m_nodeMaterial));
            index = TranspilerUtilities.SearchInstruction(codes, new CodeInstruction(OpCodes.Ldfld, nodeMaterialField), index, dir: -1);
            int insertIndex3 = index + 1;

            var nodeMeshField = AccessTools.Field(typeof(NetInfo.Node), nameof(NetInfo.Node.m_nodeMesh));
            index = TranspilerUtilities.SearchInstruction(codes, new CodeInstruction(OpCodes.Ldfld, nodeMeshField), index, dir: -1);
            int insertIndex2 = index + 1;

            var checkRenderDistanceMethod = AccessTools.Method(typeof(RenderManager.CameraInfo), nameof(RenderManager.CameraInfo.CheckRenderDistance));
            index = TranspilerUtilities.SearchInstruction(codes, new CodeInstruction(OpCodes.Callvirt, checkRenderDistanceMethod), index, dir: -1);
            int insertIndex1 = index + 1; // at this point boloean is in stack

            var LDArg_NodeID = TranspilerUtilities.GetLDArg(method, "nodeID");
            var LDLoc_segmentID = BuildSegnentLDLocFromPrevSTLoc(codes, index, counter: 1);

            {
                var newInstructions = new[]
                {
                    LDArg_NodeID,
                    LDLoc_segmentID,
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NetNodePatches), nameof(CalculateMaterial))),
                };
                TranspilerUtilities.InsertInstructions(codes, newInstructions, insertIndex3);
            }

            {
                var newInstructions = new[]
                {
                    LDArg_NodeID,
                    LDLoc_segmentID,
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NetNodePatches), nameof(CalculateMesh))),
                };
                TranspilerUtilities.InsertInstructions(codes, newInstructions, insertIndex2);
            }

            {
                var newInstructions = new[]
                {
                    LDArg_NodeID,
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NetNodePatches), nameof(ShouldContinueMedian))),
                    new CodeInstruction(OpCodes.Or)
                };

                TranspilerUtilities.InsertInstructions(codes, newInstructions, insertIndex1);
            }
        }

        private static CodeInstruction BuildSegnentLDLocFromPrevSTLoc(List<CodeInstruction> codes, int index, int counter = 1)
        {
            var getSegmentMethod = AccessTools.Method(typeof(NetNode), nameof(NetNode.GetSegment));
            index = TranspilerUtilities.SearchInstruction(codes, new CodeInstruction(OpCodes.Call, getSegmentMethod), index, counter: counter, dir: -1);
            return codes[index + 1].BuildLdLocFromStLoc();
        }
        private static bool ShouldContinueMedian(ushort nodeID) => Manager.Instance[nodeID] is NodeData data && data.Type == NodeStyleType.Stretch;
        private static Material CalculateMaterial(Material material, ushort nodeId, ushort segmentId)
        {
            if (ShouldContinueMedian(nodeId))
                material = MaterialUtilities.ContinuesMedian(material, segmentId.GetSegment().Info, false);

            return material;
        }
        private static Mesh CalculateMesh(Mesh mesh, ushort nodeId, ushort segmentId)
        {
            if (ShouldContinueMedian(nodeId))
                mesh = MaterialUtilities.ContinuesMedian(mesh, segmentId.GetSegment().Info);

            return mesh;
        }
    }
}
