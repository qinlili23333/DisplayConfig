﻿using MartinGC94.DisplayConfig.Native.Enums;
using MartinGC94.DisplayConfig.Native;
using MartinGC94.DisplayConfig.Native.Structs;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Management.Automation;

namespace MartinGC94.DisplayConfig.API
{
    public sealed class DisplayConfig
    {
        public DISPLAYCONFIG_PATH_INFO[] PathArray { get; set; }
        public DISPLAYCONFIG_MODE_INFO[] ModeArray { get; set; }
        public DisplayConfigFlags Flags { get; set; }
        public DISPLAYCONFIG_TOPOLOGY_ID TopologyID { get; set; }
        /// <summary>
        /// Contains indexes to display paths that are, or can be enabled in <see cref="PathArray"/>
        /// The indexes are sorted by <see cref="SortPathIndexes(List{int}, DISPLAYCONFIG_PATH_INFO[], out DISPLAYCONFIG_TARGET_DEVICE_NAME[])"/>
        /// where the goal is to match the the order assigned to displays in the Settings app as closely as possible.
        /// Meaning that the first index should point to the path that connects to Display 1 in the settings app.
        /// </summary>
        public int[] AvailablePathIndexes { get; set; }
        /// <summary>
        /// Contains device name info of the connected displays. The order is the same as <see cref="AvailablePathIndexes"/>
        /// </summary>
        public DISPLAYCONFIG_TARGET_DEVICE_NAME[] AvailablePathNames { get; set; }
        internal bool isImportedConfig = false;

        internal const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xffffffff;
        internal const uint DISPLAYCONFIG_PATH_SOURCE_MODE_IDX_INVALID = 0xffff;
        internal const uint DISPLAYCONFIG_PATH_TARGET_MODE_IDX_INVALID = 0xffff;
        internal const uint DISPLAYCONFIG_PATH_DESKTOP_IMAGE_IDX_INVALID = 0xffff;
        internal const uint DISPLAYCONFIG_PATH_CLONE_GROUP_INVALID = 0xffff;
        private const DisplayConfigFlags DefaultConfigFlags = DisplayConfigFlags.QDC_ALL_PATHS | DisplayConfigFlags.QDC_VIRTUAL_MODE_AWARE;

        public static DisplayConfig GetConfig(DisplayConfigFlags flags = DefaultConfigFlags)
        {
            DISPLAYCONFIG_PATH_INFO[] pathArray;
            uint pathArraySize;
            DISPLAYCONFIG_MODE_INFO[] modeArray;
            uint modeArraySize;
            DISPLAYCONFIG_TOPOLOGY_ID topology;
            do
            {
                ReturnCode code = NativeMethods.GetDisplayConfigBufferSizes(flags, out pathArraySize, out modeArraySize);
                if (code != ReturnCode.ERROR_SUCCESS)
                {
                    throw new Win32Exception((int)code);
                }

                if (pathArraySize == 0 && modeArraySize == 0)
                {
                    throw new Exception("No display paths found. Is this a headless session?");
                }

                pathArray = new DISPLAYCONFIG_PATH_INFO[pathArraySize];
                modeArray = new DISPLAYCONFIG_MODE_INFO[modeArraySize];
                if (flags.HasFlag(DisplayConfigFlags.QDC_DATABASE_CURRENT))
                {
                    code = NativeMethods.QueryDisplayConfig(flags, ref pathArraySize, pathArray, ref modeArraySize, modeArray, out topology);
                }
                else
                {
                    code = NativeMethods.QueryDisplayConfig(flags, ref pathArraySize, pathArray, ref modeArraySize, modeArray, IntPtr.Zero);
                    topology = DISPLAYCONFIG_TOPOLOGY_ID.None;
                }

                if (code == ReturnCode.ERROR_SUCCESS)
                {
                    break;
                }

                if (code != ReturnCode.ERROR_INSUFFICIENT_BUFFER)
                {
                    throw new Win32Exception((int)code);
                }
            } while (true);

            Array.Resize(ref pathArray, (int)pathArraySize);
            Array.Resize(ref modeArray, (int)modeArraySize);
            List<int> unsortedIndexes = GetAvailablePathIndexes(pathArray);
            int[] availablePathIndexes = SortPathIndexes(unsortedIndexes, pathArray, out DISPLAYCONFIG_TARGET_DEVICE_NAME[] targetDeviceInfo);

            return new DisplayConfig()
            {
                PathArray = pathArray,
                ModeArray = modeArray,
                Flags = flags,
                TopologyID = topology,
                AvailablePathIndexes = availablePathIndexes,
                AvailablePathNames = targetDeviceInfo
            };
        }

        internal static DisplayConfig GetConfig(Cmdlet command, DisplayConfigFlags flags = DefaultConfigFlags)
        {
            DisplayConfig config;
            try
            {
                config = GetConfig(flags);
            }
            catch (Win32Exception error)
            {
                command.ThrowTerminatingError(new ErrorRecord(error, "GetDisplayConfigAPIError", Utils.GetErrorCategory(error), null));
                throw new Exception();
            }
            catch (Exception error)
            {
                var record = new ErrorRecord(error, "NoDisplayPaths", ErrorCategory.InvalidResult, null)
                {
                    ErrorDetails = new ErrorDetails(string.Empty)
                    {
                        RecommendedAction = "Log on with an interactive session."
                    }
                };
                command.ThrowTerminatingError(record);
                throw new Exception();
            }

            return config;
        }

        private static List<int> GetAvailablePathIndexes(DISPLAYCONFIG_PATH_INFO[] pathArray)
        {
            var result = new List<int>();
            var monitorIdTable = new Dictionary<LUID, HashSet<uint>>();
            for (int i = 0; i < pathArray.Length; i++)
            {
                DISPLAYCONFIG_PATH_INFO path = pathArray[i];
                if (!path.targetInfo.targetAvailable)
                {
                    continue;
                }

                if (!monitorIdTable.TryGetValue(path.targetInfo.adapterId, out HashSet<uint> idSet))
                {
                    idSet = new HashSet<uint>();
                    monitorIdTable.Add(path.targetInfo.adapterId, idSet);
                }

                if (idSet.Add(path.targetInfo.id))
                {
                    result.Add(i);
                }
            }

            return result;
        }

        /// <summary>
        /// Sorts the discovered path indexes, ideally it would match the display settings in Windows but they are undocumented.
        /// The next best thing is to sort by output tech and connectorInstance (which should increment by 1 for each display with the same output tech)
        /// </summary>
        private static int[] SortPathIndexes(List<int> indexes, DISPLAYCONFIG_PATH_INFO[] pathArray, out DISPLAYCONFIG_TARGET_DEVICE_NAME[] targetNameInfo)
        {
            var nameInfoTable = new Dictionary<int, DISPLAYCONFIG_TARGET_DEVICE_NAME>(indexes.Count);
            var adapterNameTable = new Dictionary<LUID, string>();
            foreach (int pathIndex in indexes)
            {
                DISPLAYCONFIG_PATH_TARGET_INFO targetInfo = pathArray[pathIndex].targetInfo;
                DISPLAYCONFIG_TARGET_DEVICE_NAME deviceNameInfo = DisplayTargetInfo.GetTargetDeviceName(targetInfo.adapterId, targetInfo.id);
                if (!adapterNameTable.ContainsKey(targetInfo.adapterId))
                {
                    adapterNameTable.Add(targetInfo.adapterId, AdapterName.GetAdapterName(targetInfo.adapterId));
                }
                nameInfoTable.Add(pathIndex, deviceNameInfo);
            }

            int[] result = indexes.OrderBy(i => adapterNameTable[pathArray[i].targetInfo.adapterId]).ThenBy(i =>
            {
                // A lower number means a higher priority.
                // Generally speaking Windows prioritizes like this:
                // 1: internal display. 2: PC specific connectors like DVI/Displayport. 3: HDMI/others.
                uint priority;
                switch (pathArray[i].targetInfo.outputTechnology)
                {
                    case DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL:
                        priority = 50;
                        break;

                    case DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED:
                        priority = 100;
                        break;

                    case DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI:
                        priority = 150;
                        break;

                    case DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL:
                        priority = 200;
                        break;

                    case DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI:
                        priority = 250;
                        break;

                    default:
                        priority = 300;
                        break;
                }

                priority += nameInfoTable[i].connectorInstance;

                return priority;
            }).ToArray();

            targetNameInfo = new DISPLAYCONFIG_TARGET_DEVICE_NAME[result.Length];
            for (int i = 0; i < result.Length; i++)
            {
                targetNameInfo[i] = nameInfoTable[result[i]];
            }

            return result;
        }

        public void ApplyConfig(SetDisplayConfigFlags flags)
        {
            uint modeArrayLength = ModeArray is null
                ? 0
                : (uint)ModeArray.Length;
            ReturnCode result = NativeMethods.SetDisplayConfig((uint)PathArray.Length, PathArray, modeArrayLength, ModeArray, flags);

            if (result != ReturnCode.ERROR_SUCCESS)
            {
                throw new Win32Exception((int)result);
            }
        }

        internal int GetDisplayIndex(uint displayId)
        {
            // The idea here is that users provide a 1-based display ID that usually matches the screen numbers shown in the Settings app
            // The AvailablePathIndexes array order should match the order in the Settings app,
            // but only if the QDC_ALL_PATHS flag was set, or if all connected displays are active.
            try
            {
                return AvailablePathIndexes[displayId - 1];
            }
            catch (IndexOutOfRangeException e)
            {
                throw new ArgumentException($"Failed to find display with id {displayId}", e);
            }
        }

        internal uint GetDisplayId(int index)
        {
            return (uint)Array.IndexOf(AvailablePathIndexes, index) + 1;
        }

        internal DISPLAYCONFIG_TARGET_DEVICE_NAME GetDeviceNameInfo(int displayIndex)
        {
            int index = Array.IndexOf(AvailablePathIndexes, displayIndex);
            return AvailablePathNames[index];
        }

        public DISPLAYCONFIG_ROTATION GetDisplayRotation(uint displayId)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            return PathArray[displayIndex].targetInfo.rotation;
        }

        public void SetDisplayRotation(uint displayId, DISPLAYCONFIG_ROTATION rotation)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            PathArray[displayIndex].targetInfo.rotation = rotation;
        }

        public DISPLAYCONFIG_SCALING GetDisplayScaling(uint displayId)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            return PathArray[displayIndex].targetInfo.scaling;
        }

        public void SetDisplayScaling(uint displayId, DISPLAYCONFIG_SCALING scale)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            PathArray[displayIndex].targetInfo.scaling = scale;
        }

        public double GetDisplayRefreshRate(uint displayId)
        {
            int displayIndex = GetDisplayIndex(displayId);
            DISPLAYCONFIG_RATIONAL result = GetDisplayRefreshRate(displayIndex);
            return result.AsDouble();
        }

        internal DISPLAYCONFIG_RATIONAL GetDisplayRefreshRate(int displayIndex)
        {
            ValidatePathIsActive(displayIndex);
            uint modeInfoIndex = PathArray[displayIndex].targetInfo.TargetModeInfoIdx;
            ValidateModeIndex(modeInfoIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET);
            return ModeArray[modeInfoIndex].modeInfo.targetMode.targetVideoSignalInfo.vSyncFreq;
        }

        public void SetDisplayRefreshRate(uint displayId, double refreshRate)
        {
            int displayIndex = GetDisplayIndex(displayId);
            var convertedRefreshRate = DISPLAYCONFIG_RATIONAL.FromDouble(refreshRate);
            SetDisplayRefreshRate(displayIndex, convertedRefreshRate);
        }

        internal void SetDisplayRefreshRate(int displayIndex, DISPLAYCONFIG_RATIONAL refreshRate)
        {
            ValidatePathIsActive(displayIndex);
            uint modeInfoIndex = PathArray[displayIndex].targetInfo.TargetModeInfoIdx;
            ValidateModeIndex(modeInfoIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET);
            ModeArray[modeInfoIndex].modeInfo.targetMode.targetVideoSignalInfo.vSyncFreq = refreshRate;
        }

        public DISPLAYCONFIG_2DREGION GetDisplayActiveSize(uint displayId)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            uint modeInfoIndex = PathArray[displayIndex].targetInfo.TargetModeInfoIdx;
            ValidateModeIndex(modeInfoIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET);
            return ModeArray[modeInfoIndex].modeInfo.targetMode.targetVideoSignalInfo.activeSize;
        }

        public void SetDisplayActiveSize(uint displayId, uint width, uint height)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            uint modeInfoIndex = PathArray[displayIndex].targetInfo.TargetModeInfoIdx;
            ValidateModeIndex(modeInfoIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET);
            ModeArray[modeInfoIndex].modeInfo.targetMode.targetVideoSignalInfo.activeSize.cx = width;
            ModeArray[modeInfoIndex].modeInfo.targetMode.targetVideoSignalInfo.activeSize.cy = height;
        }

        public DISPLAYCONFIG_SCANLINE_ORDERING GetDisplayScanlineOrder(uint displayId)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            uint modeInfoIndex = PathArray[displayIndex].targetInfo.TargetModeInfoIdx;
            ValidateModeIndex(modeInfoIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET);
            return ModeArray[modeInfoIndex].modeInfo.targetMode.targetVideoSignalInfo.scanLineOrdering;
        }

        public void SetDisplayScanlineOrder(uint displayId, DISPLAYCONFIG_SCANLINE_ORDERING order)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            uint modeInfoIndex = PathArray[displayIndex].targetInfo.TargetModeInfoIdx;
            ValidateModeIndex(modeInfoIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET);
            ModeArray[modeInfoIndex].modeInfo.targetMode.targetVideoSignalInfo.scanLineOrdering = order;
        }

        public DisplayResolution GetDisplayResolution(uint displayId)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            uint modeInfoIndex = PathArray[displayIndex].sourceInfo.SourceModeInfoIdx;
            ValidateModeIndex(modeInfoIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);
            DISPLAYCONFIG_SOURCE_MODE sourceMode = ModeArray[modeInfoIndex].modeInfo.sourceMode;
            return new DisplayResolution(sourceMode.width, sourceMode.height);
        }

        public void SetDisplayResolution(uint displayId, uint width, uint height)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            uint modeInfoIndex = PathArray[displayIndex].sourceInfo.SourceModeInfoIdx;
            ValidateModeIndex(modeInfoIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);
            
            var desktopMap = GetDesktopMap();
            int intWidth = (int)width;
            int intHeight = (int)height;
            
            foreach (int pathIndex in desktopMap[ModeArray[modeInfoIndex].modeInfo.sourceMode.position])
            {
                uint sourceIndex = PathArray[pathIndex].sourceInfo.SourceModeInfoIdx;
                uint desktopIndex = PathArray[pathIndex].targetInfo.DesktopModeInfoIdx;
                ModeArray[sourceIndex].modeInfo.sourceMode.width = width;
                ModeArray[sourceIndex].modeInfo.sourceMode.height = height;
                if (desktopIndex != DISPLAYCONFIG_PATH_DESKTOP_IMAGE_IDX_INVALID)
                {
                    ModeArray[desktopIndex].modeInfo.desktopImageInfo.desktopImageClip.right = intWidth;
                    ModeArray[desktopIndex].modeInfo.desktopImageInfo.desktopImageClip.bottom = intHeight;
                }
            }
        }

        public uint GetPrimaryDisplayId()
        {
            foreach (var index in AvailablePathIndexes)
            {
                DISPLAYCONFIG_PATH_INFO pathInfo = PathArray[index];
                if (!pathInfo.flags.HasFlag(PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE))
                {
                    continue;
                }

                uint modeIndex = pathInfo.sourceInfo.SourceModeInfoIdx;
                if (ModeArray[modeIndex].modeInfo.sourceMode.IsPrimary())
                {
                    return GetDisplayId(index);
                }
            }

            throw new Exception("Failed to find the primary display");
        }

        public void SetPrimaryDisplay(uint displayId)
        {
            int targetIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(targetIndex);

            uint targetModeIndex = PathArray[targetIndex].sourceInfo.SourceModeInfoIdx;
            ValidateModeIndex(targetModeIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);

            DISPLAYCONFIG_SOURCE_MODE sourceModeInfo = ModeArray[targetModeIndex].modeInfo.sourceMode;
            if (sourceModeInfo.IsPrimary())
            {
                return;
            }

            for (int i = 0; i < ModeArray.Length; i++)
            {
                if (ModeArray[i].infoType != DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                {
                    continue;
                }

                if (ModeArray[i].modeInfo.sourceMode.position.Equals(sourceModeInfo.position))
                {
                    ModeArray[i].modeInfo.sourceMode.position.x = 0;
                    ModeArray[i].modeInfo.sourceMode.position.y = 0;
                    continue;
                }

                ModeArray[i].modeInfo.sourceMode.position.x -= sourceModeInfo.position.x;
                ModeArray[i].modeInfo.sourceMode.position.y -= sourceModeInfo.position.y;
            }
        }

        /// <summary>
        /// Gets a dictionary map of the desktop positions and associated PathArray indexes.
        /// Cloned displays will have the same desktop positions so this is a way to see if a desktop has been cloned or not.
        /// </summary>
        private Dictionary<POINTL, HashSet<int>> GetDesktopMap()
        {
            var result = new Dictionary<POINTL, HashSet<int>>();
            foreach (var index in AvailablePathIndexes)
            {
                if (!PathArray[index].flags.HasFlag(PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE) ||
                    PathArray[index].sourceInfo.SourceModeInfoIdx == DISPLAYCONFIG_PATH_SOURCE_MODE_IDX_INVALID)
                {
                    continue;
                }

                POINTL desktopPosition = ModeArray[PathArray[index].sourceInfo.SourceModeInfoIdx].modeInfo.sourceMode.position;
                if (result.TryGetValue(desktopPosition, out HashSet<int> pathIndexes))
                {
                    pathIndexes.Add(index);
                }
                else
                {
                    result.Add(desktopPosition, new HashSet<int>() { index });
                }
            }

            return result;
        }

        public void CloneDisplay(uint sourceId, HashSet<uint> destinationIds)
        {
            if (destinationIds is null)
            {
                throw new ArgumentNullException(nameof(destinationIds));
            }

            if (destinationIds.Count == 0)
            {
                throw new ArgumentException("Need to specify at least 1 destination ID", nameof(destinationIds));
            }

            if (destinationIds.Contains(sourceId))
            {
                throw new ArgumentException("Cannot copy from the same source to the same destination ID");
            }

            int sourceIndex = GetDisplayIndex(sourceId);
            ValidatePathIsActive(sourceIndex);

            var destinationIndexes = new HashSet<int>();
            foreach (uint id in destinationIds)
            {
                int destinationIndex = GetDisplayIndex(id);
                ValidatePathIsActive(destinationIndex);
                _ = destinationIndexes.Add(destinationIndex);
            }

            uint sourceDesktopIndex = PathArray[sourceIndex].targetInfo.DesktopModeInfoIdx;
            Dictionary<POINTL, HashSet<int>> desktopMap = GetDesktopMap();
            DISPLAYCONFIG_SOURCE_MODE sourceDisplaySource = ModeArray[PathArray[sourceIndex].sourceInfo.SourceModeInfoIdx].modeInfo.sourceMode;
            RECTL sourceDisplayClip = sourceDesktopIndex == DISPLAYCONFIG_PATH_DESKTOP_IMAGE_IDX_INVALID
                ? default
                : ModeArray[sourceDesktopIndex].modeInfo.desktopImageInfo.desktopImageClip;

            var removedSources = new List<DISPLAYCONFIG_SOURCE_MODE>();

            foreach (int index in destinationIndexes)
            {
                DISPLAYCONFIG_SOURCE_MODE sourceToRemove = ModeArray[PathArray[index].sourceInfo.SourceModeInfoIdx].modeInfo.sourceMode;
                HashSet<int> indexesWithSameDesktop = desktopMap[sourceToRemove.position];
                indexesWithSameDesktop.ExceptWith(destinationIndexes);
                if (indexesWithSameDesktop.Count == 0)
                {
                    removedSources.Add(sourceToRemove);
                }

                ModeArray[PathArray[index].sourceInfo.SourceModeInfoIdx].modeInfo.sourceMode = sourceDisplaySource;
                if (sourceDesktopIndex != DISPLAYCONFIG_PATH_DESKTOP_IMAGE_IDX_INVALID)
                {
                    ModeArray[PathArray[index].targetInfo.DesktopModeInfoIdx].modeInfo.desktopImageInfo.desktopImageClip = sourceDisplayClip;
                }
            }

            foreach (DISPLAYCONFIG_SOURCE_MODE removedSource in removedSources)
            {
                if (removedSource.IsPrimary())
                {
                    SetPrimaryDisplay(sourceId);
                    continue;
                }

                AdjustDesktopAfterRemovedSource(removedSource);
            }
        }

        public void DisableDisplay(uint displayId)
        {
            int pathIndex = GetDisplayIndex(displayId);
            if (!PathArray[pathIndex].flags.HasFlag(PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE))
            {
                // Display is already disabled
                return;
            }

            uint sourceModeIndex = PathArray[pathIndex].sourceInfo.SourceModeInfoIdx;
            DISPLAYCONFIG_SOURCE_MODE displaySourceMode = ModeArray[sourceModeIndex].modeInfo.sourceMode;
            bool sourceIsCloned = false;
            foreach (int index in AvailablePathIndexes)
            {
                if (index == pathIndex)
                {
                    continue;
                }

                if (PathArray[index].sourceInfo.SourceModeInfoIdx != DISPLAYCONFIG_PATH_SOURCE_MODE_IDX_INVALID &&
                    ModeArray[PathArray[index].sourceInfo.SourceModeInfoIdx].modeInfo.sourceMode.position.Equals(displaySourceMode.position))
                {
                    sourceIsCloned = true;
                    break;
                }
            }

            if (!sourceIsCloned && ModeArray[sourceModeIndex].modeInfo.sourceMode.IsPrimary())
            {
                throw new Exception($"Cannot disable display {displayId} because it is the primary display.");
            }

            var newModeArray = new List<DISPLAYCONFIG_MODE_INFO>(ModeArray.Length - 2);
            foreach (int index in AvailablePathIndexes)
            {
                if (index == pathIndex || PathArray[index].sourceInfo.modeInfoIdx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
                {
                    continue;
                }

                newModeArray.Add(ModeArray[PathArray[index].targetInfo.TargetModeInfoIdx]);
                PathArray[index].targetInfo.TargetModeInfoIdx = (uint)(newModeArray.Count - 1);

                newModeArray.Add(ModeArray[PathArray[index].sourceInfo.SourceModeInfoIdx]);
                PathArray[index].sourceInfo.SourceModeInfoIdx = (uint)(newModeArray.Count - 1);

                uint desktopIndex = PathArray[index].targetInfo.DesktopModeInfoIdx;
                if (desktopIndex != DISPLAYCONFIG_PATH_DESKTOP_IMAGE_IDX_INVALID)
                {
                    newModeArray.Add(ModeArray[desktopIndex]);
                    PathArray[index].targetInfo.DesktopModeInfoIdx = (uint)(newModeArray.Count - 1);
                }
            }

            PathArray[pathIndex].sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            PathArray[pathIndex].targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            PathArray[pathIndex].flags &= ~PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE;

            ModeArray = newModeArray.ToArray();

            if (!sourceIsCloned)
            {
                AdjustDesktopAfterRemovedSource(displaySourceMode);
            }
        }

        public POINTL GetDisplayPosition(uint displayId)
        {
            int pathIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(pathIndex);
            uint modeIndex = PathArray[pathIndex].sourceInfo.SourceModeInfoIdx;
            ValidateModeIndex(modeIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);
            return ModeArray[modeIndex].modeInfo.sourceMode.position;
        }

        /// <summary>
        /// Sets the position of a display.
        /// If the display is cloned, the rest of the clonegroup is also repositioned.
        /// </summary>
        /// <remarks>
        /// This does not attempt to reposition other displays to fit the new position.
        /// It's the callers responsibility to make sure all desktop positions are valid (no overlapping displays, or gaps)
        /// </remarks>
        public void SetDisplayPosition(uint displayId, int x, int y)
        {
            int pathIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(pathIndex);
            uint modeIndex = PathArray[pathIndex].sourceInfo.SourceModeInfoIdx;
            
            if (x != int.MaxValue)
            {
                ModeArray[modeIndex].modeInfo.sourceMode.position.x = x;
            }

            if (y != int.MaxValue)
            {
                ModeArray[modeIndex].modeInfo.sourceMode.position.y = y;
            }
        }

        public void SetDisplayPositionLeftToRight(uint[] displayIDsInOrder)
        {
            if (displayIDsInOrder is null)
            {
                throw new ArgumentNullException(nameof(displayIDsInOrder));
            }

            var newModeArray = (DISPLAYCONFIG_MODE_INFO[])ModeArray.Clone();

            var desktopMap = GetDesktopMap();
            var positionMap = new Dictionary<POINTL, POINTL>();
            int xOffset = 0;
            uint primaryDisplayId = 0;
            foreach (uint displayId in displayIDsInOrder)
            {
                int displayIndex = GetDisplayIndex(displayId);
                ValidatePathIsActive(displayIndex);
                uint modeIndex = PathArray[displayIndex].sourceInfo.SourceModeInfoIdx;
                ValidateModeIndex(modeIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);

                if (primaryDisplayId == 0 && ModeArray[modeIndex].modeInfo.sourceMode.IsPrimary())
                {
                    primaryDisplayId = displayId;
                }

                foreach (int pathIndex in desktopMap[ModeArray[modeIndex].modeInfo.sourceMode.position])
                {
                    modeIndex = PathArray[pathIndex].sourceInfo.SourceModeInfoIdx;
                    if (positionMap.TryGetValue(ModeArray[modeIndex].modeInfo.sourceMode.position, out POINTL newPosition))
                    {
                        // Cloned displays share the same position so if we find the position in the table we just need to set the updated position.
                        newModeArray[modeIndex].modeInfo.sourceMode.position = newPosition;
                        continue;
                    }

                    newPosition = new POINTL() { x = xOffset, y = 0 };
                    positionMap.Add(ModeArray[modeIndex].modeInfo.sourceMode.position, newPosition);
                    newModeArray[modeIndex].modeInfo.sourceMode.position = newPosition;
                    xOffset += (int)ModeArray[modeIndex].modeInfo.sourceMode.width;
                }
            }

            ModeArray = newModeArray;
            if (primaryDisplayId > 0)
            {
                SetPrimaryDisplay(primaryDisplayId);
            }
        }

        private void ValidateModeIndex(uint modeInfoIndex, DISPLAYCONFIG_MODE_INFO_TYPE expectedType)
        {
            DISPLAYCONFIG_MODE_INFO_TYPE modeType;
            try
            {
                modeType = ModeArray[modeInfoIndex].infoType;
            }
            catch (IndexOutOfRangeException e)
            {
                throw new Exception($"Mode index {modeInfoIndex} did not point to a valid entry in the ModeArray.", e);
            }

            if (modeType != expectedType)
            {
                throw new Exception($"The mode entry at {modeInfoIndex} is a {modeType} instead of the expected {expectedType}.");
            }
        }

        internal void ValidatePathIsActive(int pathIndex)
        {
            if (!PathArray[pathIndex].flags.HasFlag(PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE))
            {
                throw new Exception($"Display ID '{GetDisplayId(pathIndex)}' is not active");
            }
        }

        /// <summary>
        /// Rearranges desktop position of the sources in <see cref="ModeArray"/> to close any gaps left after removing a source (disabling or cloning a display)
        /// </summary>
        /// <param name="oldSourceMode">The source mode of the display that has been removed.</param>
        private void AdjustDesktopAfterRemovedSource(DISPLAYCONFIG_SOURCE_MODE oldSourceMode)
        {
            for (int i = 0; i < ModeArray.Length; i++)
            {
                if (ModeArray[i].infoType != DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                {
                    continue;
                }

                POINTL position = ModeArray[i].modeInfo.sourceMode.position;
                POINTL oldSourcePosition = oldSourceMode.position;
                if (oldSourcePosition.x > 0)
                {
                    if (position.x > oldSourcePosition.x)
                    {
                        ModeArray[i].modeInfo.sourceMode.position.x -= (int)oldSourceMode.width;
                    }
                }
                else if (oldSourcePosition.x < 0 && position.x < oldSourcePosition.x)
                {
                    ModeArray[i].modeInfo.sourceMode.position.x += (int)oldSourceMode.width;
                }

                if (oldSourcePosition.y > 0)
                {
                    if (position.y > oldSourcePosition.y)
                    {
                        ModeArray[i].modeInfo.sourceMode.position.y -= (int)oldSourceMode.height;
                    }
                }
                else if (oldSourcePosition.y < 0 && position.y < oldSourcePosition.y)
                {
                    ModeArray[i].modeInfo.sourceMode.position.y += (int)oldSourceMode.height;
                }
            }
        }

        /// <summary>
        /// Moves the specified display to a <see cref="RelativePosition"/> relative to the other specified display.
        /// </summary>
        /// <remarks>
        /// This does not attempt to reposition other displays to fit the new position.
        /// It's the callers responsibility to make sure all desktop positions are valid (no overlapping displays, or gaps)
        /// </remarks>
        public void MoveDisplayPosition(uint displayId, uint relativeDisplayId, RelativePosition position)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);

            int relativeDisplayIndex = GetDisplayIndex(relativeDisplayId);
            ValidatePathIsActive(relativeDisplayIndex);

            uint displayModeIndex = PathArray[displayIndex].sourceInfo.SourceModeInfoIdx;
            ValidateModeIndex(displayModeIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);
            uint relativeDisplayModeIndex = PathArray[relativeDisplayIndex].sourceInfo.SourceModeInfoIdx;
            ValidateModeIndex(relativeDisplayModeIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);
            
            if (ModeArray[displayModeIndex].modeInfo.sourceMode.position.Equals(ModeArray[relativeDisplayModeIndex].modeInfo.sourceMode.position))
            {
                throw new Exception("Both displays are in the same clone group.");
            }

            bool resetPrimary = false;
            if (ModeArray[displayModeIndex].modeInfo.sourceMode.IsPrimary())
            {
                // Temporarily change the primary display so I don't have to deal with a new offset for every other display.
                resetPrimary = true;
                SetPrimaryDisplay(relativeDisplayId);
            }

            DISPLAYCONFIG_SOURCE_MODE oldSourceMode = ModeArray[displayModeIndex].modeInfo.sourceMode;
            DISPLAYCONFIG_SOURCE_MODE relativeSourceMode = ModeArray[relativeDisplayModeIndex].modeInfo.sourceMode;
            var desktopMap = GetDesktopMap();
            foreach (var index in desktopMap[oldSourceMode.position])
            {
                displayModeIndex = PathArray[index].sourceInfo.SourceModeInfoIdx;
                switch (position)
                {
                    case RelativePosition.Left:
                        ModeArray[displayModeIndex].modeInfo.sourceMode.position.x = relativeSourceMode.position.x - (int)oldSourceMode.width;
                        ModeArray[displayModeIndex].modeInfo.sourceMode.position.y = relativeSourceMode.position.y;
                        break;

                    case RelativePosition.Right:
                        ModeArray[displayModeIndex].modeInfo.sourceMode.position.x = relativeSourceMode.position.x + (int)relativeSourceMode.width;
                        ModeArray[displayModeIndex].modeInfo.sourceMode.position.y = relativeSourceMode.position.y;
                        break;

                    case RelativePosition.Above:
                        ModeArray[displayModeIndex].modeInfo.sourceMode.position.x = relativeSourceMode.position.x;
                        ModeArray[displayModeIndex].modeInfo.sourceMode.position.y = relativeSourceMode.position.y - (int)oldSourceMode.height;
                        break;

                    case RelativePosition.Under:
                        ModeArray[displayModeIndex].modeInfo.sourceMode.position.x = relativeSourceMode.position.x;
                        ModeArray[displayModeIndex].modeInfo.sourceMode.position.y = relativeSourceMode.position.y + (int)relativeSourceMode.height;
                        break;

                    default:
                        break;
                }
            }
            

            if (resetPrimary)
            {
                SetPrimaryDisplay(displayId);
            }
        }

        /// <summary>
        /// Moves the specified display X and Y specified amount of pixels from its current location.
        /// </summary>
        /// <remarks>
        /// This does not attempt to reposition other displays to fit the new position.
        /// It's the callers responsibility to make sure all desktop positions are valid (no overlapping displays, or gaps)
        /// </remarks>
        public void MoveDisplayPosition(uint displayId, int xOffset, int yOffset)
        {
            int displayIndex = GetDisplayIndex(displayId);
            ValidatePathIsActive(displayIndex);
            uint displayModeIndex = PathArray[displayIndex].sourceInfo.SourceModeInfoIdx;
            ValidateModeIndex(displayModeIndex, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);

            uint tempPrimaryId = 0;
            if (ModeArray[displayModeIndex].modeInfo.sourceMode.IsPrimary())
            {
                // Temporarily change the primary display so I don't have to deal with a new offset for every other display.
                foreach (int index in AvailablePathIndexes)
                {
                    if (!PathArray[index].flags.HasFlag(PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE) ||
                        PathArray[index].sourceInfo.SourceModeInfoIdx == displayModeIndex)
                    {
                        continue;
                    }

                    tempPrimaryId = GetDisplayId(index);
                    break;
                }

                if (tempPrimaryId == 0)
                {
                    // There is only 1 active display, so it doesn't make sense to adjust positions.
                    return;
                }

                SetPrimaryDisplay(tempPrimaryId);
            }

            var desktopMap = GetDesktopMap();
            foreach (int pathIndex in desktopMap[ModeArray[displayModeIndex].modeInfo.sourceMode.position])
            {
                displayModeIndex = PathArray[pathIndex].sourceInfo.SourceModeInfoIdx;
                if (xOffset != int.MaxValue)
                {
                    ModeArray[displayModeIndex].modeInfo.sourceMode.position.x += xOffset;
                }

                if (yOffset != int.MaxValue)
                {
                    ModeArray[displayModeIndex].modeInfo.sourceMode.position.y += yOffset;
                }
            }
            

            if (tempPrimaryId != 0)
            {
                SetPrimaryDisplay(displayId);
            }
        }

        /// <summary>
        /// Swaps the desktop positions of the specified displays.
        /// </summary>
        /// <remarks>
        /// This does not attempt to reposition other displays to fit the new position.
        /// It's the callers responsibility to make sure all desktop positions are valid (no overlapping displays, or gaps)
        /// </remarks>
        public void SwapDisplayPosition(uint displayId1, uint displayId2)
        {
            int displayIndex1 = GetDisplayIndex(displayId1);
            ValidatePathIsActive(displayIndex1);
            uint displayModeIndex1 = PathArray[displayIndex1].sourceInfo.SourceModeInfoIdx;
            ValidateModeIndex(displayModeIndex1, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);

            int displayIndex2 = GetDisplayIndex(displayId2);
            ValidatePathIsActive(displayIndex2);
            uint displayModeIndex2 = PathArray[displayIndex2].sourceInfo.SourceModeInfoIdx;
            ValidateModeIndex(displayModeIndex2, DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);

            if (ModeArray[displayModeIndex1].modeInfo.sourceMode.position.Equals(ModeArray[displayModeIndex2].modeInfo.sourceMode.position))
            {
                throw new Exception("Both displays are in the same clone group.");
            }

            var oldSource1 = ModeArray[displayModeIndex1].modeInfo.sourceMode;
            var oldSource2 = ModeArray[displayModeIndex2].modeInfo.sourceMode;

            var displayMap = GetDesktopMap();
            foreach (int pathIndex in displayMap[oldSource1.position])
            {
                displayModeIndex1 = PathArray[pathIndex].sourceInfo.SourceModeInfoIdx;
                ModeArray[displayModeIndex1].modeInfo.sourceMode.position = oldSource2.position;
            }

            foreach (int pathIndex in displayMap[oldSource2.position])
            {
                displayModeIndex2 = PathArray[pathIndex].sourceInfo.SourceModeInfoIdx;
                ModeArray[displayModeIndex2].modeInfo.sourceMode.position = oldSource1.position;
            }

            if (oldSource1.IsPrimary())
            {
                SetPrimaryDisplay(displayId1);
            }
            else if (oldSource2.IsPrimary())
            {
                SetPrimaryDisplay(displayId2);
            }
        }

        /// <summary>
        /// Updates all the display adapter ID references in the displayconfig to the adapter IDs found in a new display config scan.
        /// This is useful when importing an old serialized config where the adapter IDs may have changed.
        /// </summary>
        public void UpdateAdapterIds()
        {
            var newConfig = GetConfig(Flags);
            if (newConfig.AvailablePathNames.Length != AvailablePathNames.Length)
            {
                throw new Exception($"Cannot update adapter IDs because the amount of connected displays has changed. Old: {AvailablePathNames.Length} New: {newConfig.AvailablePathNames.Length}");
            }

            var luidMap = new Dictionary<LUID, LUID>(AvailablePathNames.Length);
            for (int i = 0; i < AvailablePathNames.Length; i++)
            {
                if (luidMap.TryGetValue(AvailablePathNames[i].header.adapterId, out LUID foundAdapter))
                {
                    if (!newConfig.AvailablePathNames[i].header.adapterId.Equals(foundAdapter))
                    {
                        throw new Exception("Cannot update adapter IDs because the adapter layout has changed.");
                    }

                    continue;
                }

                luidMap.Add(AvailablePathNames[i].header.adapterId, newConfig.AvailablePathNames[i].header.adapterId);
            }

            for (int i = 0; i < AvailablePathNames.Length; i++)
            {
                AvailablePathNames[i].header.adapterId = luidMap[AvailablePathNames[i].header.adapterId];
            }

            for (int i = 0; i < ModeArray.Length; i++)
            {
                ModeArray[i].adapterId = luidMap[ModeArray[i].adapterId];
            }

            for (int i = 0; i < PathArray.Length; i++)
            {
                if (!luidMap.TryGetValue(PathArray[i].sourceInfo.adapterId, out LUID sourceAdapter))
                {
                    // An adapter may be missing from the dictionary if it has no available paths (No connected displays).
                    // There's no good way to map unused adapters from an old config to a new config
                    // so we just keep the adapter ID the same and assume the display API ignores these unused paths.
                    sourceAdapter = PathArray[i].sourceInfo.adapterId;
                    luidMap.Add(sourceAdapter, sourceAdapter);
                }

                if (!luidMap.TryGetValue(PathArray[i].targetInfo.adapterId, out LUID targetAdapter))
                {
                    sourceAdapter = PathArray[i].targetInfo.adapterId;
                    luidMap.Add(targetAdapter, targetAdapter);
                }

                PathArray[i].sourceInfo.adapterId = sourceAdapter;
                PathArray[i].targetInfo.adapterId = targetAdapter;
            }
        }

        public ModeInfo GetPreferredMode(uint displayId)
        {
            int index = GetDisplayIndex(displayId);
            LUID adapterId = PathArray[index].targetInfo.adapterId;
            uint targetId = PathArray[index].targetInfo.id;

            var modeInfo = new DISPLAYCONFIG_TARGET_PREFERRED_MODE()
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER()
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE,
                    adapterId = adapterId,
                    id = targetId
                }
            };
            modeInfo.header.size = (uint)Marshal.SizeOf(modeInfo);
            ReturnCode result = NativeMethods.DisplayConfigGetDeviceInfo(ref modeInfo);
            if (result != ReturnCode.ERROR_SUCCESS)
            {
                throw new Win32Exception((int)result);
            }

            return new ModeInfo(modeInfo);
        }

        public static void EnableDisableDisplay(Cmdlet cmd, uint[] displaysToEnable, uint[] displaysToDisable, bool asClone = false)
        {
            var config = GetConfig(cmd);
            var displaysToEnableSet = new HashSet<int>();
            if (displaysToEnable != null)
            {
                foreach (uint id in displaysToEnable)
                {
                    try
                    {
                        _ = displaysToEnableSet.Add(config.GetDisplayIndex(id));
                    }
                    catch (ArgumentException error)
                    {
                        cmd.WriteError(Utils.GetInvalidDisplayIdError(error, id));
                    }
                }
            }

            var displaysToDisableSet = new HashSet<int>();
            if (displaysToDisable != null)
            {
                foreach (uint id in displaysToDisable)
                {
                    int index;
                    try
                    {
                        index = config.GetDisplayIndex(id);
                    }
                    catch (ArgumentException error)
                    {
                        var record = new ErrorRecord(error, "InvalidDisplayIdToDisable", ErrorCategory.InvalidArgument, id)
                        {
                            ErrorDetails = new ErrorDetails(string.Empty)
                            {
                                RecommendedAction = Utils.CheckDisplayIdRecommendation
                            }
                        };
                        cmd.WriteError(record);
                        continue;
                    }

                    if (displaysToEnableSet.Contains(index))
                    {
                        cmd.ThrowTerminatingError(new ErrorRecord(
                            new ArgumentException($"Conflicting displayId '{id}' to enable and disable"),
                            "ConflictingDisplayIds",
                            ErrorCategory.InvalidArgument,
                            id));
                    }

                    _ = displaysToDisableSet.Add(index);
                }
            }

            // "Enabling" a display adds inactive displays to the desktop or takes cloned displays out of clone mode.
            // This is done by invalidating all the mode indexes and setting a clone group for each display that should be active.
            // Displays with the same desktop position are considered cloned.
            // We keep track of the existing display clones so we don't accidentally stop cloning a display while enabling another.
            // If the AsClone switch is set, we add all the specified displays to Clone group 0.
            // This puts them in one clone group regardless if they are disabled or already in a different clone group.
            var cloneGroupTable = new Dictionary<POINTL, uint>();
            uint cloneGroup = asClone ? (uint)1 : 0;
            var sourceIdTable = new Dictionary<LUID, uint>();
            foreach (int index in config.AvailablePathIndexes)
            {
                config.PathArray[index].targetInfo.modeInfoIdx = API.DisplayConfig.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

                if (config.PathArray[index].flags.HasFlag(PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE))
                {
                    if (displaysToEnableSet.Contains(index))
                    {
                        uint cloneGroupToSet = asClone ? 0 : cloneGroup++;
                        config.PathArray[index].sourceInfo.ResetModeAndSetCloneGroup(cloneGroupToSet);
                    }
                    else if (displaysToDisableSet.Contains(index))
                    {
                        config.PathArray[index].sourceInfo.modeInfoIdx = API.DisplayConfig.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                        config.PathArray[index].targetInfo.modeInfoIdx = API.DisplayConfig.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                        config.PathArray[index].flags &= ~PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE;
                    }
                    else
                    {
                        var desktopPosition = config.ModeArray[config.PathArray[index].sourceInfo.SourceModeInfoIdx].modeInfo.sourceMode.position;
                        if (cloneGroupTable.TryGetValue(desktopPosition, out uint savedCloneGroup))
                        {
                            config.PathArray[index].sourceInfo.ResetModeAndSetCloneGroup(savedCloneGroup);
                        }
                        else
                        {
                            cloneGroupTable.Add(desktopPosition, cloneGroup);
                            config.PathArray[index].sourceInfo.ResetModeAndSetCloneGroup(cloneGroup);
                            cloneGroup++;
                        }
                    }
                }
                else if (displaysToEnableSet.Contains(index))
                {
                    uint cloneGroupToSet = asClone ? 0 : cloneGroup++;
                    config.PathArray[index].sourceInfo.CloneGroupId = cloneGroupToSet;
                    config.PathArray[index].flags |= PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE;
                }

                if (config.PathArray[index].flags.HasFlag(PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE))
                {
                    LUID adapterId = config.PathArray[index].sourceInfo.adapterId;
                    if (!sourceIdTable.ContainsKey(adapterId))
                    {
                        sourceIdTable.Add(adapterId, 0);
                    }

                    config.PathArray[index].sourceInfo.id = sourceIdTable[adapterId]++;
                }
            }

            config.ModeArray = null;
            var flags = SetDisplayConfigFlags.SDC_TOPOLOGY_SUPPLIED |
                SetDisplayConfigFlags.SDC_APPLY |
                SetDisplayConfigFlags.SDC_ALLOW_PATH_ORDER_CHANGES |
                SetDisplayConfigFlags.SDC_VIRTUAL_MODE_AWARE;
            try
            {
                config.ApplyConfig(flags);
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode == 31)
                {
                    cmd.WriteDebug("Failed to apply display config with standard approach. Retrying with alternative.");
                    // CCD sometimes fails to enable displays using the previous method so this is a fallback that uses a different approach
                    // The settings app shows similar behavior when enabling displays.
                    try
                    {
                        RetryWithExistingConfig(cmd, displaysToEnableSet, displaysToDisableSet, asClone);
                    }
                    catch (Win32Exception error2)
                    {
                        cmd.ThrowTerminatingError(new ErrorRecord(error2, "FailedToApplyAlternativeConfig", Utils.GetErrorCategory(error2), null));
                    }
                }
                else
                {
                    cmd.ThrowTerminatingError(new ErrorRecord(error, "FailedToApplyConfig", Utils.GetErrorCategory(error), null));
                }
            }
        }

        private static void RetryWithExistingConfig(Cmdlet cmd, HashSet<int> displaysToEnable, HashSet<int> displaysToDisable, bool asClone)
        {
            // To disable displays we invalidate the source/target modeInfo indexes and remove the Path_Active flag
            // To enable displays we invalidate the source/target modeInfo indexes, add the Path_Active flag
            // we also add a unique clone group for each display (unless AsClone has been specified, which tells us to use one clone group for all the specified displays)
            // Finally we ensure that there is a unique Id for each enabled display on each adapter.
            // When enabling and disabling displays at the same time (for example due to a limited amount of supported outputs by the GPU)
            // we need to reuse the IDs from the disabled displays for the newly added displays.
            // Otherwise, we just need to ensure the IDs are all unique.
            var config = GetConfig(cmd);

            var usedIds = new Dictionary<LUID, HashSet<uint>>();
            var freedIds = new Dictionary<LUID, List<uint>>();
            foreach (int pathIndex in displaysToDisable)
            {
                if (config.PathArray[pathIndex].flags.HasFlag(PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE))
                {
                    if (!freedIds.TryGetValue(config.PathArray[pathIndex].sourceInfo.adapterId, out List<uint> freeIdList))
                    {
                        freeIdList = new List<uint>();
                        freedIds.Add(config.PathArray[pathIndex].sourceInfo.adapterId, freeIdList);
                    }

                    if (!usedIds.TryGetValue(config.PathArray[pathIndex].sourceInfo.adapterId, out HashSet<uint> usedIdSet))
                    {
                        usedIdSet = new HashSet<uint>();
                        usedIds.Add(config.PathArray[pathIndex].sourceInfo.adapterId, usedIdSet);
                    }

                    freeIdList.Add(config.PathArray[pathIndex].sourceInfo.id);
                    _ = usedIdSet.Add(config.PathArray[pathIndex].sourceInfo.id);
                }

                config.PathArray[pathIndex].sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                config.PathArray[pathIndex].targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                config.PathArray[pathIndex].flags &= ~PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE;
            }

            foreach (int index in config.AvailablePathIndexes)
            {
                if (config.PathArray[index].flags.HasFlag(PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE))
                {
                    if (!usedIds.TryGetValue(config.PathArray[index].sourceInfo.adapterId, out HashSet<uint> usedIdSet))
                    {
                        usedIdSet = new HashSet<uint>();
                        usedIds.Add(config.PathArray[index].sourceInfo.adapterId, usedIdSet);
                    }

                    _ = usedIdSet.Add(config.PathArray[index].sourceInfo.id);
                }
            }

            uint cloneGroupToSet = 0;
            foreach (int pathIndex in displaysToEnable)
            {
                if (!config.PathArray[pathIndex].flags.HasFlag(PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE))
                {
                    uint newId;
                    if (freedIds.TryGetValue(config.PathArray[pathIndex].sourceInfo.adapterId, out List<uint> ids) && ids.Count > 0)
                    {
                        newId = ids[ids.Count - 1];
                        ids.RemoveAt(ids.Count - 1);
                    }
                    else
                    {
                        if (!usedIds.TryGetValue(config.PathArray[pathIndex].sourceInfo.adapterId, out HashSet<uint> usedIdSet))
                        {
                            usedIdSet = new HashSet<uint>();
                            usedIds.Add(config.PathArray[pathIndex].sourceInfo.adapterId, usedIdSet);
                        }

                        newId = 0;
                        while (!usedIdSet.Add(newId))
                        {
                            newId++;
                        }
                    }

                    config.PathArray[pathIndex].sourceInfo.id = newId;
                }

                config.PathArray[pathIndex].targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                config.PathArray[pathIndex].sourceInfo.ResetModeAndSetCloneGroup(cloneGroupToSet);
                config.PathArray[pathIndex].flags |= PathInfoFlags.DISPLAYCONFIG_PATH_ACTIVE;
                if (!asClone)
                {
                    cloneGroupToSet++;
                }
            }

            config.ApplyConfig(
                SetDisplayConfigFlags.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
                SetDisplayConfigFlags.SDC_APPLY |
                SetDisplayConfigFlags.SDC_SAVE_TO_DATABASE |
                SetDisplayConfigFlags.SDC_VIRTUAL_MODE_AWARE);
        }
    }
}