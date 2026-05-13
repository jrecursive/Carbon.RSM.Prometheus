using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace RustServerMetrics.HarmonyPatches
{
    [HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.PerformanceReport))]
    public class BasePlayer_PerformanceReport_Patch
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> originalInstructions, ILGenerator iLGenerator)
        {
            var jmpLabel = iLGenerator.DefineLabel();
            List<CodeInstruction> retList = new List<CodeInstruction>(originalInstructions);

            if (!TryFindPerformanceReportStore(retList, out var insertionIndex, out var reportLocalIndex, out var reportType))
            {
                UnityEngine.Debug.LogError("[ServerMetrics] Failed to find the insertion index for BasePlayer_PerformanceReport_Patch");
                return retList;
            }

            var returnIndex = retList.FindLastIndex(x => x.opcode == OpCodes.Ret);
            if (returnIndex < 0)
            {
                UnityEngine.Debug.LogError("[ServerMetrics] Failed to find the return target for BasePlayer_PerformanceReport_Patch");
                return retList;
            }

            var continueLabel = iLGenerator.DefineLabel();
            var methodInfo = typeof(BasePlayer_PerformanceReport_Patch)
                .GetMethod(nameof(OnPerformanceReport), BindingFlags.Static | BindingFlags.NonPublic);

            var labels = retList[insertionIndex].labels;
            var blocks = retList[insertionIndex].blocks;
            var needsLeave = HasExceptionBlocks(retList);
            retList[insertionIndex].labels = new List<Label>();
            retList[insertionIndex].labels.Add(continueLabel);
            retList[insertionIndex].blocks = new List<ExceptionBlock>();
            retList[returnIndex].labels.Add(jmpLabel);

            var insertedInstructions = new List<CodeInstruction>
            {
                CodeInstruction.LoadLocal(reportLocalIndex)
            };

            if (reportType != null && reportType.IsValueType)
            {
                insertedInstructions.Add(new CodeInstruction(OpCodes.Box, reportType));
            }

            insertedInstructions.AddRange(new[]
            {
                new CodeInstruction(OpCodes.Call, methodInfo)
            });

            if (needsLeave)
            {
                insertedInstructions.Add(new CodeInstruction(OpCodes.Brfalse, continueLabel));
                insertedInstructions.Add(new CodeInstruction(OpCodes.Leave, jmpLabel));
            }
            else
            {
                insertedInstructions.Add(new CodeInstruction(OpCodes.Brtrue, jmpLabel));
            }

            insertedInstructions[0].labels = labels;
            insertedInstructions[0].blocks = blocks;

            retList.InsertRange(insertionIndex, insertedInstructions);

            return retList;
        }

        private static bool HasExceptionBlocks(List<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.blocks.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindPerformanceReportStore(List<CodeInstruction> instructions, out int insertionIndex, out int reportLocalIndex, out Type reportType)
        {
            insertionIndex = -1;
            reportLocalIndex = -1;
            reportType = null;

            for (int i = 0; i < instructions.Count; i++)
            {
                if (!IsPerformanceReportReadCall(instructions[i], out var candidateReportType))
                {
                    continue;
                }

                if (TryFindStoredLocalAfter(instructions, i, candidateReportType, out var storeIndex, out reportLocalIndex, out reportType))
                {
                    insertionIndex = storeIndex + 1;
                    return insertionIndex < instructions.Count;
                }
            }

            return false;
        }

        private static bool IsPerformanceReportReadCall(CodeInstruction instruction, out Type reportType)
        {
            reportType = null;

            if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)
            {
                return false;
            }

            if (!(instruction.operand is MethodInfo method))
            {
                return false;
            }

            if (IsClientPerformanceReportJsonCall(method, out reportType))
            {
                return true;
            }

            if (IsProtoPerformanceReportReadCall(method, out reportType))
            {
                return true;
            }

            return false;
        }

        private static bool IsClientPerformanceReportJsonCall(MethodInfo method, out Type reportType)
        {
            reportType = null;

            if (method.Name != nameof(JsonConvert.DeserializeObject) ||
                method.DeclaringType?.FullName != typeof(JsonConvert).FullName)
            {
                return false;
            }

            if (method.IsGenericMethod)
            {
                var genericArguments = method.GetGenericArguments();
                if (genericArguments.Length == 1 && IsClientPerformanceReportType(genericArguments[0]))
                {
                    reportType = genericArguments[0];
                    return true;
                }
            }

            if (IsClientPerformanceReportType(method.ReturnType))
            {
                reportType = method.ReturnType;
                return true;
            }

            return false;
        }

        private static bool IsProtoPerformanceReportReadCall(MethodInfo method, out Type reportType)
        {
            reportType = null;

            if (method.Name != "Proto" || !method.IsGenericMethod)
            {
                return false;
            }

            var genericArguments = method.GetGenericArguments();
            if (genericArguments.Length != 1 || !IsProtoPerformanceReportType(genericArguments[0]))
            {
                return false;
            }

            reportType = genericArguments[0];
            return true;
        }

        private static bool TryFindStoredLocalAfter(List<CodeInstruction> instructions, int callIndex, Type callReportType, out int storeIndex, out int reportLocalIndex, out Type reportType)
        {
            storeIndex = -1;
            reportLocalIndex = -1;
            reportType = callReportType;

            for (int i = callIndex + 1; i < instructions.Count && i <= callIndex + 4; i++)
            {
                var instruction = instructions[i];
                if (instruction.opcode == OpCodes.Nop)
                {
                    continue;
                }

                if ((instruction.opcode == OpCodes.Unbox_Any || instruction.opcode == OpCodes.Castclass) &&
                    instruction.operand is Type castType)
                {
                    reportType = castType;
                    continue;
                }

                if (TryGetStoredLocalIndex(instruction, out reportLocalIndex))
                {
                    storeIndex = i;
                    return true;
                }

                return false;
            }

            return false;
        }

        private static bool TryGetStoredLocalIndex(CodeInstruction instruction, out int localIndex)
        {
            localIndex = -1;

            if (instruction.opcode == OpCodes.Stloc_0)
            {
                localIndex = 0;
                return true;
            }

            if (instruction.opcode == OpCodes.Stloc_1)
            {
                localIndex = 1;
                return true;
            }

            if (instruction.opcode == OpCodes.Stloc_2)
            {
                localIndex = 2;
                return true;
            }

            if (instruction.opcode == OpCodes.Stloc_3)
            {
                localIndex = 3;
                return true;
            }

            if (instruction.opcode != OpCodes.Stloc && instruction.opcode != OpCodes.Stloc_S)
            {
                return false;
            }

            if (instruction.operand is LocalBuilder localBuilder)
            {
                localIndex = localBuilder.LocalIndex;
                return true;
            }

            if (instruction.operand is int intIndex)
            {
                localIndex = intIndex;
                return true;
            }

            if (instruction.operand is byte byteIndex)
            {
                localIndex = byteIndex;
                return true;
            }

            if (instruction.operand is short shortIndex)
            {
                localIndex = shortIndex;
                return true;
            }

            return false;
        }

        private static bool OnPerformanceReport(object performanceReport)
        {
            var metricsLogger = MetricsLogger.Instance;
            if (metricsLogger == null || performanceReport == null)
            {
                return false;
            }

            if (performanceReport is ClientPerformanceReport clientPerformanceReport)
            {
                return metricsLogger.OnClientPerformanceReport(clientPerformanceReport);
            }

            return TryConvertProtoPerformanceReport(performanceReport, out clientPerformanceReport) &&
                   metricsLogger.OnClientPerformanceReport(clientPerformanceReport);
        }

        private static bool TryConvertProtoPerformanceReport(object performanceReport, out ClientPerformanceReport clientPerformanceReport)
        {
            clientPerformanceReport = default;

            if (!IsProtoPerformanceReportType(performanceReport.GetType()))
            {
                return false;
            }

            try
            {
                clientPerformanceReport = new ClientPerformanceReport
                {
                    request_id = (int)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.request_id)),
                    user_id = (string)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.user_id)),
                    fps_average = (float)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.fps_average)),
                    fps = (int)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.fps)),
                    frame_id = (int)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.frame_id)),
                    frame_time = (float)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.frame_time)),
                    frame_time_average = (float)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.frame_time_average)),
                    memory_system = (long)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.memory_system)),
                    memory_collections = (long)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.memory_collections)),
                    memory_managed_heap = (long)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.memory_managed_heap)),
                    realtime_since_startup = (float)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.realtime_since_startup)),
                    streamer_mode = (bool)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.streamer_mode)),
                    ping = (int)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.ping)),
                    tasks_invokes = (int)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.tasks_invokes)),
                    tasks_load_balancer = (int)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.tasks_load_balancer)),
                    workshop_skins_queued = (int)GetPerformanceReportField(performanceReport, nameof(ClientPerformanceReport.workshop_skins_queued))
                };

                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[ServerMetrics] Failed to read ProtoBuf.PerformanceReport fields");
                UnityEngine.Debug.LogException(ex);
                return false;
            }
        }

        private static object GetPerformanceReportField(object performanceReport, string fieldName)
        {
            var fieldInfo = performanceReport.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            if (fieldInfo == null)
            {
                throw new MissingFieldException(performanceReport.GetType().FullName, fieldName);
            }

            return fieldInfo.GetValue(performanceReport);
        }

        private static bool IsClientPerformanceReportType(Type type)
        {
            return type != null && type.FullName == typeof(ClientPerformanceReport).FullName;
        }

        private static bool IsProtoPerformanceReportType(Type type)
        {
            return type != null && type.FullName == "ProtoBuf.PerformanceReport";
        }
    }
}
